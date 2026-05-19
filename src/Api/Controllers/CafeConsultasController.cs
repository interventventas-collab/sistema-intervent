using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Api.Controllers;

/// <summary>
/// Asistente de consultas en lenguaje natural sobre los datos del modulo Cafe.
/// NO usa LLM — pattern matching + SQL contra la base local.
/// Responde preguntas tipicas: "cuanto debe X", "stock Y", "ventas hoy", etc.
/// </summary>
[ApiController]
[Route("api/cafe/consultas")]
[Authorize]
public class CafeConsultasController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeConsultasController(AppDbContext db) { _db = db; }

    private static readonly CultureInfo ARS = CultureInfo.GetCultureInfo("es-AR");

    [HttpPost]
    public async Task<IActionResult> Consultar([FromBody] CafeConsultaRequest req)
    {
        var q = (req.Query ?? "").Trim();
        if (string.IsNullOrEmpty(q)) return Ok(Ayuda("Escribí qué querés consultar."));

        var qLower = q.ToLowerInvariant();
        // Limpiar acentos para que "cuánto" matchee con "cuanto"
        qLower = SinAcentos(qLower);

        // Ordenado por especificidad — el primer match gana.
        if (TryDeudaCliente(qLower, out var nombre)) return Ok(await DeudaClienteAsync(nombre));
        if (TryTopProductosCliente(qLower, out var clienteTop)) return Ok(await TopProductosClienteAsync(clienteTop));
        if (TryStockProducto(qLower, out var prod)) return Ok(await StockProductoAsync(prod));
        if (Regex.IsMatch(qLower, @"\b(producto|sku)\b")) return Ok(await ProductoFichaAsync(ExtractAfter(qLower, new[] { "producto sku", "producto", "sku" })));
        if (TryProveedor(qLower, out var prov)) return Ok(await ProveedorFichaAsync(prov));
        if (TryClientesConDeuda(qLower)) return Ok(await ClientesConDeudaAsync());
        if (TryComprasPendientes(qLower)) return Ok(await ComprasEstadoAsync());
        if (TryVentas(qLower, out var periodo)) return Ok(await VentasPeriodoAsync(periodo));

        return Ok(Ayuda("No entendí la consulta. Probá con alguno de estos ejemplos:"));
    }

    // ============================================================
    // Pattern matchers
    // ============================================================

    /// <summary>"cuanto debe X" / "saldo X" / "cuanto me debe X" / "deuda X"</summary>
    private static bool TryDeudaCliente(string q, out string nombre)
    {
        var patrones = new[]
        {
            @"^cuanto\s+(me\s+)?debe\s+(?<n>.+)$",
            @"^saldo\s+(?<n>.+)$",
            @"^deuda\s+(?<n>.+)$",
            @"^que\s+debe\s+(?<n>.+)$"
        };
        foreach (var p in patrones)
        {
            var m = Regex.Match(q, p);
            if (m.Success) { nombre = m.Groups["n"].Value.Trim(); return true; }
        }
        nombre = ""; return false;
    }

    /// <summary>"stock X" / "tengo de X" / "stock de X"</summary>
    private static bool TryStockProducto(string q, out string nombre)
    {
        var m = Regex.Match(q, @"^(stock|tengo)\s+(de\s+)?(?<n>.+)$");
        if (m.Success) { nombre = m.Groups["n"].Value.Trim(); return true; }
        nombre = ""; return false;
    }

    /// <summary>"top productos X" / "que compra X" / "que mas compra X"</summary>
    private static bool TryTopProductosCliente(string q, out string nombre)
    {
        var patrones = new[]
        {
            @"^top\s+productos\s+(?<n>.+)$",
            @"^que\s+(mas\s+)?compra\s+(?<n>.+)$",
            @"^productos\s+de\s+(?<n>.+)$"
        };
        foreach (var p in patrones)
        {
            var m = Regex.Match(q, p);
            if (m.Success) { nombre = m.Groups["n"].Value.Trim(); return true; }
        }
        nombre = ""; return false;
    }

    private static bool TryProveedor(string q, out string nombre)
    {
        var m = Regex.Match(q, @"^proveedor\s+(?<n>.+)$");
        if (m.Success) { nombre = m.Groups["n"].Value.Trim(); return true; }
        nombre = ""; return false;
    }

    private static bool TryClientesConDeuda(string q)
        => Regex.IsMatch(q, @"\bclientes?\s+(con\s+)?deuda\b") || q == "deudores";

    private static bool TryComprasPendientes(string q)
        => Regex.IsMatch(q, @"\bcompras\s+(pendientes|borrador|sin\s+confirmar|impagas|impagos)\b");

    /// <summary>"ventas hoy" / "ventas semana" / "ventas mes" / "ventas mayo" / "ventas mes pasado"</summary>
    private static bool TryVentas(string q, out string periodo)
    {
        var m = Regex.Match(q, @"^ventas(\s+del?)?\s+(?<p>.+)$");
        if (m.Success) { periodo = m.Groups["p"].Value.Trim(); return true; }
        if (q == "ventas") { periodo = "hoy"; return true; }
        periodo = ""; return false;
    }

    // ============================================================
    // Resolvers
    // ============================================================

    private async Task<CafeConsultaResultDto> DeudaClienteAsync(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return Ayuda("Decime el nombre del cliente. Ej: 'cuanto debe Cuchiflito'");
        var clientes = await BuscarClientesAsync(nombre, 5);
        if (clientes.Count == 0)
            return Vacio($"No encontré ningún cliente con '{nombre}'.");
        if (clientes.Count > 1)
        {
            // Más de un match — mostrar opciones
            return new CafeConsultaResultDto
            {
                Tipo = "tabla",
                Titulo = $"Encontré {clientes.Count} clientes con '{nombre}'",
                Subtitulo = "Refiná la búsqueda con el nombre completo:",
                Columnas = new() { "Código", "Nombre", "Tipo", "CUIT" },
                Filas = clientes.Select(c => new Dictionary<string, string>
                {
                    ["Código"] = c.Codigo ?? "—",
                    ["Nombre"] = c.Nombre,
                    ["Tipo"] = c.Tipo,
                    ["CUIT"] = c.Cuit ?? "—"
                }).ToList()
            };
        }

        var cli = clientes[0];
        var impagas = await _db.CafeVentas
            .Where(v => v.ClienteId == cli.Id && v.Estado == "emitido" && !v.IsPaid)
            .OrderBy(v => v.Fecha)
            .ToListAsync();

        if (impagas.Count == 0)
            return Vacio($"✓ {cli.Nombre} no tiene comprobantes impagos.");

        // Monto real cobrable (con IVA si es factura ARCA, sino Total neto).
        decimal Cobrable(CafeVenta v) => (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
        return new CafeConsultaResultDto
        {
            Tipo = "tabla",
            Titulo = $"💰 {cli.Nombre} debe {Money(impagas.Sum(Cobrable))}",
            Subtitulo = $"{impagas.Count} comprobante{(impagas.Count == 1 ? "" : "s")} impago{(impagas.Count == 1 ? "" : "s")}",
            Total = Money(impagas.Sum(Cobrable)),
            Columnas = new() { "Comprobante", "Fecha", "Total" },
            Filas = impagas.Select(v => new Dictionary<string, string>
            {
                ["Comprobante"] = v.Numero,
                ["Fecha"] = v.Fecha.ToString("dd/MM/yyyy"),
                ["Total"] = Money(Cobrable(v))
            }).ToList()
        };
    }

    private async Task<CafeConsultaResultDto> StockProductoAsync(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return Ayuda("Decime el producto/marca. Ej: 'stock café Frikaf'");
        // Split por palabras: cada palabra debe matchear (AND).
        // Palabras especiales 'cafe' / 'cafés' / 'otros' / 'insumos' se interpretan como filtro de categoria.
        var palabras = nombre.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var query = _db.CafeProductos.Include(p => p.MarcaNav).Where(p => p.IsActive);
        foreach (var palRaw in palabras)
        {
            var palNorm = SinAcentos(palRaw.ToLowerInvariant());
            if (palNorm is "cafe" or "cafes" or "café" or "cafés")
            {
                query = query.Where(x => x.Categoria == "CAFE");
                continue;
            }
            if (palNorm is "otros" or "otro" or "insumo" or "insumos")
            {
                query = query.Where(x => x.Categoria == "OTROS");
                continue;
            }
            var p = palRaw;  // capture
            query = query.Where(x =>
                x.Nombre.Contains(p) ||
                (x.Sku != null && x.Sku.Contains(p)) ||
                (x.Marca != null && x.Marca.Contains(p)) ||
                (x.MarcaNav != null && x.MarcaNav.Nombre.Contains(p)) ||
                (x.Barcode != null && x.Barcode.Contains(p)));
        }
        var prods = await query.OrderBy(p => p.Nombre).Take(50).ToListAsync();

        if (prods.Count == 0)
            return Vacio($"No encontré productos con '{nombre}'.");

        return new CafeConsultaResultDto
        {
            Tipo = "tabla",
            Titulo = $"📦 Stock de '{nombre}' — {prods.Count} producto{(prods.Count == 1 ? "" : "s")}",
            Columnas = new() { "SKU", "Producto", "Marca", "Categoría", "Stock", "Costo", "PVP" },
            Filas = prods.Select(p =>
            {
                var stock = p.Categoria == "CAFE"
                    ? (p.StockGramos / 1000m).ToString("N2", ARS) + " kg"
                    : $"{p.StockUnidades} u.";
                var pvp = p.Pvp2.HasValue ? Money(p.Pvp2.Value) : "—";
                return new Dictionary<string, string>
                {
                    ["SKU"] = p.Sku ?? "—",
                    ["Producto"] = p.Nombre,
                    ["Marca"] = p.MarcaNav?.Nombre ?? p.Marca ?? "—",
                    ["Categoría"] = p.Categoria,
                    ["Stock"] = stock,
                    ["Costo"] = Money(p.Costo),
                    ["PVP"] = pvp
                };
            }).ToList()
        };
    }

    private async Task<CafeConsultaResultDto> ProductoFichaAsync(string criterio)
    {
        if (string.IsNullOrWhiteSpace(criterio)) return Ayuda("Decime SKU o nombre. Ej: 'producto F1' o 'sku C8733BL'");
        var p = await _db.CafeProductos
            .Include(x => x.MarcaNav).ThenInclude(m => m!.ProveedorNav)
            .Include(x => x.OemNav)
            .FirstOrDefaultAsync(x => x.Sku == criterio || x.Sku == criterio.ToUpperInvariant() || x.Nombre == criterio);
        if (p is null)
        {
            // Fallback: contains
            p = await _db.CafeProductos
                .Include(x => x.MarcaNav).ThenInclude(m => m!.ProveedorNav)
                .Include(x => x.OemNav)
                .Where(x => x.Sku!.Contains(criterio) || x.Nombre.Contains(criterio))
                .FirstOrDefaultAsync();
        }
        if (p is null) return Vacio($"No encontré producto con '{criterio}'.");

        var stockTxt = p.Categoria == "CAFE"
            ? (p.StockGramos / 1000m).ToString("N2", ARS) + " kg"
            : $"{p.StockUnidades} u.";
        var datos = new List<KeyValuePair<string, string>>
        {
            new("SKU", p.Sku ?? "—"),
            new("Nombre", p.Nombre),
            new("Categoría", p.Categoria),
            new("Marca", p.MarcaNav?.Nombre ?? p.Marca ?? "—"),
            new("Proveedor", p.MarcaNav?.ProveedorNav?.Nombre ?? "(sin asignar)"),
            new("Stock actual", stockTxt),
            new("Costo", Money(p.Costo))
        };
        if (p.Categoria == "CAFE")
        {
            datos.Add(new("PVP BAR (kg)", p.Pvp1.HasValue ? Money(p.Pvp1.Value) : "—"));
            datos.Add(new("PVP Comercial (kg)", p.Pvp2.HasValue ? Money(p.Pvp2.Value) : "—"));
        }
        else
        {
            datos.Add(new("PVP Comercial", p.Pvp2.HasValue ? Money(p.Pvp2.Value) : "—"));
            if (p.BarPctSobreCosto.HasValue)
            {
                var precioBar = p.Costo * (1m + p.BarPctSobreCosto.Value / 100m);
                datos.Add(new("PVP BAR", $"{Money(precioBar)} (costo + {p.BarPctSobreCosto.Value:0.##}%)"));
            }
            else { datos.Add(new("PVP BAR", "= PVP Comercial")); }
            if (p.UxB.HasValue) datos.Add(new("Unidades por bulto", p.UxB.Value.ToString()));
        }
        if (!string.IsNullOrEmpty(p.Barcode)) datos.Add(new("Código de barras", p.Barcode));
        if (p.OemNav is not null) datos.Add(new("OEM origen", p.OemNav.Codigo));
        datos.Add(new("Estado", p.IsActive ? "Activo" : "Inactivo"));

        return new CafeConsultaResultDto
        {
            Tipo = "ficha",
            Titulo = $"🔎 {p.Nombre}",
            Subtitulo = p.Sku ?? "",
            Datos = datos
        };
    }

    private async Task<CafeConsultaResultDto> ProveedorFichaAsync(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return Ayuda("Decime el proveedor. Ej: 'proveedor Colombraro'");
        var prov = await _db.CafeProveedores
            .Where(x => x.Nombre.Contains(nombre) || (x.Cuit != null && x.Cuit == nombre))
            .OrderBy(x => x.Nombre)
            .FirstOrDefaultAsync();
        if (prov is null) return Vacio($"No encontré proveedor con '{nombre}'.");

        var marcas = await _db.CafeMarcas.Where(m => m.ProveedorId == prov.Id).Select(m => m.Nombre).ToListAsync();
        var compras = await _db.CafeCompras.Where(c => c.ProveedorId == prov.Id && c.Estado != "ANULADA").CountAsync();
        var totalComprado = await _db.CafeCompras.Where(c => c.ProveedorId == prov.Id && c.Estado != "ANULADA").SumAsync(c => (decimal?)c.Total) ?? 0m;

        var datos = new List<KeyValuePair<string, string>>
        {
            new("Nombre", prov.Nombre),
            new("CUIT", prov.Cuit ?? "—"),
            new("Cat. impositiva", prov.CategoriaImpositiva ?? "—"),
            new("Teléfono", prov.Telefono ?? "—"),
            new("Email", prov.Email ?? "—"),
            new("Web", prov.Web ?? "—"),
            new("Dirección", string.Join(", ", new[] { prov.Direccion, prov.Ciudad, prov.Provincia, prov.CodigoPostal }.Where(s => !string.IsNullOrEmpty(s)))),
            new("Marcas que vende", marcas.Count > 0 ? string.Join(", ", marcas) : "(ninguna asignada)"),
            new("Compras", $"{compras} (total: {Money(totalComprado)})"),
            new("Estado", prov.IsActive ? "Activo" : "Inactivo")
        };
        return new CafeConsultaResultDto
        {
            Tipo = "ficha",
            Titulo = $"🏭 {prov.Nombre}",
            Datos = datos
        };
    }

    private async Task<CafeConsultaResultDto> ClientesConDeudaAsync()
    {
        var data = await _db.CafeVentas
            .Where(v => v.Estado == "emitido" && !v.IsPaid && v.ClienteId != null)
            .GroupBy(v => v.ClienteId!.Value)
            .Select(g => new { ClienteId = g.Key, N = g.Count(), Total = g.Sum(x => x.Total) })
            .OrderByDescending(x => x.Total)
            .Take(50)
            .ToListAsync();

        if (data.Count == 0) return Vacio("✓ No hay clientes con deuda.");
        var ids = data.Select(x => x.ClienteId).ToList();
        var clientes = await _db.CafeClientes.Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id);

        return new CafeConsultaResultDto
        {
            Tipo = "tabla",
            Titulo = $"💰 {data.Count} cliente{(data.Count == 1 ? "" : "s")} con deuda",
            Total = Money(data.Sum(x => x.Total)),
            Columnas = new() { "Código", "Cliente", "Tipo", "Comprobantes", "Total" },
            Filas = data.Select(x =>
            {
                clientes.TryGetValue(x.ClienteId, out var c);
                return new Dictionary<string, string>
                {
                    ["Código"] = c?.Codigo ?? "—",
                    ["Cliente"] = c?.Nombre ?? "(eliminado)",
                    ["Tipo"] = c?.Tipo == "BAR" ? "🍺 BAR" : "🏢 Comercial",
                    ["Comprobantes"] = x.N.ToString(),
                    ["Total"] = Money(x.Total)
                };
            }).ToList()
        };
    }

    private async Task<CafeConsultaResultDto> ComprasEstadoAsync()
    {
        var compras = await _db.CafeCompras
            .Include(c => c.ProveedorNav)
            .Where(c => c.Estado == "BORRADOR")
            .OrderBy(c => c.Fecha)
            .Take(100)
            .ToListAsync();

        if (compras.Count == 0) return Vacio("✓ No hay compras pendientes (en borrador).");

        return new CafeConsultaResultDto
        {
            Tipo = "tabla",
            Titulo = $"📥 {compras.Count} compra{(compras.Count == 1 ? "" : "s")} en borrador",
            Total = Money(compras.Sum(c => c.Total)),
            Columnas = new() { "Número", "Fecha", "Proveedor", "Total" },
            Filas = compras.Select(c => new Dictionary<string, string>
            {
                ["Número"] = c.Numero,
                ["Fecha"] = c.Fecha.ToString("dd/MM/yyyy"),
                ["Proveedor"] = c.ProveedorNav?.Nombre ?? c.ProveedorNombreSnapshot ?? "—",
                ["Total"] = Money(c.Total)
            }).ToList()
        };
    }

    private async Task<CafeConsultaResultDto> VentasPeriodoAsync(string periodo)
    {
        var (desde, hasta, label) = ResolverPeriodo(periodo);
        var ventas = await _db.CafeVentas
            .Include(v => v.ClienteNav)
            .Where(v => v.Estado == "emitido" && v.Fecha >= desde && v.Fecha <= hasta)
            .OrderByDescending(v => v.Fecha).ThenByDescending(v => v.Id)
            .Take(200)
            .ToListAsync();

        if (ventas.Count == 0) return Vacio($"No hay ventas en {label}.");

        // Monto real (con IVA si es factura ARCA, sino Total neto).
        decimal Cobrable(CafeVenta v) => (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
        return new CafeConsultaResultDto
        {
            Tipo = "tabla",
            Titulo = $"💵 Ventas {label}",
            Subtitulo = $"{ventas.Count} comprobante{(ventas.Count == 1 ? "" : "s")} · {desde:dd/MM/yyyy} a {hasta:dd/MM/yyyy}",
            Total = Money(ventas.Sum(Cobrable)),
            Columnas = new() { "Número", "Fecha", "Cliente", "Total", "Pagada" },
            Filas = ventas.Select(v => new Dictionary<string, string>
            {
                ["Número"] = v.Numero,
                ["Fecha"] = v.Fecha.ToString("dd/MM/yyyy"),
                ["Cliente"] = v.ClienteNombreSnapshot ?? v.ClienteNav?.Nombre ?? "Consumidor final",
                ["Total"] = Money(Cobrable(v)),
                ["Pagada"] = v.IsPaid ? "✓" : "—"
            }).ToList()
        };
    }

    private async Task<CafeConsultaResultDto> TopProductosClienteAsync(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return Ayuda("Decime el cliente. Ej: 'top productos Cuchiflito'");
        var clientes = await BuscarClientesAsync(nombre, 5);
        if (clientes.Count == 0) return Vacio($"No encontré cliente con '{nombre}'.");
        if (clientes.Count > 1)
        {
            return new CafeConsultaResultDto
            {
                Tipo = "tabla",
                Titulo = $"Encontré {clientes.Count} clientes con '{nombre}'",
                Subtitulo = "Probá con el nombre completo:",
                Columnas = new() { "Código", "Nombre", "Tipo" },
                Filas = clientes.Select(c => new Dictionary<string, string>
                {
                    ["Código"] = c.Codigo ?? "—",
                    ["Nombre"] = c.Nombre,
                    ["Tipo"] = c.Tipo
                }).ToList()
            };
        }
        var cli = clientes[0];
        var top = await _db.CafeVentaItems
            .Where(i => i.VentaNav != null && i.VentaNav.ClienteId == cli.Id && i.VentaNav.Estado != "anulado")
            .GroupBy(i => new { i.ProductoNombreSnapshot, i.Formato, i.ProductoId })
            .Select(g => new
            {
                Producto = g.Key.ProductoNombreSnapshot,
                Formato = g.Key.Formato,
                Veces = g.Select(x => x.VentaId).Distinct().Count(),
                Cantidad = g.Sum(x => x.Cantidad),
                Ultima = g.Max(x => x.VentaNav!.Fecha)
            })
            .OrderByDescending(x => x.Veces)
            .Take(15)
            .ToListAsync();
        if (top.Count == 0) return Vacio($"{cli.Nombre} todavía no compró nada.");

        return new CafeConsultaResultDto
        {
            Tipo = "tabla",
            Titulo = $"⭐ Top productos de {cli.Nombre}",
            Columnas = new() { "Producto", "Formato", "Veces", "Cant. total", "Última compra" },
            Filas = top.Select(t => new Dictionary<string, string>
            {
                ["Producto"] = t.Producto,
                ["Formato"] = FormatoLabel(t.Formato),
                ["Veces"] = t.Veces.ToString(),
                ["Cant. total"] = t.Cantidad.ToString(),
                ["Última compra"] = t.Ultima.ToString("dd/MM/yyyy")
            }).ToList()
        };
    }

    // ============================================================
    // Helpers
    // ============================================================

    private async Task<List<Models.CafeCliente>> BuscarClientesAsync(string nombre, int limit)
    {
        var n = nombre.Trim();
        // Match exacto (codigo o nombre completo) primero, despues contains.
        var exactos = await _db.CafeClientes
            .Where(c => c.IsActive && (c.Codigo == n || c.Nombre == n || (c.Cuit != null && c.Cuit == n)))
            .ToListAsync();
        if (exactos.Count > 0) return exactos;
        return await _db.CafeClientes
            .Where(c => c.IsActive && c.Nombre.Contains(n))
            .OrderBy(c => c.Nombre)
            .Take(limit)
            .ToListAsync();
    }

    private static (DateTime desde, DateTime hasta, string label) ResolverPeriodo(string periodo)
    {
        var hoy = DateTime.UtcNow.Date;
        var p = periodo.ToLowerInvariant().Trim();

        if (p == "hoy" || p == "del dia") return (hoy, hoy.AddDays(1).AddSeconds(-1), "de hoy");
        if (p == "ayer") return (hoy.AddDays(-1), hoy.AddSeconds(-1), "de ayer");
        if (p == "semana" || p == "esta semana" || p == "de la semana")
        {
            var lunes = hoy.AddDays(-(int)hoy.DayOfWeek + (hoy.DayOfWeek == DayOfWeek.Sunday ? -6 : 1));
            return (lunes, hoy.AddDays(1).AddSeconds(-1), "de esta semana");
        }
        if (p == "mes" || p == "este mes" || p == "del mes")
        {
            var primer = new DateTime(hoy.Year, hoy.Month, 1);
            return (primer, hoy.AddDays(1).AddSeconds(-1), "de este mes");
        }
        if (p == "mes pasado" || p == "del mes pasado")
        {
            var primer = new DateTime(hoy.Year, hoy.Month, 1).AddMonths(-1);
            var ultimo = new DateTime(hoy.Year, hoy.Month, 1).AddSeconds(-1);
            return (primer, ultimo, "del mes pasado");
        }
        if (p == "anio" || p == "ano" || p == "este ano" || p == "este anio" || p == "del ano" || p == "del anio")
        {
            return (new DateTime(hoy.Year, 1, 1), hoy.AddDays(1).AddSeconds(-1), "de este año");
        }
        // Mes por nombre
        var meses = new Dictionary<string, int>
        {
            ["enero"] = 1, ["febrero"] = 2, ["marzo"] = 3, ["abril"] = 4, ["mayo"] = 5, ["junio"] = 6,
            ["julio"] = 7, ["agosto"] = 8, ["septiembre"] = 9, ["setiembre"] = 9, ["octubre"] = 10, ["noviembre"] = 11, ["diciembre"] = 12
        };
        foreach (var (nombre, mes) in meses)
        {
            if (p.Contains(nombre))
            {
                var anio = hoy.Year;
                var primer = new DateTime(anio, mes, 1);
                var ultimo = primer.AddMonths(1).AddSeconds(-1);
                if (primer > hoy) { anio--; primer = new DateTime(anio, mes, 1); ultimo = primer.AddMonths(1).AddSeconds(-1); }
                return (primer, ultimo, $"de {nombre} {anio}");
            }
        }
        // Default: hoy
        return (hoy, hoy.AddDays(1).AddSeconds(-1), "de hoy");
    }

    private static string ExtractAfter(string q, string[] prefixes)
    {
        foreach (var p in prefixes.OrderByDescending(x => x.Length))
            if (q.StartsWith(p + " ")) return q.Substring(p.Length + 1).Trim();
        return q;
    }

    private static string Money(decimal v) => "$" + v.ToString("N2", ARS);

    private static string FormatoLabel(string f) => f switch
    {
        "1KG" => "1 kg",
        "MEDIO" => "1/2 kg",
        "CUARTO" => "1/4 kg",
        "UNIT" => "u.",
        _ => f
    };

    private static string SinAcentos(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.Normalize(System.Text.NormalizationForm.FormD))
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    private static CafeConsultaResultDto Vacio(string mensaje) => new()
    {
        Tipo = "vacio",
        Titulo = mensaje
    };

    private static CafeConsultaResultDto Ayuda(string titulo) => new()
    {
        Tipo = "ayuda",
        Titulo = titulo,
        Ayuda = new()
        {
            "cuanto debe Cuchiflito",
            "stock café Frikaf",
            "ventas hoy",
            "ventas mes",
            "ventas mayo",
            "compras pendientes",
            "clientes con deuda",
            "top productos Cuchiflito",
            "producto F1",
            "proveedor Colombraro"
        }
    };
}
