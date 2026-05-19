using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/clientes")]
[Authorize]
public class CafeClientesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly GoogleMapsLinkResolverService _mapsResolver;
    private static readonly string[] TiposValidos = { "BAR", "OTRO" };

    public CafeClientesController(AppDbContext db, GoogleMapsLinkResolverService mapsResolver)
    {
        _db = db;
        _mapsResolver = mapsResolver;
    }

    private static CafeClienteDto Map(CafeCliente c) => new(
        c.Id, c.Codigo, c.Nombre, c.RazonSocial, c.Tipo,
        c.Cuit, c.Telefono, c.Email,
        c.Direccion, c.Localidad, c.Ciudad, c.Cp,
        c.CondicionIvaDefault,
        c.DomicilioEntrega,
        c.Notas, c.ComentariosComprobante,
        c.IsActive, c.CreatedAt, c.UpdatedAt,
        c.CodigoInterno, c.MapeoLink,
        c.MapeoLat, c.MapeoLng);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var list = await _db.CafeClientes.OrderBy(c => c.Nombre).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        return Ok(Map(c));
    }

    public record MovimientoCuentaDto(
        DateTime Fecha, string Tipo, string Numero, decimal Debe, decimal Haber, decimal SaldoAcumulado, string? Detalle);
    public record EstadoCuentaDto(int ClienteId, string ClienteNombre, decimal Saldo, List<MovimientoCuentaDto> Movimientos);

    /// <summary>
    /// Estado de cuenta del cliente: lista cronologica de ventas (debe) y cobranzas (haber)
    /// + saldo final. Para la ficha de cliente "Tab Cuenta corriente".
    /// </summary>
    [HttpGet("{id:int}/estado-cuenta")]
    public async Task<IActionResult> EstadoCuenta(int id)
    {
        var cliente = await _db.CafeClientes.FindAsync(id);
        if (cliente is null) return NotFound();

        // Ventas vigentes del cliente. Para facturas A/B/C con IVA, el debe es el TOTAL CON IVA
        // (ArcaImpTotal), no el neto (Total) — sino la cuenta corriente queda corta el IVA.
        var ventas = await _db.CafeVentas
            .Where(v => v.ClienteId == id && v.Estado != "anulado")
            .Select(v => new { v.Id, v.Fecha, v.Numero, v.Total, v.ArcaImpTotal })
            .ToListAsync();
        // Cobranzas vigentes y sus retenciones
        var cobranzas = await _db.CafeCobranzas
            .Where(c => c.ClienteId == id && c.Estado == "VIGENTE")
            .Select(c => new { c.Id, c.Fecha, c.Numero, c.Total, c.Retenciones })
            .ToListAsync();
        // Comprobantes de cobranzas (para saber a que venta se aplicaron, opcional para detalle)
        // Por simplicidad la cobranza la mostramos como un haber total (Total + Retenciones).

        var movs = new List<(DateTime fecha, string tipo, string num, decimal debe, decimal haber, string? det)>();
        foreach (var v in ventas)
        {
            var debe = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
            movs.Add((v.Fecha, "Venta", v.Numero ?? $"#{v.Id}", debe, 0m, null));
        }
        foreach (var c in cobranzas)
            movs.Add((c.Fecha, "Cobranza", c.Numero, 0m, c.Total + c.Retenciones,
                c.Retenciones > 0 ? $"(incluye ${c.Retenciones:N2} retenciones)" : null));

        movs = movs.OrderBy(x => x.fecha).ToList();
        decimal acum = 0m;
        var result = new List<MovimientoCuentaDto>(movs.Count);
        foreach (var m in movs)
        {
            acum += m.debe - m.haber;
            result.Add(new MovimientoCuentaDto(m.fecha, m.tipo, m.num, m.debe, m.haber, acum, m.det));
        }
        return Ok(new EstadoCuentaDto(id, cliente.Nombre, acum, result));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeClienteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });
        var tipo = NormTipo(req.Tipo);
        var c = new CafeCliente
        {
            Codigo = await GenerarCodigoAsync(),
            Nombre = req.Nombre.Trim(),
            RazonSocial = Norm(req.RazonSocial),
            Tipo = tipo,
            Cuit = Norm(req.Cuit),
            Telefono = Norm(req.Telefono),
            Email = Norm(req.Email),
            Direccion = Norm(req.Direccion),
            Localidad = Norm(req.Localidad),
            Ciudad = Norm(req.Ciudad),
            Cp = Norm(req.Cp),
            CondicionIvaDefault = Norm(req.CondicionIvaDefault),
            DomicilioEntrega = Norm(req.DomicilioEntrega),
            Notas = Norm(req.Notas),
            ComentariosComprobante = Norm(req.ComentariosComprobante),
            MapeoLink = Norm(req.MapeoLink),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        // Si vino MapeoLink, intentamos resolverlo y guardar las coords automáticamente.
        if (!string.IsNullOrEmpty(c.MapeoLink))
        {
            var coords = await _mapsResolver.TryResolverCoordenadasAsync(c.MapeoLink);
            if (coords.HasValue) { c.MapeoLat = coords.Value.lat; c.MapeoLng = coords.Value.lng; }
        }
        _db.CafeClientes.Add(c);
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    /// <summary>
    /// Devuelve el siguiente codigo secuencial. Pad a 4 digitos para los primeros 9999.
    /// Si ya existe alguno >= 9999 (improbable pero por las dudas), arranca con 5 digitos.
    /// </summary>
    private async Task<string> GenerarCodigoAsync()
    {
        var maxNum = await _db.CafeClientes
            .Where(c => c.Codigo != null)
            .Select(c => c.Codigo!)
            .ToListAsync();
        int max = 0;
        foreach (var s in maxNum)
        {
            if (int.TryParse(s, out var n) && n > max) max = n;
        }
        var siguiente = max + 1;
        return siguiente < 10000 ? siguiente.ToString("D4") : siguiente.ToString();
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeClienteRequest req)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede ser vacio" });
            c.Nombre = req.Nombre.Trim();
        }
        if (req.RazonSocial is not null) c.RazonSocial = Norm(req.RazonSocial);
        if (req.Tipo is not null) c.Tipo = NormTipo(req.Tipo);
        if (req.Cuit is not null) c.Cuit = Norm(req.Cuit);
        if (req.Telefono is not null) c.Telefono = Norm(req.Telefono);
        if (req.Email is not null) c.Email = Norm(req.Email);
        if (req.Direccion is not null) c.Direccion = Norm(req.Direccion);
        if (req.Localidad is not null) c.Localidad = Norm(req.Localidad);
        if (req.Ciudad is not null) c.Ciudad = Norm(req.Ciudad);
        if (req.Cp is not null) c.Cp = Norm(req.Cp);
        if (req.CondicionIvaDefault is not null) c.CondicionIvaDefault = Norm(req.CondicionIvaDefault);
        if (req.DomicilioEntrega is not null) c.DomicilioEntrega = Norm(req.DomicilioEntrega);
        if (req.Notas is not null) c.Notas = Norm(req.Notas);
        if (req.ComentariosComprobante is not null) c.ComentariosComprobante = Norm(req.ComentariosComprobante);
        if (req.IsActive.HasValue) c.IsActive = req.IsActive.Value;
        // MapeoLink: si vino, actualizo. Si vino ClearMapeoLink, lo vacío.
        // Si el link cambió (o se agregó por primera vez), intentamos extraer coords del link de Google Maps.
        var linkPrevio = c.MapeoLink;
        if (req.MapeoLink is not null) c.MapeoLink = Norm(req.MapeoLink);
        else if (req.ClearMapeoLink) { c.MapeoLink = null; c.MapeoLat = null; c.MapeoLng = null; }
        if (!string.IsNullOrEmpty(c.MapeoLink) && c.MapeoLink != linkPrevio)
        {
            var coords = await _mapsResolver.TryResolverCoordenadasAsync(c.MapeoLink);
            if (coords.HasValue)
            {
                c.MapeoLat = coords.Value.lat;
                c.MapeoLng = coords.Value.lng;
            }
            // Si no se pudo resolver, mantenemos las coords previas (o null si nunca tuvo).
            // El usuario puede usar el botón "Re-extraer coords" después.
        }
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    /// <summary>Vuelve a resolver el MapeoLink del cliente y actualiza MapeoLat/Lng.
    /// Útil si la extracción inicial falló (Google rate-limit, formato extraño, etc.).</summary>
    [HttpPost("{id:int}/reextraer-coords")]
    public async Task<IActionResult> ReExtraerCoords(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        if (string.IsNullOrEmpty(c.MapeoLink))
            return BadRequest(new { error = "El cliente no tiene MapeoLink cargado." });
        var coords = await _mapsResolver.TryResolverCoordenadasAsync(c.MapeoLink);
        if (!coords.HasValue)
            return BadRequest(new { error = "No se pudieron extraer coordenadas del link. Probá con otro link o ingresá las coordenadas manualmente." });
        c.MapeoLat = coords.Value.lat;
        c.MapeoLng = coords.Value.lng;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    /// <summary>Asigna un código interno correlativo al cliente. Si ya tiene uno, lo respeta.
    /// El correlativo se calcula como MAX(CodigoInterno actual) + 1.</summary>
    [HttpPost("{id:int}/asignar-codigo-interno")]
    public async Task<IActionResult> AsignarCodigoInterno(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        if (c.CodigoInterno.HasValue)
            return Ok(Map(c));   // ya tenía uno, lo respetamos
        var maxActual = await _db.CafeClientes
            .Where(x => x.CodigoInterno != null)
            .MaxAsync(x => (int?)x.CodigoInterno) ?? 0;
        c.CodigoInterno = maxActual + 1;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    /// <summary>Saca el código interno (vuelve a null). Útil si el operador lo asignó por error.</summary>
    [HttpDelete("{id:int}/codigo-interno")]
    public async Task<IActionResult> QuitarCodigoInterno(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        c.CodigoInterno = null;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        _db.CafeClientes.Remove(c);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private static string NormTipo(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "OTRO";
        var v = t.Trim().ToUpperInvariant();
        return TiposValidos.Contains(v) ? v : "OTRO";
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ============================================================
    // Saldos pendientes — vista consolidada por cliente
    // ============================================================

    public record ClienteSaldoPendienteDto(
        int ClienteId, string Nombre, string? Tipo, string? Telefono, string? MapeoLink,
        int? CodigoInterno,
        int CantidadVentasPendientes,
        decimal SaldoPendiente,
        DateTime FechaMasAntigua, int DiasMasAntigua,
        bool TieneSaldoMigracion,
        /// <summary>Saldo de comprobantes tipo X y PRO (no fiscales). Default 0 si no hay.</summary>
        decimal SaldoCotizacion = 0m,
        /// <summary>Saldo de comprobantes tipo FA, FB, FC (con CAE de ARCA, fiscales). Default 0 si no hay.</summary>
        decimal SaldoFactura = 0m);

    /// <summary>Lista TODOS los clientes con saldo pendiente (deudores), agrupados.
    /// Saldo pendiente = SUM(ventas emitidas).Total - SUM(cobranzas vigentes asignadas a esas ventas).
    /// Las ventas creadas como "saldo de migración" del sistema viejo se incluyen igual (son ventas tipo X).
    /// Solo devuelve clientes con saldo > 0.</summary>
    // ─── Token publico del panel de saldos ─── (mismo patron que nominas/panel)
    private const string ClientesPanelTokenKey = "clientes.panel.public_token";

    private async Task<string> GetOrCreateClientesPanelTokenAsync()
    {
        var existing = await _db.AppSettings.FindAsync(ClientesPanelTokenKey);
        if (existing is not null && !string.IsNullOrEmpty(existing.Value)) return existing.Value;
        var token = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');
        if (existing is null) _db.AppSettings.Add(new AppSetting { Key = ClientesPanelTokenKey, Value = token });
        else existing.Value = token;
        await _db.SaveChangesAsync();
        return token;
    }

    [HttpGet("saldos-pendientes/public-token")]
    public async Task<IActionResult> GetClientesPanelPublicToken()
    {
        var token = await GetOrCreateClientesPanelTokenAsync();
        return Ok(new { token });
    }

    [HttpPost("saldos-pendientes/public-token/regenerate")]
    public async Task<IActionResult> RegenerateClientesPanelPublicToken()
    {
        var existing = await _db.AppSettings.FindAsync(ClientesPanelTokenKey);
        var nuevo = Convert.ToBase64String(Guid.NewGuid().ToByteArray())
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');
        if (existing is null) _db.AppSettings.Add(new AppSetting { Key = ClientesPanelTokenKey, Value = nuevo });
        else existing.Value = nuevo;
        await _db.SaveChangesAsync();
        return Ok(new { token = nuevo });
    }

    [HttpGet("saldos-pendientes/publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSaldosPendientesPublic(string token)
    {
        var saved = await _db.AppSettings.FindAsync(ClientesPanelTokenKey);
        if (saved is null || string.IsNullOrEmpty(saved.Value) || saved.Value != token) return NotFound();
        return await GetSaldosPendientes();
    }

    [HttpGet("saldos-pendientes")]
    public async Task<IActionResult> GetSaldosPendientes()
    {
        // Traer todas las ventas emitidas (no anuladas) con cliente y total > 0
        // Incluyo TipoComprobante para separar saldo Cotizacion (X/PRO) vs Factura (FA/FB/FC).
        var ventas = await _db.CafeVentas
            .Where(v => v.Estado != "anulado"
                     && v.ClienteId != null
                     && v.Total > 0)
            .Select(v => new {
                v.Id, ClienteId = v.ClienteId!.Value, v.Total, v.ArcaImpTotal, v.Fecha, v.TipoComprobante,
                EsSaldoMigracion = _db.CafeSaldosMigracion.Any(s => s.VentaId == v.Id)
            })
            .ToListAsync();
        if (ventas.Count == 0) return Ok(new List<ClienteSaldoPendienteDto>());

        // Calcular pagos por venta (cobranzas vigentes)
        var ventaIds = ventas.Select(v => v.Id).ToList();
        var pagados = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId != null && ventaIds.Contains(c.VentaId!.Value)
                     && c.Cobranza!.Estado == "VIGENTE")
            .GroupBy(c => c.VentaId!.Value)
            .Select(g => new { VentaId = g.Key, Pagado = g.Sum(x => x.Importe) })
            .ToListAsync();
        var pagadosDict = pagados.ToDictionary(p => p.VentaId, p => p.Pagado);

        // Calcular saldo de cada venta — incluyo TipoComprobante para poder separar despues.
        // Monto real cobrable: ArcaImpTotal (con IVA) si la venta tiene CAE, sino Total.
        var ventasConSaldo = ventas.Select(v =>
        {
            var totalCobrar = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
            return new {
                v.Id, v.ClienteId, Total = totalCobrar, v.Fecha, v.TipoComprobante, v.EsSaldoMigracion,
                Saldo = totalCobrar - (pagadosDict.TryGetValue(v.Id, out var p) ? p : 0m)
            };
        }).Where(v => v.Saldo > 0).ToList();
        if (ventasConSaldo.Count == 0) return Ok(new List<ClienteSaldoPendienteDto>());

        // Agrupar por cliente
        var clienteIds = ventasConSaldo.Select(v => v.ClienteId).Distinct().ToList();
        var clientes = await _db.CafeClientes
            .Where(c => clienteIds.Contains(c.Id))
            .ToListAsync();
        var clientesDict = clientes.ToDictionary(c => c.Id);

        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        var result = ventasConSaldo
            .GroupBy(v => v.ClienteId)
            .Select(g =>
            {
                clientesDict.TryGetValue(g.Key, out var cli);
                var fechaMasAntigua = g.Min(x => x.Fecha);
                // Separar saldo por tipo de comprobante (pedido del usuario 2026-05-19):
                //   "Cotizacion" = X + PRO (no fiscal, interno)
                //   "Factura"    = FA + FB + FC (con CAE de ARCA, fiscal)
                var saldoCotizacion = g.Where(x => x.TipoComprobante == "X" || x.TipoComprobante == "PRO")
                    .Sum(x => x.Saldo);
                var saldoFactura = g.Where(x => x.TipoComprobante == "FA" || x.TipoComprobante == "FB" || x.TipoComprobante == "FC")
                    .Sum(x => x.Saldo);
                return new ClienteSaldoPendienteDto(
                    g.Key,
                    cli?.Nombre ?? "(sin nombre)",
                    cli?.Tipo,
                    cli?.Telefono,
                    cli?.MapeoLink,
                    cli?.CodigoInterno,
                    g.Count(),
                    g.Sum(x => x.Saldo),
                    fechaMasAntigua,
                    (int)(hoy - fechaMasAntigua.Date).TotalDays,
                    g.Any(x => x.EsSaldoMigracion),
                    saldoCotizacion,
                    saldoFactura
                );
            })
            .OrderBy(c => c.FechaMasAntigua) // más antigua primero (mayor urgencia)
            .ToList();
        return Ok(result);
    }

    public class ExportSaldosRequest
    {
        /// <summary>Si está vacío, exporta TODOS los clientes con saldo. Si vienen ids, exporta solo esos.</summary>
        public List<int>? ClienteIds { get; set; }
    }

    /// <summary>Exporta las cuentas corrientes de los clientes seleccionados (o todos los deudores)
    /// a un Excel. Hoja 1 con el resumen + 1 hoja por cada cliente con sus comprobantes pendientes.</summary>
    [HttpPost("saldos-pendientes/excel")]
    public async Task<IActionResult> ExportSaldosExcel([FromBody] ExportSaldosRequest req)
    {
        // Traer ventas con sus saldos
        var ventas = await _db.CafeVentas
            .Where(v => v.Estado != "anulado" && v.ClienteId != null && v.Total > 0)
            .ToListAsync();
        if (ventas.Count == 0)
            return BadRequest(new { error = "No hay ventas pendientes para exportar" });

        var ventaIds = ventas.Select(v => v.Id).ToList();
        var pagados = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId != null && ventaIds.Contains(c.VentaId!.Value)
                     && c.Cobranza!.Estado == "VIGENTE")
            .GroupBy(c => c.VentaId!.Value)
            .Select(g => new { VentaId = g.Key, Pagado = g.Sum(x => x.Importe) })
            .ToListAsync();
        var pagadosDict = pagados.ToDictionary(p => p.VentaId, p => p.Pagado);

        // Filtrar ventas con saldo > 0. Monto cobrable = ArcaImpTotal si es factura ARCA, sino Total.
        var ventasConSaldo = ventas
            .Select(v =>
            {
                var totalCobrar = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
                var pagado = pagadosDict.TryGetValue(v.Id, out var p) ? p : 0m;
                return new {
                    v.Id, v.Numero, ClienteId = v.ClienteId!.Value, Total = totalCobrar, v.Fecha,
                    v.TipoComprobante,
                    Pagado = pagado,
                    Saldo = totalCobrar - pagado
                };
            })
            .Where(v => v.Saldo > 0)
            .ToList();

        // Filtrar por clienteIds si vinieron
        if (req.ClienteIds is not null && req.ClienteIds.Count > 0)
            ventasConSaldo = ventasConSaldo.Where(v => req.ClienteIds.Contains(v.ClienteId)).ToList();

        if (ventasConSaldo.Count == 0)
            return BadRequest(new { error = "No hay clientes con saldo pendiente que coincidan" });

        // Traer clientes
        var clienteIds = ventasConSaldo.Select(v => v.ClienteId).Distinct().ToList();
        var clientes = await _db.CafeClientes.Where(c => clienteIds.Contains(c.Id)).ToListAsync();
        var clientesDict = clientes.ToDictionary(c => c.Id);

        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        var esCulture = new System.Globalization.CultureInfo("es-AR");

        using var wb = new XLWorkbook();

        // ===== HOJA 1: RESUMEN =====
        var ws = wb.Worksheets.Add("Resumen");
        ws.Cell(1, 1).Value = "Saldos pendientes de clientes";
        ws.Range(1, 1, 1, 7).Merge().Style.Font.SetBold(true).Font.SetFontSize(14);
        ws.Cell(2, 1).Value = $"Generado: {hoy:dd/MM/yyyy}";
        ws.Range(2, 1, 2, 7).Merge().Style.Font.SetItalic(true).Font.SetFontColor(XLColor.DarkGray);

        // 9 columnas — pedido del usuario 2026-05-19: separar saldo "Cotizacion" (X/PRO) de
        // saldo "Factura" (FA/FB/FC), ademas del saldo total. Asi se ve de un vistazo cuanto
        // debe el cliente "en cotizacion" vs "facturado".
        var headers = new[] { "Cliente", "Tipo", "Teléfono", "N° pendientes", "Días vencido",
            "Más antigua", "📝 Saldo Cotización (X)", "📋 Saldo Factura (A/B/C)", "Saldo total" };
        for (int i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(4, i + 1);
            c.Value = headers[i];
            c.Style.Font.SetBold(true);
            c.Style.Fill.SetBackgroundColor(XLColor.LightGray);
            c.Style.Border.SetBottomBorder(XLBorderStyleValues.Thin);
        }

        var resumen = ventasConSaldo
            .GroupBy(v => v.ClienteId)
            .Select(g => new {
                ClienteId = g.Key,
                Cliente = clientesDict.TryGetValue(g.Key, out var c) ? c : null,
                Cantidad = g.Count(),
                Saldo = g.Sum(x => x.Saldo),
                // Split por tipo de comprobante
                SaldoCotizacion = g.Where(x => x.TipoComprobante == "X" || x.TipoComprobante == "PRO").Sum(x => x.Saldo),
                SaldoFactura = g.Where(x => x.TipoComprobante == "FA" || x.TipoComprobante == "FB" || x.TipoComprobante == "FC").Sum(x => x.Saldo),
                FechaMasAntigua = g.Min(x => x.Fecha)
            })
            .OrderBy(x => x.FechaMasAntigua)
            .ToList();

        int row = 5;
        foreach (var r in resumen)
        {
            ws.Cell(row, 1).Value = r.Cliente?.Nombre ?? "(sin nombre)";
            ws.Cell(row, 2).Value = r.Cliente?.Tipo ?? "OTRO";
            ws.Cell(row, 3).Value = r.Cliente?.Telefono ?? "";
            ws.Cell(row, 4).Value = r.Cantidad;
            ws.Cell(row, 5).Value = (int)(hoy - r.FechaMasAntigua.Date).TotalDays;
            ws.Cell(row, 6).Value = r.FechaMasAntigua; ws.Cell(row, 6).Style.DateFormat.Format = "dd/MM/yyyy";
            ws.Cell(row, 7).Value = r.SaldoCotizacion; ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 8).Value = r.SaldoFactura; ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 9).Value = r.Saldo; ws.Cell(row, 9).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }
        // Fila TOTAL
        ws.Cell(row, 1).Value = "TOTAL";
        ws.Cell(row, 1).Style.Font.SetBold(true);
        ws.Range(row, 1, row, 6).Merge().Style.Font.SetBold(true);
        ws.Cell(row, 7).Value = resumen.Sum(r => r.SaldoCotizacion);
        ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 7).Style.Font.SetBold(true);
        ws.Cell(row, 8).Value = resumen.Sum(r => r.SaldoFactura);
        ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 8).Style.Font.SetBold(true);
        ws.Cell(row, 9).Value = resumen.Sum(r => r.Saldo);
        ws.Cell(row, 9).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(row, 9).Style.Font.SetBold(true);
        ws.Range(row, 1, row, 9).Style.Fill.SetBackgroundColor(XLColor.LightYellow);

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(4);

        // ===== UNA HOJA POR CADA CLIENTE =====
        foreach (var r in resumen)
        {
            // Sanitizar nombre de la hoja (Excel no permite ciertos chars, max 31 chars)
            var sheetName = SanitizeSheetName(r.Cliente?.Nombre ?? $"Cliente {r.ClienteId}");
            // Evitar duplicados (puede haber 2 clientes con mismo nombre truncado)
            var sName = sheetName;
            int n = 2;
            while (wb.Worksheets.Any(x => x.Name == sName)) { sName = sheetName.Substring(0, Math.Min(sheetName.Length, 28)) + $"({n++})"; }
            var ws2 = wb.Worksheets.Add(sName);

            ws2.Cell(1, 1).Value = r.Cliente?.Nombre ?? "?";
            ws2.Range(1, 1, 1, 6).Merge().Style.Font.SetBold(true).Font.SetFontSize(13);
            if (r.Cliente is not null)
            {
                int infoRow = 2;
                if (!string.IsNullOrEmpty(r.Cliente.Cuit))
                {
                    ws2.Cell(infoRow, 1).Value = $"CUIT/DNI: {r.Cliente.Cuit}";
                    ws2.Range(infoRow, 1, infoRow, 6).Merge();
                    infoRow++;
                }
                if (!string.IsNullOrEmpty(r.Cliente.Telefono))
                {
                    ws2.Cell(infoRow, 1).Value = $"Teléfono: {r.Cliente.Telefono}";
                    ws2.Range(infoRow, 1, infoRow, 6).Merge();
                    infoRow++;
                }
                if (!string.IsNullOrEmpty(r.Cliente.DomicilioEntrega ?? r.Cliente.Direccion))
                {
                    ws2.Cell(infoRow, 1).Value = $"Dirección: {r.Cliente.DomicilioEntrega ?? r.Cliente.Direccion}";
                    ws2.Range(infoRow, 1, infoRow, 6).Merge();
                }
            }

            var detHeaders = new[] { "N° comprobante", "Fecha", "Tipo", "Total", "Cobrado", "Saldo" };
            int hRow = 6;
            for (int i = 0; i < detHeaders.Length; i++)
            {
                var c = ws2.Cell(hRow, i + 1);
                c.Value = detHeaders[i];
                c.Style.Font.SetBold(true);
                c.Style.Fill.SetBackgroundColor(XLColor.LightGray);
            }
            int dRow = hRow + 1;
            var itemsCliente = ventasConSaldo.Where(v => v.ClienteId == r.ClienteId).OrderBy(v => v.Fecha).ToList();
            foreach (var v in itemsCliente)
            {
                ws2.Cell(dRow, 1).Value = v.Numero;
                ws2.Cell(dRow, 2).Value = v.Fecha; ws2.Cell(dRow, 2).Style.DateFormat.Format = "dd/MM/yyyy";
                ws2.Cell(dRow, 3).Value = v.TipoComprobante;
                ws2.Cell(dRow, 4).Value = v.Total; ws2.Cell(dRow, 4).Style.NumberFormat.Format = "#,##0.00";
                ws2.Cell(dRow, 5).Value = v.Pagado; ws2.Cell(dRow, 5).Style.NumberFormat.Format = "#,##0.00";
                ws2.Cell(dRow, 6).Value = v.Saldo; ws2.Cell(dRow, 6).Style.NumberFormat.Format = "#,##0.00";
                dRow++;
            }
            // Fila TOTAL
            ws2.Cell(dRow, 1).Value = "TOTAL ADEUDADO";
            ws2.Range(dRow, 1, dRow, 5).Merge().Style.Font.SetBold(true);
            ws2.Cell(dRow, 6).Value = r.Saldo;
            ws2.Cell(dRow, 6).Style.NumberFormat.Format = "#,##0.00";
            ws2.Cell(dRow, 6).Style.Font.SetBold(true);
            ws2.Range(dRow, 1, dRow, 6).Style.Fill.SetBackgroundColor(XLColor.LightYellow);

            ws2.Columns().AdjustToContents();
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var bytes = ms.ToArray();
        var filename = $"saldos-pendientes_{hoy:yyyyMMdd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
    }

    /// <summary>Sanitiza un nombre de cliente para usarlo como nombre de hoja Excel:
    /// max 31 chars, sin / \ ? * [ ].</summary>
    private static string SanitizeSheetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Cliente";
        foreach (var bad in new[] { '/', '\\', '?', '*', '[', ']', ':' })
            name = name.Replace(bad, '-');
        if (name.Length > 31) name = name.Substring(0, 31);
        return name.Trim();
    }
}
