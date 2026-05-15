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
[Route("api/cafe/ventas")]
[Authorize]
public class CafeVentasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CafeCotizacionPdfService _pdfService;
    private readonly ArcaInvoiceService _arcaInvoiceService;
    private readonly ArcaInvoicePdfService _arcaPdfService;
    private readonly ArcaEmisorService _emisorService;
    private static readonly string[] FormatosValidos = { "1KG", "MEDIO", "CUARTO", "UNIT", "BULTO" };

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

    /// <summary>
    /// Devuelve la fecha CALENDARIO que el usuario quiso poner, sin importar la TZ del cliente.
    /// El bug: cuando el browser ART manda "2026-05-14T00:00:00-03:00", System.Text.Json
    /// lo convierte a UTC = 2026-05-13 21:00 y al hacer .Date salia 13/05 en vez de 14/05.
    /// Fix: si el DateTime viene con TZ (Utc o Local), lo paso a hora ART antes de tomar la fecha.
    /// Si viene Unspecified (sin TZ) lo respeto literal — son los componentes que tipeo el usuario.
    /// Si no viene nada, default = hoy en ART.
    /// </summary>
    private static DateTime FechaArgentina(DateTime? input)
    {
        if (!input.HasValue) return DateTime.UtcNow.AddHours(-3).Date;
        var dt = input.Value;
        if (dt.Kind == DateTimeKind.Unspecified)
            return new DateTime(dt.Year, dt.Month, dt.Day, 0, 0, 0, DateTimeKind.Unspecified);
        // Utc o Local: convertir a hora ART y tomar la fecha
        var utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        return utc.AddHours(-3).Date;
    }

    private static CafeVentaDto Map(CafeVenta v, bool esSaldoMigracion = false) => new(
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
            i.DescuentoPct,
            i.EsConceptoLibre,
            i.EsEnvasePlateado)).ToList(),
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
        v.ArcaError,
        v.OrigenVentaId,
        v.FacturadaComoVentaId,
        esSaldoMigracion);

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var q = _db.CafeVentas.Include(v => v.Items).AsQueryable();
        if (from.HasValue) q = q.Where(v => v.Fecha >= from.Value.Date);
        if (to.HasValue) q = q.Where(v => v.Fecha <= to.Value.Date);
        var list = await q.OrderByDescending(v => v.Fecha).ThenByDescending(v => v.Id).Take(200).ToListAsync();
        // Set de VentaIds asociados a saldos de migracion — para marcar visualmente esas ventas
        // como "🔄 Migración" en el listado del frontend.
        var migrIds = await _db.CafeSaldosMigracion
            .Where(s => s.VentaId != null && s.Estado == "asociado")
            .Select(s => s.VentaId!.Value)
            .ToListAsync();
        var migrSet = new HashSet<int>(migrIds);
        return Ok(list.Select(v => Map(v, migrSet.Contains(v.Id))).ToList());
    }

    public record VentaSaldoDto(int VentaId, decimal Total, decimal Pagado, decimal Saldo);

    /// <summary>
    /// Devuelve el saldo (Total - Pagado) por cada venta visible en el listado.
    /// Pagado = suma de Importes de Cafe_CobranzasComprobantes (cobranzas VIGENTES) que apuntan a esa venta.
    /// Util para mostrar "Pagada" / "Debe $X" en la lista de ventas sin recalcular en cada fila.
    /// </summary>
    [HttpGet("saldos")]
    public async Task<IActionResult> GetSaldos([FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var q = _db.CafeVentas.AsQueryable();
        if (from.HasValue) q = q.Where(v => v.Fecha >= from.Value.Date);
        if (to.HasValue) q = q.Where(v => v.Fecha <= to.Value.Date);
        var ventas = await q.Where(v => v.Estado != "anulado").Select(v => new { v.Id, v.Total }).ToListAsync();
        var ventaIds = ventas.Select(v => v.Id).ToList();
        var pagados = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId != null && ventaIds.Contains(c.VentaId!.Value)
                && c.Cobranza!.Estado == "VIGENTE")
            .GroupBy(c => c.VentaId!.Value)
            .Select(g => new { VentaId = g.Key, Pagado = g.Sum(x => x.Importe) })
            .ToListAsync();
        var dict = pagados.ToDictionary(p => p.VentaId, p => p.Pagado);
        var result = ventas.Select(v => new VentaSaldoDto(
            v.Id, v.Total,
            dict.TryGetValue(v.Id, out var p) ? p : 0m,
            v.Total - (dict.TryGetValue(v.Id, out var p2) ? p2 : 0m)
        )).ToList();
        return Ok(result);
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
        // Include ProductoNav para que el PDF pueda mostrar el SKU del producto al lado del nombre
        var v = await _db.CafeVentas.Include(x => x.Items).ThenInclude(i => i.ProductoNav).FirstOrDefaultAsync(x => x.Id == id);
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

        // Importes para el PDF — fuente de verdad: lo que ARCA registró efectivamente.
        //
        // ESTRATEGIA (post 2026-05-15):
        //   - Si v.ArcaImpTotal está cargado → es la fuente de verdad: lo que ARCA tiene.
        //     Usamos esos campos textuales. Garantiza que PDF == lo declarado en ARCA == lo que
        //     el cliente ve en su CUIT. Imposible que difieran.
        //   - Si está NULL → factura vieja pre-fix. Hacemos fallback al cálculo histórico
        //     desde v.Subtotal (porque en ese momento ARCA recibía sumaItems sin restar descuento
        //     global, y para que el PDF cuadre con ARCA usamos Subtotal).
        decimal neto, ivaImporte, totalConIva;
        if (v.ArcaImpTotal.HasValue)
        {
            neto = v.ArcaImpNeto ?? v.Subtotal;
            ivaImporte = v.ArcaImpIVA ?? 0m;
            totalConIva = v.ArcaImpTotal.Value;
        }
        else
        {
            // Fallback para facturas viejas que no guardaban ArcaImp*. Se usa Subtotal
            // (no Total) porque en ese momento ARCA recibía el subtotal sin descuento global.
            neto = v.Subtotal;
            var ivaPctFallback = letra == "C" ? 0m : 21m;
            ivaImporte = Math.Round(neto * ivaPctFallback / 100m, 2, MidpointRounding.AwayFromZero);
            totalConIva = neto + ivaImporte;
        }
        decimal ivaPct = letra == "C" ? 0m : 21m;

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
            // Extras UX (con prolijidad fiscal — no son obligatorios pero ayudan al lector):
            IsPaid = v.IsPaid,
            TipoClienteTag = v.ClienteTipoSnapshot,
            DiasVisita = v.WeekDays,
            ComentariosCliente = v.ClienteComentariosComprobante,
            Observaciones = v.Observaciones,
            CondicionPago = v.CondicionPago,
        };

        foreach (var it in v.Items)
        {
            var puConDesc = it.DescuentoPct > 0 && it.Cantidad > 0
                ? Math.Round(it.Subtotal / it.Cantidad, 2, MidpointRounding.AwayFromZero)
                : it.PrecioUnitario;
            var desc = it.ProductoNombreSnapshot;
            if (!string.IsNullOrEmpty(it.Molienda)) desc += $" — {it.Molienda}";
            if (it.EsDoyPack) desc += " (d.p.)"; else if (it.EsEnvasePlateado) desc += " (env. plat.)";
            if (!it.EsConceptoLibre) desc += $" · {it.Formato}";

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

    /// <summary>
    /// Genera un PDF de PREVIEW del comprobante sin persistir nada en la base de datos.
    /// Toma los datos del modal en vuelo (cliente, items, tipo, etc.), arma un CafeVenta en
    /// memoria y le pasa el cotizacion al PDFService de cotizaciones. Útil para que el
    /// operador vea exactamente cómo va a quedar antes de emitir.
    /// </summary>
    [HttpPost("preview-pdf")]
    public async Task<IActionResult> PreviewPdf([FromBody] CreateCafeVentaRequest req)
    {
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "Cargá al menos un item para previsualizar." });

        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        var tipo = await ResolverTipoAsync(req.ClienteId, req.ClienteTipoOverride);

        // Cotizamos para conseguir los items con precios calculados.
        var cot = await CotizarInternoAsync(req.Items, tipo, req.Descuento, settings);

        // Resolver datos del cliente (para los snapshots).
        CafeCliente? cli = null;
        if (req.ClienteId.HasValue && req.ClienteId.Value > 0)
            cli = await _db.CafeClientes.FindAsync(req.ClienteId.Value);

        // Armamos un CafeVenta en memoria — NO se persiste.
        var ventaPreview = new CafeVenta
        {
            Id = 0,
            Numero = "PREVIEW",
            Fecha = FechaArgentina(req.Fecha),
            ClienteId = cli?.Id,
            ClienteNombreSnapshot = cli?.Nombre ?? req.ClienteNombreOverride ?? "Consumidor final",
            ClienteTipoSnapshot = tipo,
            ClienteTelefonoSnapshot = cli?.Telefono,
            ClienteRazonSocialSnapshot = cli?.RazonSocial,
            ClienteDomicilioEntregaSnapshot = cli?.DomicilioEntrega,
            ClienteComentariosComprobante = cli?.ComentariosComprobante,
            ClienteCuitSnapshot = cli?.Cuit,
            ClienteDireccionSnapshot = cli?.Direccion,
            ClienteLocalidadSnapshot = cli?.Localidad,
            ClienteCiudadSnapshot = cli?.Ciudad,
            ClienteCpSnapshot = cli?.Cp,
            Subtotal = cot.Subtotal,
            Descuento = cot.Descuento,
            Total = cot.Total,
            CostoTotal = cot.CostoTotal,
            Margen = cot.Margen,
            Observaciones = req.Observaciones,
            Estado = "emitido",
            WeekDays = NormWeekDays(req.WeekDays),
            IsPaid = req.IsPaid,
            TipoComprobante = NormTipoComprobante(req.TipoComprobante),
            CondicionIva = NormCondicionIva(req.CondicionIva),
            CondicionPago = NormCondicionPago(req.CondicionPago),
            CreatedAt = DateTime.UtcNow,
        };
        // Pre-cargo los productos referenciados (con su UxB) para que el PDF tenga el ProductoNav.
        // Sin esto, los items BULTO en el preview pierden la informacion del UxB y no muestran
        // las unidades reales en la columna Cant.
        var prodIds = cot.Items.Select(x => x.ProductoId).Where(id => id > 0).Distinct().ToList();
        var prodsCache = await _db.CafeProductos.Where(p => prodIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        foreach (var ci in cot.Items)
        {
            prodsCache.TryGetValue(ci.ProductoId, out var prodNav);
            ventaPreview.Items.Add(new CafeVentaItem
            {
                ProductoId = ci.ProductoId,
                ProductoNav = prodNav,            // necesario para que el PDF tenga el UxB cuando es BULTO
                ProductoNombreSnapshot = ci.ProductoNombre,
                Categoria = ci.Categoria,
                Formato = ci.Formato,
                Cantidad = ci.Cantidad,
                PrecioUnitario = ci.PrecioUnitario,
                CostoUnitario = ci.CostoUnitario,
                Subtotal = ci.Subtotal,
                GramosDescontados = ci.GramosNecesarios,
                Molienda = NormMolienda(ci.Molienda),
                EsDoyPack = ci.EsDoyPack && ci.Categoria == "CAFE",
                EsEnvasePlateado = ci.EsEnvasePlateado && ci.Categoria == "CAFE" && !ci.EsDoyPack,
                DescuentoPct = ci.DescuentoPct
            });
        }

        var cfg = await _db.CafeSettings.FindAsync(1);
        // Solo usamos el PDF de cotización (incluso si el tipo es FA/FB/FC) — es un PREVIEW,
        // no tiene CAE ni QR todavía. Si querés emitir real, usás el botón Emitir.
        var bytes = _pdfService.GenerarPdfBytes(ventaPreview, cfg);
        return File(bytes, "application/pdf", "preview.pdf");
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
            Fecha = FechaArgentina(req.Fecha),
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
            // Concepto libre: item sin producto del catálogo (no descuenta stock).
            if (it.Categoria == "LIBRE")
            {
                venta.Items.Add(new CafeVentaItem
                {
                    ProductoId = null,
                    EsConceptoLibre = true,
                    ProductoNombreSnapshot = it.ProductoNombre,
                    Categoria = "LIBRE",
                    Formato = "UNIT",
                    Cantidad = it.Cantidad,
                    PrecioUnitario = it.PrecioUnitario,
                    CostoUnitario = 0m,
                    Subtotal = it.Subtotal,
                    GramosDescontados = 0m,
                    Molienda = null,
                    EsDoyPack = false,
                    DescuentoPct = it.DescuentoPct
                });
                continue;
            }

            var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
            if (prod is null) return BadRequest(new { error = $"Producto {it.ProductoId} no encontrado" });

            venta.Items.Add(new CafeVentaItem
            {
                ProductoId = prod.Id,
                EsConceptoLibre = false,
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
                EsEnvasePlateado = it.EsEnvasePlateado && prod.Categoria == "CAFE" && !it.EsDoyPack,
                DescuentoPct = it.DescuentoPct
            });

            // Descontar stock. Si el formato es BULTO, 1 unidad cargada = UxB unidades reales.
            if (prod.Categoria == "CAFE")
                prod.StockGramos = Math.Max(0m, prod.StockGramos - it.GramosNecesarios);
            else
            {
                var unidadesADescontar = it.Formato == "BULTO" ? it.Cantidad * (prod.UxB ?? 1) : it.Cantidad;
                prod.StockUnidades = Math.Max(0, prod.StockUnidades - unidadesADescontar);
            }
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
            //    El descuento por item ya está en it.Subtotal (it.Subtotal/it.Cantidad = precio con desc línea).
            //    PARA EL DESCUENTO GLOBAL DE LA VENTA (venta.Descuento), prorrateamos entre todos los items
            //    así ARCA recibe el importe efectivamente cobrado al cliente (no el subtotal sin descuento).
            //    Fix 2026-05-15: antes no se aplicaba el desc global → ARCA quedaba con un total mayor al
            //    del PDF/cobrado, generando descuadre fiscal en el CUIT del cliente.
            decimal sumaItems = venta.Items.Sum(i => i.Subtotal);
            decimal factorDescGlobal = (venta.Descuento > 0m && sumaItems > 0m)
                ? Math.Max(0m, 1m - (venta.Descuento / sumaItems))
                : 1m;

            var items = new List<EmitirComprobanteItemDto>();
            foreach (var it in venta.Items)
            {
                var puConDescLinea = it.DescuentoPct > 0 && it.Cantidad > 0
                    ? Math.Round(it.Subtotal / it.Cantidad, 2, MidpointRounding.AwayFromZero)
                    : it.PrecioUnitario;
                // Aplicar descuento global prorrateado (si lo hubo) sobre el precio unitario:
                var puFinal = factorDescGlobal < 1m
                    ? Math.Round(puConDescLinea * factorDescGlobal, 2, MidpointRounding.AwayFromZero)
                    : puConDescLinea;

                var desc = it.ProductoNombreSnapshot;
                if (!string.IsNullOrEmpty(it.Molienda)) desc += $" — {it.Molienda}";
                if (it.EsDoyPack) desc += " (d.p.)"; else if (it.EsEnvasePlateado) desc += " (env. plat.)";
                if (!it.EsConceptoLibre) desc += $" · {it.Formato}";

                items.Add(new EmitirComprobanteItemDto
                {
                    Descripcion = desc,
                    Cantidad = it.Cantidad,
                    PrecioUnitario = puFinal,
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
                // Guardar los importes que ARCA registró efectivamente. El PDF los va a usar
                // textualmente para que sea imposible que difieran entre PDF y CUIT del cliente.
                venta.ArcaImpNeto = resultado.ImpNeto;
                venta.ArcaImpIVA = resultado.ImpIVA;
                venta.ArcaImpTotal = resultado.ImpTotal;
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

    /// <summary>
    /// Convierte una venta tipo X / PRO en una factura real (FA/FB/FC) emitida contra ARCA.
    /// Crea una NUEVA venta (con número nuevo), copia los items, descuenta stock,
    /// emite contra ARCA. La venta original (proforma) queda vinculada via FacturadaComoVentaId.
    /// </summary>
    [HttpPost("{id:int}/convertir-a-factura")]
    public async Task<IActionResult> ConvertirAFactura(int id, [FromBody] ConvertirAFacturaRequest req)
    {
        var original = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (original is null) return NotFound(new { error = "Venta no encontrada" });
        if (original.TipoComprobante is not ("X" or "PRO"))
            return BadRequest(new { error = "Solo se pueden convertir cotizaciones (X) y proformas (PRO)." });
        if (original.FacturadaComoVentaId.HasValue)
            return BadRequest(new { error = "Esta proforma ya fue convertida a la factura #" + original.FacturadaComoVentaId });
        if (original.Estado == "anulado")
            return BadRequest(new { error = "No se puede facturar una venta anulada." });

        var tipoNuevo = (req.TipoFactura ?? "").Trim().ToUpperInvariant();
        if (tipoNuevo is not ("FA" or "FB" or "FC"))
            return BadRequest(new { error = "TipoFactura debe ser FA, FB o FC." });

        // Construir CreateCafeVentaRequest con los datos del original.
        var createReq = new CreateCafeVentaRequest
        {
            Fecha = DateTime.Today,
            ClienteId = original.ClienteId,
            ClienteNombreOverride = original.ClienteNombreSnapshot,
            ClienteTipoOverride = original.ClienteTipoSnapshot ?? "OTRO",
            Items = original.Items.Select(i => new CafeCotizarItemRequest
            {
                ProductoId = i.ProductoId ?? 0,
                EsConceptoLibre = i.EsConceptoLibre,
                DescripcionLibre = i.EsConceptoLibre ? i.ProductoNombreSnapshot : null,
                Formato = i.Formato,
                Cantidad = i.Cantidad,
                Molienda = i.Molienda,
                EsDoyPack = i.EsDoyPack,
                EsEnvasePlateado = i.EsEnvasePlateado,
                DescuentoPct = i.DescuentoPct,
                PrecioUnitarioOverride = i.PrecioUnitario  // mantiene el precio exacto del original
            }).ToList(),
            Descuento = original.Descuento,
            Observaciones = original.Observaciones,
            WeekDays = original.WeekDays,
            IsPaid = false,  // empieza impaga, el cobro de la nueva es independiente
            TipoComprobante = tipoNuevo,
            CondicionIva = req.CondicionIva ?? original.CondicionIva,
            CondicionPago = original.CondicionPago,
        };

        // Reusamos la lógica de Create (más simple que duplicar el código)
        var createResult = await Create(createReq);
        if (createResult is not OkObjectResult ok || ok.Value is not CafeVentaDto creada)
            return createResult;

        // Vincular: marcar la proforma como facturada y la nueva como origen=proforma
        original.FacturadaComoVentaId = creada.Id;
        original.UpdatedAt = DateTime.UtcNow;
        var creadaEntity = await _db.CafeVentas.FindAsync(creada.Id);
        if (creadaEntity is not null)
        {
            creadaEntity.OrigenVentaId = original.Id;
            creadaEntity.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();

        return Ok(creada);
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

    /// <summary>
    /// Devuelve un payload pre-armado para duplicar un comprobante. El frontend usa esto
    /// para abrir el modal de "Nueva venta" con los mismos datos (cliente, items, tipo, etc).
    /// NO crea ninguna venta nueva — eso ocurre cuando el usuario confirma desde el modal.
    /// Si el comprobante original era FA/FB/FC con CAE, igual se duplica como si fuera nueva
    /// (sin CAE) — el nuevo va a necesitar su propio CAE al emitirse.
    /// </summary>
    [HttpPost("{id:int}/duplicar")]
    public async Task<IActionResult> Duplicar(int id)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        var items = v.Items.Select(i => new CafeCotizarItemRequest
        {
            ProductoId = i.ProductoId ?? 0,
            Formato = i.Formato,
            Cantidad = i.Cantidad,
            Molienda = i.Molienda,
            EsDoyPack = i.EsDoyPack,
            EsEnvasePlateado = i.EsEnvasePlateado,
            DescuentoPct = i.DescuentoPct,
            EsConceptoLibre = i.EsConceptoLibre,
            DescripcionLibre = i.EsConceptoLibre ? i.ProductoNombreSnapshot : null
        }).ToList();

        var payload = new DuplicarVentaPayloadDto(
            ClienteId: v.ClienteId,
            ClienteNombre: v.ClienteNombreSnapshot,
            ClienteTipo: v.ClienteTipoSnapshot ?? "OTRO",
            TipoComprobante: v.TipoComprobante,
            CondicionIva: v.CondicionIva,
            CondicionPago: v.CondicionPago,
            WeekDays: v.WeekDays,
            Observaciones: v.Observaciones,
            Items: items,
            OrigenNumero: v.Numero);

        return Ok(payload);
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

        // Restaurar stock (concepto libre se saltea, no descontó stock)
        foreach (var it in v.Items)
        {
            if (it.EsConceptoLibre || it.ProductoId is null) continue;
            var prod = await _db.CafeProductos.FindAsync(it.ProductoId.Value);
            if (prod is null) continue;
            if (prod.Categoria == "CAFE")
                prod.StockGramos += it.GramosDescontados;
            else
            {
                // Si el formato fue BULTO, devolver cantidad × UxB unidades.
                var unidadesADevolver = it.Formato == "BULTO" ? it.Cantidad * (prod.UxB ?? 1) : it.Cantidad;
                prod.StockUnidades += unidadesADevolver;
            }
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

        if (req.Fecha.HasValue) v.Fecha = FechaArgentina(req.Fecha);
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
                // 1. Devolver stock de los items actuales. Concepto libre se saltea (no descontó).
                foreach (var item in v.Items)
                {
                    if (item.EsConceptoLibre || item.ProductoId is null) continue;
                    var prod = await _db.CafeProductos.FindAsync(item.ProductoId.Value);
                    if (prod is null) continue;
                    if (prod.Categoria == "CAFE") prod.StockGramos += item.GramosDescontados;
                    else
                    {
                        var unidadesADevolver = item.Formato == "BULTO" ? item.Cantidad * (prod.UxB ?? 1) : item.Cantidad;
                        prod.StockUnidades += unidadesADevolver;
                    }
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
                        EsEnvasePlateado = ci.EsEnvasePlateado && prod.Categoria == "CAFE" && !ci.EsDoyPack,
                        DescuentoPct = ci.DescuentoPct
                    });
                    if (prod.Categoria == "CAFE")
                        prod.StockGramos = Math.Max(0m, prod.StockGramos - ci.GramosNecesarios);
                    else
                    {
                        var unidadesADescontar = ci.Formato == "BULTO" ? ci.Cantidad * (prod.UxB ?? 1) : ci.Cantidad;
                        prod.StockUnidades = Math.Max(0, prod.StockUnidades - unidadesADescontar);
                    }
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
        // Si estaba emitida, restaurar stock antes de borrar. Concepto libre se saltea.
        if (v.Estado == "emitido")
        {
            foreach (var it in v.Items)
            {
                if (it.EsConceptoLibre || it.ProductoId is null) continue;
                var prod = await _db.CafeProductos.FindAsync(it.ProductoId.Value);
                if (prod is null) continue;
                if (prod.Categoria == "CAFE") prod.StockGramos += it.GramosDescontados;
                else
                {
                    var unidadesADevolver = it.Formato == "BULTO" ? it.Cantidad * (prod.UxB ?? 1) : it.Cantidad;
                    prod.StockUnidades += unidadesADevolver;
                }
            }
        }
        // Desvincular las cobranzas que referenciaban esta venta (les ponemos VentaId=null = quedan
        // como "a cuenta"). NO borramos las cobranzas porque ya entraron plata real a una caja.
        var cobranzasItems = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId == v.Id)
            .ToListAsync();
        foreach (var cc in cobranzasItems)
            cc.VentaId = null;
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

        // NOTA: la matriz Cafe_ReglasPrecios fue deprecada el 2026-05-12.
        // Los descuentos automaticos se eliminaron — ahora cada producto tiene PrecioBar/PrecioOtro directos.
        // Si llega un descuento manual desde la UI (it.DescuentoPct), se aplica como descuento de linea
        // y nada mas. Sin lookup de matriz.

        foreach (var it in items)
        {
            if (it.Cantidad <= 0) continue;
            // Descuento manual override. Si viene 0 desde el request, se calcula automaticamente
            // de la matriz por tipo cliente x marca del producto.
            var descPctManual = Math.Clamp(it.DescuentoPct, 0m, 100m);

            // ---- Concepto libre: item manual sin producto del catálogo ----
            // Usa DescripcionLibre + PrecioUnitarioOverride. No toca stock ni reglas.
            if (it.EsConceptoLibre)
            {
                var descLibre = string.IsNullOrWhiteSpace(it.DescripcionLibre) ? "Concepto libre" : it.DescripcionLibre!.Trim();
                var pcuLibre = it.PrecioUnitarioOverride.HasValue && it.PrecioUnitarioOverride.Value >= 0m
                    ? Math.Round(it.PrecioUnitarioOverride.Value, 2, MidpointRounding.AwayFromZero)
                    : 0m;
                var finalLibre = Math.Round(pcuLibre * (1m - descPctManual / 100m), 2, MidpointRounding.AwayFromZero);
                var subLibre = Math.Round(finalLibre * it.Cantidad, 2, MidpointRounding.AwayFromZero);
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    ProductoId: 0,
                    ProductoNombre: descLibre,
                    Categoria: "LIBRE",
                    Formato: "UNIT",
                    Cantidad: it.Cantidad,
                    PrecioUnitario: pcuLibre,
                    CostoUnitario: 0m,
                    Subtotal: subLibre,
                    GramosNecesarios: 0m,
                    StockGramosDisponible: 0m,
                    StockUnidadesDisponible: 0,
                    StockOk: true,
                    Aviso: null,
                    Molienda: null,
                    EsDoyPack: false,
                    DescuentoPct: descPctManual));
                subtotal += subLibre;
                continue;
            }

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

            // Validar combinación: formato unitario / bulto solo para OTROS, formatos kg solo para CAFE.
            var esCafe = prod.Categoria == "CAFE";
            var esFormatoCafe = it.Formato is "1KG" or "MEDIO" or "CUARTO";
            var esFormatoBulto = it.Formato == "BULTO";
            if (esCafe != esFormatoCafe)
            {
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    prod.Id, prod.Nombre, prod.Categoria, it.Formato, it.Cantidad, 0m, 0m, 0m, 0m, prod.StockGramos, prod.StockUnidades,
                    false, esCafe ? "Para café usá 1 kg / 1/2 kg / 1/4 kg" : "Para otros productos usá 'unidad' o 'bulto'",
                    NormMolienda(it.Molienda), it.EsDoyPack, descPctManual));
                todoOk = false;
                continue;
            }
            // BULTO requiere producto OTROS con UxB cargado y al menos un precio de bulto cargado.
            if (esFormatoBulto && (!prod.UxB.HasValue || prod.UxB.Value <= 0 || (!prod.PrecioBulto.HasValue && !prod.PrecioBultoOtro.HasValue)))
            {
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    prod.Id, prod.Nombre, prod.Categoria, it.Formato, it.Cantidad, 0m, 0m, 0m, 0m, prod.StockGramos, prod.StockUnidades,
                    false, "Este producto no tiene precio de bulto cargado",
                    NormMolienda(it.Molienda), it.EsDoyPack, descPctManual));
                todoOk = false;
                continue;
            }

            // Descuento de linea: solo el manual del request. Sin matriz automatica.
            var descPct = descPctManual;

            var breakdown = CafePricingService.CalcularPrecioBreakdown(prod, it.Formato, tipo, settings, descPct);
            var precioUnit = breakdown.PrecioLista;     // lista (sin descuento) — lo que se ve en P. Unitario
            var precioFinal = breakdown.PrecioFinal;
            // Override manual: si el operador pisó el precio a mano, ese valor pasa a ser la "lista"
            // (lo que muestra arriba) y se le aplica el descuento de la línea como siempre.
            if (it.PrecioUnitarioOverride is decimal override_ && override_ >= 0m)
            {
                precioUnit = Math.Round(override_, 2, MidpointRounding.AwayFromZero);
                precioFinal = Math.Round(precioUnit * (1m - descPct / 100m), 2, MidpointRounding.AwayFromZero);
            }
            var costoUnit = CafePricingService.CalcularCostoUnitario(prod, it.Formato);
            // Aplica descuento por bulto si corresponde (producto OTROS con UxB + PrecioBulto cargados
            // y cantidad >= UxB). Si no aplica, subtotal = cantidad × precioFinal.
            var subtotalLinea = CafePricingService.CalcularSubtotalConBulto(prod, tipo, precioFinal, it.Cantidad);
            var gramosNecesarios = esCafe ? CafePricingService.GramosPorUnidad(it.Formato) * it.Cantidad : 0m;
            // Si es BULTO, una "unidad" cargada = UxB unidades reales de stock. Sino, 1 a 1.
            var unidadesNecesarias = esFormatoBulto ? it.Cantidad * (prod.UxB ?? 0) : it.Cantidad;
            var stockOk = esCafe ? gramosNecesarios <= prod.StockGramos + 0.001m : unidadesNecesarias <= prod.StockUnidades;
            string? aviso = null;
            if (!stockOk)
            {
                aviso = esCafe
                    ? $"Stock insuficiente. Disponible: {prod.StockGramos:0} g, necesitás {gramosNecesarios:0} g."
                    : $"Stock insuficiente. Disponible: {prod.StockUnidades} u, necesitás {unidadesNecesarias} u ({(esFormatoBulto ? $"{it.Cantidad} bulto×{prod.UxB}" : $"{it.Cantidad}")}).";
                todoOk = false;
            }

            cotizadoItems.Add(new CafeCotizadoItemDto(
                prod.Id, prod.Nombre, prod.Categoria, it.Formato, it.Cantidad,
                precioUnit, costoUnit, subtotalLinea,
                gramosNecesarios, prod.StockGramos, prod.StockUnidades,
                stockOk, aviso,
                NormMolienda(it.Molienda), it.EsDoyPack && esCafe,
                descPct,
                it.EsEnvasePlateado && esCafe && !it.EsDoyPack));

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

    private static readonly string[] MoliendasValidas = { "EN GRANOS", "MOLIDO FILTRO", "MOLIDO ESPRESS", "MOLIDO MOKA", "MOLIDO BODUM", "MINI EXPRESS" };
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

    // ============================================================
    // EXPORT — productos vendidos agrupados, descargable en Excel
    // ============================================================

    /// <summary>Devuelve un Excel con TODOS los productos vendidos en el rango indicado, agrupados
    /// por (SKU, Producto, Formato) y sumando cantidades y montos. Si el usuario quiere el detalle
    /// venta-por-venta usa includeDetalle=true (segunda hoja). Pensado para que el dueño baje los
    /// articulos vendidos y los importe en otro sistema (Contabilium / contadora). Solo cuenta
    /// ventas EMITIDAS (no anuladas).</summary>
    [HttpGet("export/productos-vendidos")]
    public async Task<IActionResult> ExportProductosVendidos(
        [FromQuery] DateTime? desde = null,
        [FromQuery] DateTime? hasta = null,
        [FromQuery] bool includeDetalle = true)
    {
        // Default: este mes
        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        var d = (desde ?? new DateTime(hoy.Year, hoy.Month, 1)).Date;
        var h = (hasta ?? hoy).Date;
        if (h < d) (d, h) = (h, d); // swap si vino al revés

        // Traer items de ventas EMITIDAS en el rango
        var items = await _db.CafeVentaItems
            .Include(i => i.VentaNav)
            .Include(i => i.ProductoNav)
            .Where(i => i.VentaNav != null && i.VentaNav.Estado == "emitido"
                     && i.VentaNav.Fecha >= d && i.VentaNav.Fecha <= h)
            .ToListAsync();

        using var wb = new XLWorkbook();

        // ===== Hoja 1: AGRUPADO por producto =====
        var ws = wb.Worksheets.Add("Productos vendidos");
        ws.Cell(1, 1).Value = "Productos vendidos";
        ws.Range(1, 1, 1, 8).Merge().Style.Font.SetBold(true).Font.SetFontSize(14);
        ws.Cell(2, 1).Value = $"Período: {d:dd/MM/yyyy} a {h:dd/MM/yyyy}";
        ws.Range(2, 1, 2, 8).Merge().Style.Font.SetItalic(true).Font.SetFontColor(XLColor.DarkGray);

        var headerRow = 4;
        var headers = new[] { "SKU", "Producto", "Categoría", "Formato", "Cant. total", "Subtotal", "Costo", "Margen" };
        for (int i = 0; i < headers.Length; i++)
        {
            var c = ws.Cell(headerRow, i + 1);
            c.Value = headers[i];
            c.Style.Font.SetBold(true);
            c.Style.Fill.SetBackgroundColor(XLColor.LightGray);
            c.Style.Border.SetBottomBorder(XLBorderStyleValues.Thin);
        }

        var agrupado = items
            .Where(i => !i.EsConceptoLibre)
            .GroupBy(i => new {
                Sku = i.ProductoNav?.Sku ?? "(sin sku)",
                Nombre = i.ProductoNombreSnapshot,
                Categoria = i.Categoria,
                Formato = i.Formato })
            .Select(g => new
            {
                g.Key.Sku, g.Key.Nombre, g.Key.Categoria, g.Key.Formato,
                CantTotal = g.Sum(x => x.Cantidad),
                Subtotal = g.Sum(x => x.Subtotal),
                Costo = g.Sum(x => x.CostoUnitario * x.Cantidad),
                Margen = g.Sum(x => x.Subtotal - x.CostoUnitario * x.Cantidad)
            })
            .OrderBy(x => x.Sku).ThenBy(x => x.Nombre)
            .ToList();

        int row = headerRow + 1;
        foreach (var a in agrupado)
        {
            ws.Cell(row, 1).Value = a.Sku;
            ws.Cell(row, 2).Value = a.Nombre;
            ws.Cell(row, 3).Value = a.Categoria;
            ws.Cell(row, 4).Value = FormatoNombre(a.Formato);
            ws.Cell(row, 5).Value = a.CantTotal;
            ws.Cell(row, 6).Value = a.Subtotal; ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 7).Value = a.Costo;    ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00";
            ws.Cell(row, 8).Value = a.Margen;   ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00";
            row++;
        }

        // Fila TOTAL
        if (agrupado.Any())
        {
            ws.Cell(row, 1).Value = "TOTAL";
            ws.Cell(row, 1).Style.Font.SetBold(true);
            ws.Range(row, 1, row, 4).Merge().Style.Font.SetBold(true);
            ws.Cell(row, 5).Value = agrupado.Sum(a => a.CantTotal); ws.Cell(row, 5).Style.Font.SetBold(true);
            ws.Cell(row, 6).Value = agrupado.Sum(a => a.Subtotal); ws.Cell(row, 6).Style.NumberFormat.Format = "#,##0.00"; ws.Cell(row, 6).Style.Font.SetBold(true);
            ws.Cell(row, 7).Value = agrupado.Sum(a => a.Costo);    ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0.00"; ws.Cell(row, 7).Style.Font.SetBold(true);
            ws.Cell(row, 8).Value = agrupado.Sum(a => a.Margen);   ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0.00"; ws.Cell(row, 8).Style.Font.SetBold(true);
            ws.Range(row, 1, row, 8).Style.Fill.SetBackgroundColor(XLColor.LightYellow);
        }

        ws.Columns().AdjustToContents();
        ws.SheetView.FreezeRows(headerRow);

        // ===== Hoja 2: DETALLE línea por línea =====
        if (includeDetalle && items.Any())
        {
            var ws2 = wb.Worksheets.Add("Detalle por venta");
            var det = new[] { "Fecha", "N° Venta", "Cliente", "SKU", "Producto", "Categoría", "Formato", "Cant.", "P. Unit.", "Desc %", "Subtotal", "Tipo" };
            for (int i = 0; i < det.Length; i++)
            {
                var c = ws2.Cell(1, i + 1);
                c.Value = det[i];
                c.Style.Font.SetBold(true);
                c.Style.Fill.SetBackgroundColor(XLColor.LightGray);
            }
            int r2 = 2;
            foreach (var it in items.OrderBy(x => x.VentaNav!.Fecha).ThenBy(x => x.VentaNav!.Numero))
            {
                ws2.Cell(r2, 1).Value = it.VentaNav!.Fecha; ws2.Cell(r2, 1).Style.DateFormat.Format = "dd/MM/yyyy";
                ws2.Cell(r2, 2).Value = it.VentaNav!.Numero;
                ws2.Cell(r2, 3).Value = it.VentaNav!.ClienteNombreSnapshot ?? "—";
                ws2.Cell(r2, 4).Value = it.ProductoNav?.Sku ?? "";
                ws2.Cell(r2, 5).Value = it.ProductoNombreSnapshot;
                ws2.Cell(r2, 6).Value = it.Categoria;
                ws2.Cell(r2, 7).Value = FormatoNombre(it.Formato);
                ws2.Cell(r2, 8).Value = it.Cantidad;
                ws2.Cell(r2, 9).Value = it.PrecioUnitario; ws2.Cell(r2, 9).Style.NumberFormat.Format = "#,##0.00";
                ws2.Cell(r2, 10).Value = it.DescuentoPct;
                ws2.Cell(r2, 11).Value = it.Subtotal; ws2.Cell(r2, 11).Style.NumberFormat.Format = "#,##0.00";
                ws2.Cell(r2, 12).Value = it.VentaNav!.TipoComprobante;
                r2++;
            }
            ws2.Columns().AdjustToContents();
            ws2.SheetView.FreezeRows(1);
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var bytes = ms.ToArray();
        var filename = $"productos-vendidos_{d:yyyyMMdd}_{h:yyyyMMdd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
    }

    private static string FormatoNombre(string f) => f switch
    {
        "1KG" => "1 kg",
        "MEDIO" => "1/2 kg",
        "CUARTO" => "1/4 kg",
        "UNIT" => "Unidad",
        "BULTO" => "Bulto",
        _ => f
    };
}
