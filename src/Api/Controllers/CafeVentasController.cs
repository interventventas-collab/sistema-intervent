using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/ventas")]
[Authorize]
public class CafeVentasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CafeCotizacionPdfService _pdfService;
    private readonly ArcaInvoiceService _arcaInvoiceService;
    private readonly ArcaInvoicePdfService _arcaPdfService;
    private readonly ArcaEmisorService _emisorService;
    private static readonly string[] FormatosValidos = { "1KG", "MEDIO", "CUARTO", "UNIT" };

    public CafeVentasController(
        AppDbContext db,
        CafeCotizacionPdfService pdfService,
        ArcaInvoiceService arcaInvoiceService,
        ArcaInvoicePdfService arcaPdfService,
        ArcaEmisorService emisorService)
    {
        _db = db;
        _pdfService = pdfService;
        _arcaInvoiceService = arcaInvoiceService;
        _arcaPdfService = arcaPdfService;
        _emisorService = emisorService;
    }

    private static CafeVentaDto Map(CafeVenta v) => new(
        v.Id, v.Numero, v.Fecha,
        v.ClienteId, v.ClienteNombreSnapshot, v.ClienteTipoSnapshot, v.ClienteTelefonoSnapshot,
        v.Subtotal, v.Descuento, v.Total, v.CostoTotal, v.Margen,
        v.Observaciones, v.Estado,
        v.WeekDays, v.IsPaid,
        v.TipoComprobante, v.CondicionIva, v.CondicionPago,
        v.CreatedAt,
        v.Items.Select(i => new CafeVentaItemDto(
            i.Id, i.ProductoId, i.ProductoNombreSnapshot, i.Categoria,
            i.Formato, i.Cantidad,
            i.PrecioUnitario, i.CostoUnitario, i.Subtotal,
            i.GramosDescontados,
            i.Molienda, i.EsDoyPack,
            i.DescuentoPct)).ToList(),
        v.ClienteRazonSocialSnapshot,
        v.ClienteDomicilioEntregaSnapshot,
        v.ClienteComentariosComprobante,
        v.ClienteCuitSnapshot,
        v.ClienteDireccionSnapshot,
        v.ClienteLocalidadSnapshot,
        v.ClienteCiudadSnapshot,
        v.ClienteCpSnapshot,
        v.ArcaEstado,
        v.ArcaCae,
        v.ArcaCaeVto,
        v.ArcaPtoVta,
        v.ArcaCbteNro,
        v.ArcaCbteTipoNum,
        v.ArcaError);

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var q = _db.CafeVentas.Include(v => v.Items).AsQueryable();
        if (from.HasValue) q = q.Where(v => v.Fecha >= from.Value.Date);
        if (to.HasValue) q = q.Where(v => v.Fecha <= to.Value.Date);
        var list = await q.OrderByDescending(v => v.Fecha).ThenByDescending(v => v.Id).Take(200).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        return Ok(Map(v));
    }

    /// <summary>
    /// Genera el PDF de la cotización / proforma / comprobante interno usando QuestPDF.
    /// Devuelve los bytes inline para que el frontend lo abra en una pestaña nueva.
    /// Cuando avancemos con factura ARCA real (FA/FB/FC), ese tipo de venta va a usar
    /// otro endpoint (el de ARCA) — este es solo para los tipos X y PRO.
    /// </summary>
    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> GetPdf(int id)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        var cfg = await _db.CafeSettings.FindAsync(1);

        // Si la venta es Factura A/B/C y está autorizada en ARCA → PDF de factura ARCA con CAE+QR.
        // Si no → PDF de cotización interno (lo que ya tenías).
        var esFacturaArca = v.TipoComprobante is "FA" or "FB" or "FC";
        var autorizada = v.ArcaEstado == "autorizado"
                         && !string.IsNullOrEmpty(v.ArcaCae)
                         && v.ArcaCbteNro.HasValue
                         && v.ArcaPtoVta.HasValue
                         && v.ArcaCbteTipoNum.HasValue;

        if (esFacturaArca && autorizada)
        {
            var pdfBytes = BuildArcaPdf(v, cfg!);
            return File(pdfBytes, "application/pdf", $"{v.Numero}.pdf");
        }

        var bytes = _pdfService.GenerarPdfBytes(v, cfg);
        return File(bytes, "application/pdf", $"{v.Numero}.pdf");
    }

    /// <summary>
    /// Arma el PdfEmisor + PdfComprobante + PdfReceptor a partir de los datos de la venta
    /// del Café y los datos del negocio, y genera el PDF de factura ARCA (con CAE y QR).
    /// </summary>
    private byte[] BuildArcaPdf(CafeVenta v, CafeSetting cfg)
    {
        var ficha = _emisorService.GetEntityByCuitAsync(cfg?.NegocioCuit ?? "").GetAwaiter().GetResult();
        var emisor = new PdfEmisor
        {
            Cuit = ficha?.Cuit ?? new string((cfg?.NegocioCuit ?? "").Where(char.IsDigit).ToArray()),
            RazonSocial = ficha?.RazonSocial ?? cfg?.NegocioRazonSocial ?? cfg?.NegocioNombre ?? "—",
            CondicionIva = ficha?.CondicionIva ?? "Responsable Inscripto",
            Domicilio = ficha?.Domicilio ?? cfg?.NegocioDireccion,
            IIBBTipo = ficha?.IIBBTipo,
            IIBBNumero = ficha?.IIBBNumero ?? cfg?.NegocioIngresosBrutos,
            InicioActividades = ficha?.InicioActividades ?? cfg?.NegocioInicioActividad,
            LogoBytes = _emisorService.TryGetLogoBytes(ficha?.LogoPath),
        };

        var letra = ArcaInvoicePdfService.LetraDelTipo(v.ArcaCbteTipoNum ?? 0);

        // Los precios del Café se cargan SIN IVA, por convención. Recalculamos
        // neto/IVA/total acá igual que lo hizo ArcaInvoiceService al emitir
        // contra ARCA, para que el PDF muestre los montos coherentes con el CAE
        // (y el QR del comprobante lleve el mismo Importe Total que tiene ARCA).
        //   - Letra A / B: total = neto + 21%
        //   - Letra C:     total = neto (sin IVA)
        decimal neto = v.Total;
        decimal ivaPct = letra == "C" ? 0m : 21m;
        decimal ivaImporte = Math.Round(neto * ivaPct / 100m, 2, MidpointRounding.AwayFromZero);
        decimal totalConIva = neto + ivaImporte;

        var comp = new PdfComprobante
        {
            CbteTipoNro = v.ArcaCbteTipoNum ?? 0,
            CbteTipoNombre = ArcaWsService.NombreCbte(v.ArcaCbteTipoNum ?? 0),
            PtoVta = v.ArcaPtoVta ?? 0,
            CbteNro = v.ArcaCbteNro ?? 0,
            Fecha = v.Fecha.ToString("yyyyMMdd"),
            Concepto = 1,
            ImpNeto = neto,
            ImpTotal = totalConIva,
            Cae = v.ArcaCae,
            CaeVto = v.ArcaCaeVto?.ToString("yyyyMMdd") ?? "",
        };

        foreach (var it in v.Items)
        {
            var puConDesc = it.DescuentoPct > 0 && it.Cantidad > 0
                ? Math.Round(it.Subtotal / it.Cantidad, 2, MidpointRounding.AwayFromZero)
                : it.PrecioUnitario;
            var desc = it.ProductoNombreSnapshot;
            if (!string.IsNullOrEmpty(it.Molienda)) desc += $" — {it.Molienda}";
            if (it.EsDoyPack) desc += " (d.p.)";
            desc += $" · {it.Formato}";

            comp.Items.Add(new PdfItem
            {
                Descripcion = desc,
                Cantidad = it.Cantidad,
                PrecioUnitario = puConDesc,
                AlicPct = letra == "C" ? 0 : 21m, // Hardcoded 21% — coherente con la emisión
            });
        }
        // Desglose IVA en el footer del PDF:
        //   - Letra A: obligatorio por norma (también la tabla muestra precio sin IVA).
        //   - Letra B: lo agregamos como info al cliente (precio en tabla va con IVA incluido,
        //     pero abajo vemos Neto + IVA + Total para que cuadre todo).
        //   - Letra C: sin IVA, no aplica.
        if (letra is "A" or "B")
        {
            comp.IvasDesglosados.Add(new PdfIvaDesglose { Pct = 21m, Importe = ivaImporte });
        }

        var docTipoR = 99;
        var docNroR = "0";
        var cuitCli = new string((v.ClienteCuitSnapshot ?? "").Where(char.IsDigit).ToArray());
        if (cuitCli.Length == 11) { docTipoR = 80; docNroR = cuitCli; }

        var receptor = new PdfReceptor
        {
            DocTipo = docTipoR,
            DocNro = docNroR,
            Nombre = !string.IsNullOrWhiteSpace(v.ClienteRazonSocialSnapshot)
                ? v.ClienteRazonSocialSnapshot
                : (v.ClienteNombreSnapshot ?? "Consumidor Final"),
            Domicilio = v.ClienteDireccionSnapshot,
            CondicionIvaId = v.CondicionIva switch { "RI" => 1, "EX" => 4, "MO" => 6, "CF" => 5, _ => 5 },
            CondicionVenta = v.CondicionPago switch
            {
                "EFECTIVO" => "Efectivo",
                "TRANSFERENCIA" => "Transferencia",
                "DEBITO" => "Débito",
                "CREDITO" => "Crédito",
                "CTA_CORRIENTE" => "Cuenta corriente",
                "CHEQUE" => "Cheque",
                "V_PRIVADO" => "Venta privada",
                _ => null
            },
        };

        return _arcaPdfService.GenerarPdfBytes(emisor, comp, receptor, false);
    }

    /// <summary>Devuelve los productos que mas compro un cliente (combinacion ProductoId+Formato),
    /// ordenados por cantidad de comprobantes en los que aparecio. Solo cuenta ventas no anuladas.</summary>
    [HttpGet("top-productos-cliente/{clienteId:int}")]
    public async Task<IActionResult> GetTopProductosByCliente(int clienteId, [FromQuery] int count = 10)
    {
        if (clienteId <= 0) return Ok(new List<CafeTopProductoClienteDto>());
        if (count <= 0) count = 10;

        var grouped = await _db.CafeVentaItems
            .Where(i => i.VentaNav != null
                        && i.VentaNav.ClienteId == clienteId
                        && i.VentaNav.Estado != "anulado")
            .GroupBy(i => new { i.ProductoId, i.Formato })
            .Select(g => new
            {
                g.Key.ProductoId,
                g.Key.Formato,
                TimesOrdered = g.Select(x => x.VentaId).Distinct().Count(),
                TotalQuantity = g.Sum(x => x.Cantidad),
                LastPurchase = g.Max(x => x.VentaNav!.Fecha)
            })
            .OrderByDescending(x => x.TimesOrdered)
            .ThenByDescending(x => x.TotalQuantity)
            .ThenByDescending(x => x.LastPurchase)
            .Take(count)
            .ToListAsync();

        if (grouped.Count == 0) return Ok(new List<CafeTopProductoClienteDto>());

        var ids = grouped.Select(x => x.ProductoId).Distinct().ToList();
        var productos = await _db.CafeProductos
            .Where(p => ids.Contains(p.Id) && p.IsActive)
            .ToListAsync();

        var cliente = await _db.CafeClientes.FindAsync(clienteId);
        var tipo = CafePricingService.ResolverTipo(cliente?.Tipo);
        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };

        var result = new List<CafeTopProductoClienteDto>();
        foreach (var g in grouped)
        {
            var p = productos.FirstOrDefault(x => x.Id == g.ProductoId);
            if (p is null) continue;
            var precio = CafePricingService.CalcularPrecioUnitario(p, g.Formato, tipo, settings);
            result.Add(new CafeTopProductoClienteDto(
                p.Id, p.Sku, p.Nombre, p.Categoria, p.Marca,
                g.Formato,
                g.TimesOrdered, g.TotalQuantity, g.LastPurchase,
                p.StockGramos, p.StockUnidades, precio));
        }
        return Ok(result);
    }

    /// <summary>Cotización en vivo: NO crea la venta, solo calcula precios + verifica stock.</summary>
    [HttpPost("cotizar")]
    public async Task<IActionResult> Cotizar([FromBody] CafeCotizarRequest req)
    {
        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        var tipo = await ResolverTipoAsync(req.ClienteId, req.ClienteTipo);
        return Ok(await CotizarInternoAsync(req.Items, tipo, req.Descuento, settings));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeVentaRequest req)
    {
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "La venta debe tener al menos un item" });

        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        var tipo = await ResolverTipoAsync(req.ClienteId, req.ClienteTipoOverride);

        var cot = await CotizarInternoAsync(req.Items, tipo, req.Descuento, settings);
        if (!cot.TodoOk)
            return BadRequest(new { error = "No hay stock suficiente para alguno de los items. Revisá la cotización." });

        // Resolver datos del cliente
        string? clienteNombre = null;
        string? clienteTelefono = null;
        string? clienteRazonSocial = null;
        string? clienteDomicilioEntrega = null;
        string? clienteComentariosComprobante = null;
        string? clienteCuit = null;
        string? clienteDireccion = null;
        string? clienteLocalidad = null;
        string? clienteCiudad = null;
        string? clienteCp = null;
        if (req.ClienteId.HasValue && req.ClienteId.Value > 0)
        {
            var cli = await _db.CafeClientes.FindAsync(req.ClienteId.Value);
            if (cli is null) return BadRequest(new { error = "Cliente no encontrado" });
            clienteNombre = cli.Nombre;
            clienteTelefono = cli.Telefono;
            clienteRazonSocial = cli.RazonSocial;
            clienteDomicilioEntrega = cli.DomicilioEntrega;
            clienteComentariosComprobante = cli.ComentariosComprobante;
            clienteCuit = cli.Cuit;
            clienteDireccion = cli.Direccion;
            clienteLocalidad = cli.Localidad;
            clienteCiudad = cli.Ciudad;
            clienteCp = cli.Cp;
            tipo = CafePricingService.ResolverTipo(cli.Tipo);
        }
        else
        {
            clienteNombre = string.IsNullOrWhiteSpace(req.ClienteNombreOverride) ? "Consumidor final" : req.ClienteNombreOverride.Trim();
        }

        // Persistir: crear venta + items + descontar stock
        var venta = new CafeVenta
        {
            Numero = await GenerarNumeroAsync(),
            Fecha = (req.Fecha ?? DateTime.Today).Date,
            ClienteId = req.ClienteId.HasValue && req.ClienteId.Value > 0 ? req.ClienteId.Value : null,
            ClienteNombreSnapshot = clienteNombre,
            ClienteTipoSnapshot = tipo,
            ClienteTelefonoSnapshot = clienteTelefono,
            ClienteRazonSocialSnapshot = clienteRazonSocial,
            ClienteDomicilioEntregaSnapshot = clienteDomicilioEntrega,
            ClienteComentariosComprobante = clienteComentariosComprobante,
            ClienteCuitSnapshot = clienteCuit,
            ClienteDireccionSnapshot = clienteDireccion,
            ClienteLocalidadSnapshot = clienteLocalidad,
            ClienteCiudadSnapshot = clienteCiudad,
            ClienteCpSnapshot = clienteCp,
            Subtotal = cot.Subtotal,
            Descuento = cot.Descuento,
            Total = cot.Total,
            CostoTotal = cot.CostoTotal,
            Margen = cot.Margen,
            Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim(),
            Estado = "emitido",
            WeekDays = NormWeekDays(req.WeekDays),
            IsPaid = req.IsPaid,
            TipoComprobante = NormTipoComprobante(req.TipoComprobante),
            CondicionIva = NormCondicionIva(req.CondicionIva),
            CondicionPago = NormCondicionPago(req.CondicionPago),
            CreatedAt = DateTime.UtcNow
        };

        // Mapear items + descontar stock fisico
        foreach (var it in cot.Items)
        {
            var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
            if (prod is null) return BadRequest(new { error = $"Producto {it.ProductoId} no encontrado" });

            venta.Items.Add(new CafeVentaItem
            {
                ProductoId = prod.Id,
                ProductoNombreSnapshot = prod.Nombre,
                Categoria = prod.Categoria,
                Formato = it.Formato,
                Cantidad = it.Cantidad,
                PrecioUnitario = it.PrecioUnitario,
                CostoUnitario = it.CostoUnitario,
                Subtotal = it.Subtotal,
                GramosDescontados = it.GramosNecesarios,
                Molienda = NormMolienda(it.Molienda),
                EsDoyPack = it.EsDoyPack && prod.Categoria == "CAFE",  // doy pack solo aplica a cafe
                DescuentoPct = it.DescuentoPct
            });

            // Descontar stock
            if (prod.Categoria == "CAFE")
                prod.StockGramos = Math.Max(0m, prod.StockGramos - it.GramosNecesarios);
            else
                prod.StockUnidades = Math.Max(0, prod.StockUnidades - it.Cantidad);
            prod.UpdatedAt = DateTime.UtcNow;
        }

        _db.CafeVentas.Add(venta);
        await _db.SaveChangesAsync();

        // ============================================================
        // Si es Factura A/B/C, intentar emitir contra ARCA inmediatamente.
        // No bloquea el guardado: si ARCA rechaza, la venta queda como
        // "pendiente" y el usuario puede reintentar.
        // ============================================================
        if (venta.TipoComprobante is "FA" or "FB" or "FC")
        {
            await EmitirArcaAsync(venta);
        }

        return Ok(Map(venta));
    }

    /// <summary>
    /// Emite la factura contra ARCA usando el certificado activo del CUIT del negocio Café.
    /// NO tira excepciones — si falla, deja la venta marcada como "pendiente" con error.
    /// </summary>
    private async Task EmitirArcaAsync(CafeVenta venta)
    {
        try
        {
            // 1. Resolver el CUIT del emisor (del CafeSetting)
            var cfg = await _db.CafeSettings.FindAsync(1);
            var cuitEmisor = cfg?.NegocioCuit;
            if (string.IsNullOrWhiteSpace(cuitEmisor))
            {
                venta.ArcaEstado = "pendiente";
                venta.ArcaError = "Falta cargar el CUIT en Café → Configuración del negocio.";
                await _db.SaveChangesAsync();
                return;
            }

            // 2. Buscar el certificado ARCA activo para ese CUIT
            var cuitDigits = new string(cuitEmisor.Where(char.IsDigit).ToArray());
            var arcaAccount = await _db.ArcaWebserviceAccounts
                .Where(a => a.Cuit == cuitDigits && a.IsActive)
                .OrderByDescending(a => a.Environment == "production")
                .FirstOrDefaultAsync();
            if (arcaAccount is null)
            {
                venta.ArcaEstado = "pendiente";
                venta.ArcaError = $"No hay un certificado ARCA activo para el CUIT {cuitDigits}. Cargá uno en Integraciones → ARCA (webservice).";
                await _db.SaveChangesAsync();
                return;
            }

            // 3. Mapear tipo de comprobante: FA→1, FB→6, FC→11
            int cbteTipo = venta.TipoComprobante switch
            {
                "FA" => 1,
                "FB" => 6,
                "FC" => 11,
                _ => 0
            };
            if (cbteTipo == 0)
            {
                venta.ArcaEstado = "pendiente";
                venta.ArcaError = $"Tipo de comprobante {venta.TipoComprobante} no mapeable a ARCA.";
                await _db.SaveChangesAsync();
                return;
            }

            // 4. Mapear DocTipo y CondIVA del receptor
            //    - Si tiene CUIT cargado → DocTipo 80 (CUIT)
            //    - Si no → DocTipo 99 (CF) con DocNro 0
            int docTipo;
            string docNro;
            var cuitCli = new string((venta.ClienteCuitSnapshot ?? "").Where(char.IsDigit).ToArray());
            if (cuitCli.Length == 11)
            {
                docTipo = 80;
                docNro = cuitCli;
            }
            else
            {
                docTipo = 99;
                docNro = "0";
            }

            // CondicionIVAReceptorId — ARCA usa códigos específicos (RG 5616).
            // Si la venta tiene cliente cargado, leemos la Cond IVA ACTUAL del cliente
            // (no el snapshot guardado en la venta), así si el usuario actualizó la ficha
            // del cliente entre la primera emisión fallida y un reintento, ese cambio
            // se aplica automáticamente. Esto es clave para el caso en que ARCA rechazó
            // por condición IVA inválida y el usuario corrigió la ficha.
            var condIvaActual = venta.CondicionIva;
            if (venta.ClienteId.HasValue)
            {
                var cli = await _db.CafeClientes.FindAsync(venta.ClienteId.Value);
                if (cli is not null && !string.IsNullOrEmpty(cli.CondicionIvaDefault))
                {
                    condIvaActual = cli.CondicionIvaDefault!;
                    // También actualizo el snapshot de la venta para que coincida
                    venta.CondicionIva = cli.CondicionIvaDefault!;
                }
            }
            int condIvaReceptor = condIvaActual switch
            {
                "RI" => 1,   // Responsable Inscripto
                "EX" => 4,   // Sujeto Exento
                "CF" => 5,   // Consumidor Final
                "MO" => 6,   // Monotributo
                _ => 5,
            };

            // 5. Mapear items. Precios del Café se asumen SIN IVA. Alícuota = 21% por default.
            //    El descuento por item se aplica al precio unitario antes de pasarlo a ARCA.
            var items = new List<EmitirComprobanteItemDto>();
            foreach (var it in venta.Items)
            {
                var puConDesc = it.DescuentoPct > 0 && it.Cantidad > 0
                    ? Math.Round(it.Subtotal / it.Cantidad, 2, MidpointRounding.AwayFromZero)
                    : it.PrecioUnitario;

                var desc = it.ProductoNombreSnapshot;
                if (!string.IsNullOrEmpty(it.Molienda)) desc += $" — {it.Molienda}";
                if (it.EsDoyPack) desc += " (d.p.)";
                desc += $" · {it.Formato}";

                items.Add(new EmitirComprobanteItemDto
                {
                    Descripcion = desc,
                    Cantidad = it.Cantidad,
                    PrecioUnitario = puConDesc,
                    AlicIvaId = 5, // 21% — hardcoded por ahora, se mejora con campo AlicIvaPct por producto después
                });
            }

            // 6. Armar el request y llamar al ArcaInvoiceService
            var req = new EmitirComprobanteRequest
            {
                PtoVta = 2, // PtoVta fijo para Café (configurable después si hace falta)
                CbteTipo = cbteTipo,
                Concepto = 1, // Productos
                DocTipo = docTipo,
                DocNro = docNro,
                ReceptorNombre = !string.IsNullOrWhiteSpace(venta.ClienteRazonSocialSnapshot)
                    ? venta.ClienteRazonSocialSnapshot!
                    : (venta.ClienteNombreSnapshot ?? "Consumidor Final"),
                ReceptorDomicilio = venta.ClienteDireccionSnapshot,
                CondicionIVAReceptorId = condIvaReceptor,
                Items = items,
            };

            var resultado = await _arcaInvoiceService.EmitirComprobanteAsync(arcaAccount.Id, req);

            // 7. Persistir resultado
            if (resultado.Success)
            {
                venta.ArcaEstado = "autorizado";
                venta.ArcaCae = resultado.Cae;
                if (DateTime.TryParseExact(resultado.CaeVto, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var caeVto))
                    venta.ArcaCaeVto = caeVto;
                venta.ArcaPtoVta = resultado.PtoVta;
                venta.ArcaCbteNro = resultado.CbteNro;
                venta.ArcaCbteTipoNum = resultado.CbteTipo;
                venta.ArcaError = string.IsNullOrEmpty(resultado.Observaciones) ? null : resultado.Observaciones;
            }
            else
            {
                venta.ArcaEstado = "pendiente";
                venta.ArcaError = resultado.Error ?? "ARCA rechazó la factura.";
            }
            venta.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            venta.ArcaEstado = "pendiente";
            venta.ArcaError = "Error inesperado: " + ex.Message;
            venta.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>Reintentar emisión ARCA para una venta que quedó pendiente.</summary>
    [HttpPost("{id:int}/retry-arca")]
    public async Task<IActionResult> RetryArca(int id)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        if (v.TipoComprobante is not ("FA" or "FB" or "FC"))
            return BadRequest(new { error = "Esta venta no es factura, no se puede emitir contra ARCA." });
        if (v.ArcaEstado == "autorizado")
            return BadRequest(new { error = "La venta ya está autorizada con CAE " + v.ArcaCae });
        await EmitirArcaAsync(v);
        return Ok(Map(v));
    }

    /// <summary>Toggle de "pagado" y/o "dias de la semana" sobre una venta ya emitida (sin recalcular precios).</summary>
    [HttpPut("{id:int}/flags")]
    public async Task<IActionResult> UpdateFlags(int id, [FromBody] UpdateCafeVentaFlagsRequest req)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        if (req.WeekDays is not null) v.WeekDays = NormWeekDays(req.WeekDays);
        if (req.IsPaid.HasValue) v.IsPaid = req.IsPaid.Value;
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(v));
    }

    [HttpPost("{id:int}/anular")]
    public async Task<IActionResult> Anular(int id)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        if (v.Estado == "anulado") return BadRequest(new { error = "Ya estaba anulada" });

        // Comprobantes con CAE de ARCA: no se pueden anular desde acá — están en el libro
        // IVA Ventas de ARCA. La única forma de revertirlos es emitiendo una Nota de Crédito.
        if (v.ArcaEstado == "autorizado")
            return BadRequest(new { error = $"El comprobante tiene CAE de ARCA ({v.ArcaCae}). Para revertirlo hay que emitir una Nota de Crédito." });

        // Restaurar stock
        foreach (var it in v.Items)
        {
            var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
            if (prod is null) continue;
            if (prod.Categoria == "CAFE")
                prod.StockGramos += it.GramosDescontados;
            else
                prod.StockUnidades += it.Cantidad;
            prod.UpdatedAt = DateTime.UtcNow;
        }
        v.Estado = "anulado";
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(v));
    }

    /// <summary>Devuelve quien puede eliminar y el hint de la clave (sin la clave en si).</summary>
    [HttpGet("delete-settings")]
    public async Task<IActionResult> GetDeleteSettings()
    {
        var keys = new[] { "sales.delete_allowed_operator", "sales.delete_password_hint" };
        var settings = await _db.AppSettings.Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        return Ok(new DeleteCafeVentaSettingsDto(
            settings.GetValueOrDefault("sales.delete_allowed_operator", "OSMAR"),
            settings.GetValueOrDefault("sales.delete_password_hint", "")
        ));
    }

    /// <summary>Eliminacion definitiva de UNA venta. Requiere operador permitido + clave.</summary>
    [HttpPost("{id:int}/delete")]
    public async Task<IActionResult> Delete(int id, [FromBody] DeleteCafeVentaRequest req)
    {
        var op = HttpContext.Request.Headers["X-Operator-Name"].ToString();
        try
        {
            await ValidateDeletePermissionAsync(op, req.Password);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }

        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        // Comprobantes con CAE de ARCA: no se pueden borrar. Solo NC.
        if (v.ArcaEstado == "autorizado")
            return BadRequest(new { error = $"El comprobante tiene CAE de ARCA ({v.ArcaCae}). No se puede eliminar; hay que emitir una Nota de Crédito." });
        await DeleteVentaInternalAsync(v);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    /// <summary>Eliminacion masiva. Requiere operador permitido + clave.</summary>
    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteCafeVentasRequest req)
    {
        if (req.Ids is null || req.Ids.Count == 0)
            return BadRequest(new { error = "No hay ventas seleccionadas" });

        var op = HttpContext.Request.Headers["X-Operator-Name"].ToString();
        try
        {
            await ValidateDeletePermissionAsync(op, req.Password);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }

        var ventas = await _db.CafeVentas.Include(x => x.Items)
            .Where(v => req.Ids.Contains(v.Id))
            .ToListAsync();

        // Filtrar las que tienen CAE — esas no se pueden borrar. Si hay alguna,
        // avisar pero seguir con las demás (mejor experiencia que abortar todo).
        var conCae = ventas.Where(v => v.ArcaEstado == "autorizado").ToList();
        var borrables = ventas.Where(v => v.ArcaEstado != "autorizado").ToList();

        foreach (var v in borrables)
            await DeleteVentaInternalAsync(v);

        await _db.SaveChangesAsync();
        return Ok(new
        {
            deleted = borrables.Count,
            skipped = conCae.Count,
            skippedNumbers = conCae.Select(v => v.Numero).ToList(),
            warning = conCae.Count > 0
                ? $"{conCae.Count} comprobante(s) con CAE de ARCA no se borraron. Para revertirlos hay que emitir Nota de Crédito."
                : null
        });
    }

    /// <summary>Edita una venta. Si Items != null y la venta esta emitida, reemplaza items + recalcula
    /// precios + ajusta stock (devuelve los viejos, descuenta los nuevos). Si Items es null, solo metadata.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeVentaRequest req)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        if (req.Fecha.HasValue) v.Fecha = req.Fecha.Value.Date;
        if (req.Observaciones is not null)
            v.Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim();
        if (req.TipoComprobante is not null) v.TipoComprobante = NormTipoComprobante(req.TipoComprobante);
        if (req.CondicionIva is not null) v.CondicionIva = NormCondicionIva(req.CondicionIva);
        if (req.CondicionPago is not null) v.CondicionPago = NormCondicionPago(req.CondicionPago);
        if (req.WeekDays is not null) v.WeekDays = NormWeekDays(req.WeekDays);
        if (req.IsPaid.HasValue) v.IsPaid = req.IsPaid.Value;

        // Cliente: si mandaron ClienteId valido > 0, vinculo al cliente y refresco snapshot.
        // Si mandaron 0 o null + override, dejo como manual (consumidor final / nombre libre).
        if (req.ClienteId.HasValue)
        {
            if (req.ClienteId.Value > 0)
            {
                var cli = await _db.CafeClientes.FindAsync(req.ClienteId.Value);
                if (cli is null) return BadRequest(new { error = "Cliente no encontrado" });
                v.ClienteId = cli.Id;
                v.ClienteNombreSnapshot = cli.Nombre;
                v.ClienteTipoSnapshot = CafePricingService.ResolverTipo(cli.Tipo);
                v.ClienteTelefonoSnapshot = cli.Telefono;
                v.ClienteRazonSocialSnapshot = cli.RazonSocial;
                v.ClienteDomicilioEntregaSnapshot = cli.DomicilioEntrega;
                v.ClienteComentariosComprobante = cli.ComentariosComprobante;
                v.ClienteCuitSnapshot = cli.Cuit;
                v.ClienteDireccionSnapshot = cli.Direccion;
                v.ClienteLocalidadSnapshot = cli.Localidad;
                v.ClienteCiudadSnapshot = cli.Ciudad;
                v.ClienteCpSnapshot = cli.Cp;
            }
            else
            {
                v.ClienteId = null;
                v.ClienteNombreSnapshot = string.IsNullOrWhiteSpace(req.ClienteNombreOverride)
                    ? "Consumidor final" : req.ClienteNombreOverride.Trim();
                v.ClienteTipoSnapshot = CafePricingService.ResolverTipo(req.ClienteTipoOverride);
                v.ClienteTelefonoSnapshot = null;
                v.ClienteRazonSocialSnapshot = null;
                v.ClienteDomicilioEntregaSnapshot = null;
                v.ClienteComentariosComprobante = null;
                v.ClienteCuitSnapshot = null;
                v.ClienteDireccionSnapshot = null;
                v.ClienteLocalidadSnapshot = null;
                v.ClienteCiudadSnapshot = null;
                v.ClienteCpSnapshot = null;
            }
        }
        else if (!v.ClienteId.HasValue && !string.IsNullOrWhiteSpace(req.ClienteNombreOverride))
        {
            v.ClienteNombreSnapshot = req.ClienteNombreOverride.Trim();
            if (!string.IsNullOrWhiteSpace(req.ClienteTipoOverride))
                v.ClienteTipoSnapshot = CafePricingService.ResolverTipo(req.ClienteTipoOverride);
        }

        // Items: si se envian, reemplazar + ajustar stock + recalcular totales.
        // Solo aplicable si la venta no esta anulada.
        if (req.Items is not null)
        {
            if (v.Estado != "emitido")
                return BadRequest(new { error = "No se pueden modificar los items de una venta anulada" });
            if (req.Items.Count == 0)
                return BadRequest(new { error = "La venta debe tener al menos un item" });

            using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                // 1. Devolver stock de los items actuales.
                foreach (var item in v.Items)
                {
                    var prod = await _db.CafeProductos.FindAsync(item.ProductoId);
                    if (prod is null) continue;
                    if (prod.Categoria == "CAFE") prod.StockGramos += item.GramosDescontados;
                    else prod.StockUnidades += item.Cantidad;
                    prod.UpdatedAt = DateTime.UtcNow;
                }
                _db.CafeVentaItems.RemoveRange(v.Items);
                v.Items.Clear();
                await _db.SaveChangesAsync();

                // 2. Cotizar items nuevos contra el stock recien restaurado.
                var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
                var tipo = v.ClienteTipoSnapshot ?? "OTRO";
                var descuentoNuevo = req.Descuento ?? v.Descuento;
                var cot = await CotizarInternoAsync(req.Items, tipo, descuentoNuevo, settings);
                if (!cot.TodoOk)
                {
                    await tx.RollbackAsync();
                    return BadRequest(new { error = "No hay stock suficiente para alguno de los items nuevos." });
                }

                // 3. Persistir items nuevos + descontar stock.
                foreach (var ci in cot.Items)
                {
                    var prod = await _db.CafeProductos.FindAsync(ci.ProductoId);
                    if (prod is null) return BadRequest(new { error = $"Producto {ci.ProductoId} no encontrado" });
                    v.Items.Add(new CafeVentaItem
                    {
                        ProductoId = prod.Id,
                        ProductoNombreSnapshot = prod.Nombre,
                        Categoria = prod.Categoria,
                        Formato = ci.Formato,
                        Cantidad = ci.Cantidad,
                        PrecioUnitario = ci.PrecioUnitario,
                        CostoUnitario = ci.CostoUnitario,
                        Subtotal = ci.Subtotal,
                        GramosDescontados = ci.GramosNecesarios,
                        Molienda = NormMolienda(ci.Molienda),
                        EsDoyPack = ci.EsDoyPack && prod.Categoria == "CAFE",
                        DescuentoPct = ci.DescuentoPct
                    });
                    if (prod.Categoria == "CAFE")
                        prod.StockGramos = Math.Max(0m, prod.StockGramos - ci.GramosNecesarios);
                    else
                        prod.StockUnidades = Math.Max(0, prod.StockUnidades - ci.Cantidad);
                    prod.UpdatedAt = DateTime.UtcNow;
                }

                v.Subtotal = cot.Subtotal;
                v.Descuento = cot.Descuento;
                v.Total = cot.Total;
                v.CostoTotal = cot.CostoTotal;
                v.Margen = cot.Margen;
                v.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
        else if (req.Descuento.HasValue)
        {
            // Solo cambio el descuento global sin tocar items.
            var d = Math.Min(v.Subtotal, Math.Abs(req.Descuento.Value));
            v.Descuento = d;
            v.Total = Math.Max(0m, v.Subtotal - d);
            v.Margen = v.Total - v.CostoTotal;
            v.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        else
        {
            v.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return Ok(Map(v));
    }

    private async Task ValidateDeletePermissionAsync(string operatorName, string password)
    {
        var allowedOp = (await _db.AppSettings.FindAsync("sales.delete_allowed_operator"))?.Value ?? "OSMAR";
        var expectedPassword = (await _db.AppSettings.FindAsync("sales.delete_password"))?.Value ?? "";

        if (!string.Equals(operatorName ?? "", allowedOp, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Solo {allowedOp} puede eliminar comprobantes.");
        if (string.IsNullOrEmpty(expectedPassword) || password != expectedPassword)
            throw new UnauthorizedAccessException("Clave incorrecta.");
    }

    private async Task DeleteVentaInternalAsync(CafeVenta v)
    {
        // Si estaba emitida, restaurar stock antes de borrar.
        if (v.Estado == "emitido")
        {
            foreach (var it in v.Items)
            {
                var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
                if (prod is null) continue;
                if (prod.Categoria == "CAFE") prod.StockGramos += it.GramosDescontados;
                else prod.StockUnidades += it.Cantidad;
            }
        }
        _db.CafeVentas.Remove(v);
    }

    // ============================================================
    // INTERNAS
    // ============================================================

    private async Task<string> ResolverTipoAsync(int? clienteId, string? tipoOverride)
    {
        if (clienteId.HasValue && clienteId.Value > 0)
        {
            var c = await _db.CafeClientes.FindAsync(clienteId.Value);
            if (c is not null) return CafePricingService.ResolverTipo(c.Tipo);
        }
        return CafePricingService.ResolverTipo(tipoOverride);
    }

    private async Task<CafeCotizadoDto> CotizarInternoAsync(List<CafeCotizarItemRequest> items, string tipo, decimal descuento, CafeSetting settings)
    {
        var cotizadoItems = new List<CafeCotizadoItemDto>();
        decimal subtotal = 0m, costoTotal = 0m;
        bool todoOk = true;

        // Pre-cargar marcas (para bloqueo de descuento) y reglas de precios.
        var marcas = await _db.CafeMarcas.ToDictionaryAsync(m => m.Id);
        var reglas = await _db.CafeReglasPrecios.ToListAsync();

        decimal ResolverDescuento(string categoria, int? marcaId)
        {
            // Override por marca + categoria.
            if (marcaId.HasValue)
            {
                var override_ = reglas.FirstOrDefault(r => r.TipoCliente == tipo && r.Categoria == categoria && r.MarcaId == marcaId);
                if (override_ is not null) return override_.DescuentoPct;
            }
            // Regla general (sin marca).
            var general = reglas.FirstOrDefault(r => r.TipoCliente == tipo && r.Categoria == categoria && r.MarcaId == null);
            return general?.DescuentoPct ?? 0m;
        }

        foreach (var it in items)
        {
            if (it.Cantidad <= 0) continue;
            // Descuento manual override. Si viene 0 desde el request, se calcula automaticamente
            // de la matriz por tipo cliente x marca del producto.
            var descPctManual = Math.Clamp(it.DescuentoPct, 0m, 100m);
            if (!FormatosValidos.Contains(it.Formato))
            {
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    it.ProductoId, "?", "?", it.Formato, it.Cantidad, 0m, 0m, 0m, 0m, 0m, 0,
                    false, "Formato inválido", NormMolienda(it.Molienda), it.EsDoyPack, descPctManual));
                todoOk = false;
                continue;
            }

            var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
            if (prod is null)
            {
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    it.ProductoId, "?", "?", it.Formato, it.Cantidad, 0m, 0m, 0m, 0m, 0m, 0,
                    false, "Producto no encontrado", NormMolienda(it.Molienda), it.EsDoyPack, descPctManual));
                todoOk = false;
                continue;
            }

            // Validar combinación: formato unitario solo para OTROS, formatos kg solo para CAFE.
            var esCafe = prod.Categoria == "CAFE";
            var esFormatoCafe = it.Formato is "1KG" or "MEDIO" or "CUARTO";
            if (esCafe != esFormatoCafe)
            {
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    prod.Id, prod.Nombre, prod.Categoria, it.Formato, it.Cantidad, 0m, 0m, 0m, 0m, prod.StockGramos, prod.StockUnidades,
                    false, esCafe ? "Para café usá 1 kg / 1/2 kg / 1/4 kg" : "Para otros productos usá 'unidad'",
                    NormMolienda(it.Molienda), it.EsDoyPack, descPctManual));
                todoOk = false;
                continue;
            }

            // Descuento: manual del request si lo hay > 0; si no, el de la matriz reglas (cliente x categoria x marca).
            var descPct = descPctManual;
            if (descPct == 0)
                descPct = ResolverDescuento(prod.Categoria, prod.MarcaId);

            var breakdown = CafePricingService.CalcularPrecioBreakdown(prod, it.Formato, tipo, settings, descPct);
            var precioUnit = breakdown.PrecioLista;     // lista (sin descuento) — lo que se ve en P. Unitario
            var costoUnit = CafePricingService.CalcularCostoUnitario(prod, it.Formato);
            var subtotalLinea = Math.Round(breakdown.PrecioFinal * it.Cantidad, 2, MidpointRounding.AwayFromZero);
            var gramosNecesarios = esCafe ? CafePricingService.GramosPorUnidad(it.Formato) * it.Cantidad : 0m;
            var stockOk = esCafe ? gramosNecesarios <= prod.StockGramos + 0.001m : it.Cantidad <= prod.StockUnidades;
            string? aviso = null;
            if (!stockOk)
            {
                aviso = esCafe
                    ? $"Stock insuficiente. Disponible: {prod.StockGramos:0} g, necesitás {gramosNecesarios:0} g."
                    : $"Stock insuficiente. Disponible: {prod.StockUnidades} u, necesitás {it.Cantidad}.";
                todoOk = false;
            }

            cotizadoItems.Add(new CafeCotizadoItemDto(
                prod.Id, prod.Nombre, prod.Categoria, it.Formato, it.Cantidad,
                precioUnit, costoUnit, subtotalLinea,
                gramosNecesarios, prod.StockGramos, prod.StockUnidades,
                stockOk, aviso,
                NormMolienda(it.Molienda), it.EsDoyPack && esCafe,
                descPct));

            subtotal += subtotalLinea;
            costoTotal += costoUnit * it.Cantidad;
        }

        var desc = Math.Min(subtotal, Math.Abs(descuento));
        var total = Math.Max(0m, subtotal - desc);
        var margen = total - costoTotal;
        return new CafeCotizadoDto(tipo, subtotal, desc, total, costoTotal, margen, todoOk, cotizadoItems);
    }

    private static readonly string[] TiposComprobanteValidos = { "X", "PRO", "FA", "FB", "FC" };
    private static string NormTipoComprobante(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "X";
        var v = s.Trim().ToUpperInvariant();
        return TiposComprobanteValidos.Contains(v) ? v : "X";
    }

    private static readonly string[] CondicionesIvaValidas = { "CF", "RI", "MO", "EX" };
    private static string NormCondicionIva(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "CF";
        var v = s.Trim().ToUpperInvariant();
        return CondicionesIvaValidas.Contains(v) ? v : "CF";
    }

    private static readonly string[] CondicionesPagoValidas = { "EFECTIVO", "TRANSFERENCIA", "DEBITO", "CREDITO", "CTA_CORRIENTE", "CHEQUE", "V_PRIVADO" };
    private static string NormCondicionPago(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "EFECTIVO";
        var v = s.Trim().ToUpperInvariant();
        return CondicionesPagoValidas.Contains(v) ? v : "EFECTIVO";
    }

    private static readonly string[] MoliendasValidas = { "EN GRANOS", "MOLIDO FILTRO", "MOLIDO ESPRESS" };
    private static string? NormMolienda(string? m)
    {
        if (string.IsNullOrWhiteSpace(m)) return null;
        var v = m.Trim().ToUpperInvariant();
        return MoliendasValidas.Contains(v) ? v : null;
    }

    /// <summary>Normaliza CSV de dias: solo deja LUN/MAR/MIE/JUE/VIE/SAB/DOM en mayúscula y sin duplicados.</summary>
    private static readonly string[] DiasValidos = { "LUN", "MAR", "MIE", "JUE", "VIE", "SAB", "DOM" };
    private static string? NormWeekDays(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var list = csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => DiasValidos.Contains(s))
            .Distinct()
            .OrderBy(s => Array.IndexOf(DiasValidos, s))
            .ToList();
        return list.Count == 0 ? null : string.Join(",", list);
    }

    private async Task<string> GenerarNumeroAsync()
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"CAFE-{year}-";
        var existing = await _db.CafeVentas
            .Where(v => v.Numero.StartsWith(prefix))
            .Select(v => v.Numero)
            .ToListAsync();
        int max = 0;
        foreach (var s in existing)
        {
            if (int.TryParse(s.Substring(prefix.Length), out var n) && n > max) max = n;
        }
        return $"{prefix}{(max + 1):D4}";
    }
}
