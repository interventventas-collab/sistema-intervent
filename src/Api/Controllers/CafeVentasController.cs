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
    private readonly IntegrationService _integrationService;
    private readonly WhatsAppService _whatsAppService;
    private readonly QrRepartidorService _qrRepartidorService;
    private readonly CafeReciboVisitaCobranzaPdfService _reciboVisitaPdfService;
    private readonly CafeReciboEntregaPdfService _reciboEntregaPdfService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CafeStockLogger _stockLogger;
    private static readonly string[] FormatosValidos = { "1KG", "MEDIO", "CUARTO", "UNIT", "BULTO" };

    /// <summary>Valida formato: los fijos de FormatosValidos o PACK_N (N entero positivo).</summary>
    private static bool IsFormatoValido(string? formato)
    {
        if (string.IsNullOrEmpty(formato)) return false;
        if (FormatosValidos.Contains(formato)) return true;
        return CafePricingService.ParsePackUnidades(formato).HasValue;
    }

    /// <summary>Unidades reales que descuentan stock para una linea OTROS:
    /// PACK_N → cantidad × N, BULTO → cantidad × UxB, sino cantidad.</summary>
    private static int UnidadesRealesStock(CafeProducto prod, string formato, int cantidad)
    {
        var packN = CafePricingService.ParsePackUnidades(formato);
        if (packN.HasValue) return cantidad * packN.Value;
        if (formato == "BULTO") return cantidad * (prod.UxB ?? 1);
        return cantidad;
    }

    public CafeVentasController(
        AppDbContext db,
        CafeCotizacionPdfService pdfService,
        ArcaInvoiceService arcaInvoiceService,
        ArcaInvoicePdfService arcaPdfService,
        ArcaEmisorService emisorService,
        IntegrationService integrationService,
        WhatsAppService whatsAppService,
        QrRepartidorService qrRepartidorService,
        CafeReciboVisitaCobranzaPdfService reciboVisitaPdfService,
        CafeReciboEntregaPdfService reciboEntregaPdfService,
        IServiceScopeFactory scopeFactory,
        CafeStockLogger stockLogger)
    {
        _db = db;
        _pdfService = pdfService;
        _arcaInvoiceService = arcaInvoiceService;
        _arcaPdfService = arcaPdfService;
        _emisorService = emisorService;
        _integrationService = integrationService;
        _whatsAppService = whatsAppService;
        _qrRepartidorService = qrRepartidorService;
        _reciboVisitaPdfService = reciboVisitaPdfService;
        _reciboEntregaPdfService = reciboEntregaPdfService;
        _scopeFactory = scopeFactory;
        _stockLogger = stockLogger;
    }

    /// <summary>2026-06-16: completa el cfg con datos de la ficha del Emisor ARCA (un solo lugar de carga).
    /// Si /integraciones tiene cargado Telefono/Telefono2/Email/Web/Web2 y el cfg no, los completa con la ficha.
    /// Asi alcanza con cargar los datos UNA SOLA VEZ (en la ficha Emisor) y aplican tanto a facturas como a
    /// cotizaciones tipo X. Si el cfg ya tiene un valor cargado, prevalece.</summary>
    [NonAction]
    public async Task HydrateCfgFromEmisorAsync(CafeSetting? cfg)
    {
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.NegocioCuit)) return;
        try
        {
            var ficha = await _emisorService.GetEntityByCuitAsync(cfg.NegocioCuit);
            if (ficha is null) return;
            if (string.IsNullOrWhiteSpace(cfg.NegocioTelefono)) cfg.NegocioTelefono = ficha.Telefono;
            if (string.IsNullOrWhiteSpace(cfg.NegocioTelefono2)) cfg.NegocioTelefono2 = ficha.Telefono2;
            if (string.IsNullOrWhiteSpace(cfg.NegocioEmail)) cfg.NegocioEmail = ficha.Email;
            if (string.IsNullOrWhiteSpace(cfg.NegocioWeb)) cfg.NegocioWeb = ficha.Web;
            if (string.IsNullOrWhiteSpace(cfg.NegocioWeb2)) cfg.NegocioWeb2 = ficha.Web2;
        }
        catch { /* si falla la lectura, dejamos cfg como está */ }
    }

    /// <summary>Dispara push de stock a MeLi en background (fire-and-forget). No bloquea el response.
    /// Si MeLi falla, queda marcado StockChangedAt y el job de respaldo lo recupera en max 15 min.</summary>
    private void FireAndForgetPushMeli(List<int> cafeProductoIds)
    {
        if (cafeProductoIds is null || cafeProductoIds.Count == 0) return;
        var scopeFactory = _scopeFactory;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var pushSvc = scope.ServiceProvider.GetRequiredService<MeliStockPushService>();
                foreach (var pid in cafeProductoIds)
                {
                    try { await pushSvc.PushStockForProductoAsync(pid); }
                    catch { /* errores capturados por el service, marca queda en StockChangedAt */ }
                }
            }
            catch { /* el job de respaldo lo recupera */ }
        });
    }

    /// <summary>Genera el PDF "Recibo de Visita por Cobranza" — para imprimir cuando el
    /// repartidor va solo a cobrar un comprobante antiguo. Pedido del usuario 2026-05-20.</summary>
    [HttpGet("{id:int}/recibo-visita.pdf")]
    public async Task<IActionResult> GetReciboVisitaPdf(int id)
    {
        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound();
        // Asegurar token publico para el QR
        if (string.IsNullOrEmpty(v.PublicToken))
        {
            v.PublicToken = GeneratePublicToken();
            await _db.SaveChangesAsync();
        }
        // Calcular saldo pendiente (cobrable - pagado)
        var totalCobrable = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
        var pagado = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId == v.Id && c.Cobranza!.Estado == "VIGENTE")
            .SumAsync(c => (decimal?)c.Importe) ?? 0m;
        var saldo = Math.Max(0m, totalCobrable - pagado);
        var cfg = await _db.CafeSettings.FindAsync(1);
        var qr = await _qrRepartidorService.GenerarQrAsync(v.PublicToken);
        var bytes = _reciboVisitaPdfService.GenerarPdf(v, saldo, cfg, qr);
        return File(bytes, "application/pdf", $"VIS-{DateTime.Now.Year:0000}-{v.Id:0000}.pdf");
    }

    /// <summary>2026-06-22: Genera el PDF "Recibo de Entrega" para CUALQUIER venta (entregada o no).
    /// Si esta entregada: incluye fecha/hora + repartidor. Si tiene firma: la incluye como imagen.
    /// Si no esta entregada o no tiene firma, esas secciones se omiten o muestran espacio en blanco
    /// (asi el repartidor puede imprimir el recibo y firmarlo en papel a mano si hace falta).</summary>
    [HttpGet("{id:int}/recibo-entrega.pdf")]
    public async Task<IActionResult> GetReciboEntregaPdf(int id)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        var cliente = v.ClienteId.HasValue
            ? await _db.CafeClientes.FirstOrDefaultAsync(c => c.Id == v.ClienteId.Value)
            : null;
        var repartidor = v.EntregadoPorRepartidorId.HasValue
            ? await _db.CafeRepartidores.FirstOrDefaultAsync(r => r.Id == v.EntregadoPorRepartidorId.Value)
            : null;
        var cfg = await _db.CafeSettings.FindAsync(1);
        await HydrateCfgFromEmisorAsync(cfg);

        var bytes = _reciboEntregaPdfService.GenerarPdfBytes(v, cliente, repartidor, cfg);
        var filename = $"RecEntrega-{v.Numero}.pdf";
        return File(bytes, "application/pdf", filename);
    }

    /// <summary>
    /// Devuelve la fecha CALENDARIO que el usuario quiso poner, sin importar la TZ del cliente.
    /// El bug: cuando el browser ART manda "2026-05-14T00:00:00-03:00", System.Text.Json
    /// lo convierte a UTC = 2026-05-13 21:00 y al hacer .Date salia 13/05 en vez de 14/05.
    /// Fix: si el DateTime viene con TZ (Utc o Local), lo paso a hora ART antes de tomar la fecha.
    /// Si viene Unspecified (sin TZ) lo respeto literal — son los componentes que tipeo el usuario.
    /// Si no viene nada, default = hoy en ART.
    /// </summary>
    /// <summary>
    /// Arma el nombre de archivo descriptivo del PDF del comprobante:
    /// "{Tipo} - {Cliente} - {Direccion} - {Fecha}.pdf"
    /// Ej: "Factura A - DULCE LUGAR SRL - PERON 1891 - 2026-05-18.pdf"
    /// Pedido del usuario 2026-05-18: cuando descarga un comprobante, que el nombre
    /// del archivo le diga de un vistazo qué es y a quién. Antes era solo "CAFE-2026-0131.pdf".
    /// Sanitiza chars inválidos para filesystem y trunca para no pasarse del límite (~200 chars).
    /// </summary>
    [NonAction]
    public static string BuildPdfFilename(CafeVenta v)
    {
        var tipo = v.TipoComprobante switch
        {
            "FA" => "FA",
            "FB" => "FB",
            "FC" => "FC",
            "X" => "X",
            "PRO" => "PROF",
            _ => v.TipoComprobante ?? "Comprobante"
        };
        var cliente = !string.IsNullOrWhiteSpace(v.ClienteRazonSocialSnapshot)
            ? v.ClienteRazonSocialSnapshot!
            : (v.ClienteNombreSnapshot ?? "Consumidor Final");
        var direccion = !string.IsNullOrWhiteSpace(v.ClienteDomicilioEntregaSnapshot)
            ? v.ClienteDomicilioEntregaSnapshot!
            : (v.ClienteDireccionSnapshot ?? "");
        var fecha = v.Fecha.ToString("yyyy-MM-dd");

        // Sanitizar cada parte: chars invalidos en filesystem (/ \ : * ? " < > |) → espacio.
        // Tambien colapsar espacios multiples y truncar para no romper el limite del FS.
        static string Sanitize(string s, int maxLen)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            var invalid = new HashSet<char>(System.IO.Path.GetInvalidFileNameChars()) { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
            var clean = new string(s.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
            clean = System.Text.RegularExpressions.Regex.Replace(clean, @"\s+", " ").Trim();
            return clean.Length > maxLen ? clean.Substring(0, maxLen).Trim() : clean;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(tipo)) parts.Add(Sanitize(tipo, 30));
        var clienteSan = Sanitize(cliente, 60);
        if (!string.IsNullOrWhiteSpace(clienteSan)) parts.Add(clienteSan);
        var direccionSan = Sanitize(direccion, 60);
        if (!string.IsNullOrWhiteSpace(direccionSan)) parts.Add(direccionSan);
        parts.Add(fecha);

        var name = string.Join(" - ", parts);
        return name + ".pdf";
    }

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

    private class EscaneoRow { public int VentaId { get; set; } public DateTime CreatedAt { get; set; } public string Nombre { get; set; } = ""; }

    private static CafeVentaDto Map(CafeVenta v, bool esSaldoMigracion = false, string? entregadoPorRepartidorNombre = null,
        string? escaneadoPorRepartidorNombre = null, DateTime? escaneadoAt = null,
        int? clienteCodigoInterno = null,
        decimal? cobradoEnEntrega = null) => new(
        v.Id, v.Numero, v.Fecha,
        v.ClienteId, v.ClienteNombreSnapshot, v.ClienteTipoSnapshot, v.ClienteTelefonoSnapshot,
        clienteCodigoInterno,  // 2026-06-08: codigo interno del cliente
        v.Subtotal, v.Descuento, v.Total, v.CostoTotal, v.Margen,
        v.Observaciones, v.Estado,
        v.WeekDays, v.EnRadar, v.IsPaid, v.Retira,
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
            i.EsEnvasePlateado,
            // 2026-06-08: marca de combo origen. El frontend resuelve nombre/sku desde su catálogo
            // de combos en memoria; los PDFs hacen una query puntual al generar el documento.
            i.ComboOrigenId,
            i.ComboOrigenNav?.Nombre,
            i.ComboOrigenNav?.Sku)).ToList(),
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
        esSaldoMigracion,
        v.PinNota,
        v.PublicToken,
        v.EntregaPor,
        v.EstadoPreparacion,
        v.PreparacionUpdatedAt,
        v.ArcaImpTotal,
        v.EntregadoPorRepartidorId,
        entregadoPorRepartidorNombre,
        v.EntregadoAt,
        v.DriveFileId,
        v.DriveSubidoAt,
        v.DriveSubidasCount,
        v.ComentarioArmado,
        escaneadoPorRepartidorNombre,
        escaneadoAt,
        v.PorTransporte,
        v.TransporteEmpresa,
        v.TransporteDestino,
        v.CreadoPorOperador,
        // 2026-06-23: Concepto AFIP
        v.Concepto,
        v.ConceptoServDesde,
        v.ConceptoServHasta,
        v.MapeoLink,
        v.ArcaWebserviceAccountId,
        cobradoEnEntrega);

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] int? limit = null,
        [FromQuery] int offset = 0)
    {
        var q = _db.CafeVentas.Include(v => v.Items).AsQueryable();
        if (from.HasValue) q = q.Where(v => v.Fecha >= from.Value.Date);
        if (to.HasValue) q = q.Where(v => v.Fecha <= to.Value.Date);

        // 2026-06-22: paginacion server-side. Si limit es null, trae todo (caso historico y
        // para usos como exportar Excel). Si limit tiene valor, aplica Skip/Take y devuelve
        // el total count en el header X-Total-Count para que el frontend pueda mostrar el
        // paginador. Cuando hay fechas, se ignora la paginacion (la query de rango ya filtra
        // y el usuario espera ver todo lo que pidio para ese rango).
        var ordered = q.OrderByDescending(v => v.Fecha).ThenByDescending(v => v.Id);
        bool aplicarPaginacion = limit.HasValue && limit.Value > 0 && !from.HasValue && !to.HasValue;
        if (aplicarPaginacion)
        {
            var totalCount = await q.CountAsync();
            Response.Headers["X-Total-Count"] = totalCount.ToString();
            Response.Headers["Access-Control-Expose-Headers"] = "X-Total-Count";
        }
        var list = aplicarPaginacion
            ? await ordered.Skip(Math.Max(0, offset)).Take(limit!.Value).ToListAsync()
            : await ordered.ToListAsync();
        // Set de VentaIds asociados a saldos de migracion — para marcar visualmente esas ventas
        // como "🔄 Migración" en el listado del frontend.
        var migrIds = await _db.CafeSaldosMigracion
            .Where(s => s.VentaId != null && s.Estado == "asociado")
            .Select(s => s.VentaId!.Value)
            .ToListAsync();
        var migrSet = new HashSet<int>(migrIds);
        // Pre-cargar nombres de repartidores para las ventas que tienen EntregadoPorRepartidorId
        var repIds = list.Where(v => v.EntregadoPorRepartidorId.HasValue).Select(v => v.EntregadoPorRepartidorId!.Value).Distinct().ToList();
        var repsDict = repIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.CafeRepartidores.Where(r => repIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Nombre);

        // 2026-06-05: precargar el repartidor que ESCANEO cada venta (lo cargo a su lista
        // pero todavia no necesariamente la entrego). Tomamos el escaneo "cargado" mas
        // reciente por venta.
        var ventaIds = list.Select(v => v.Id).ToList();
        var escaneosRaw = ventaIds.Count == 0
            ? new List<EscaneoRow>()
            : await _db.CafeQrEscaneos
                .Where(e => ventaIds.Contains(e.VentaId) && e.Accion == "cargado")
                .Join(_db.CafeRepartidores, e => e.RepartidorId, r => r.Id,
                    (e, r) => new EscaneoRow { VentaId = e.VentaId, CreatedAt = e.CreatedAt, Nombre = r.Nombre })
                .ToListAsync();
        var escDic = escaneosRaw
            .GroupBy(x => x.VentaId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.CreatedAt).First());

        // 2026-06-08: precargar el CodigoInterno del cliente (si existe) para mostrarlo
        // como "(#123)" al lado del nombre en el listado de ventas.
        var clienteIdsParaCodigo = list.Where(v => v.ClienteId.HasValue).Select(v => v.ClienteId!.Value).Distinct().ToList();
        var codigosDict = clienteIdsParaCodigo.Count == 0
            ? new Dictionary<int, int?>()
            : await _db.CafeClientes
                .Where(c => clienteIdsParaCodigo.Contains(c.Id))
                .Select(c => new { c.Id, c.CodigoInterno })
                .ToDictionaryAsync(c => c.Id, c => c.CodigoInterno);

        // 2026-07-03: precargar cuanta plata trajo el repartidor por cada venta (suma de
        // CafeCobranzasPendientes no rechazadas). Se muestra como "💰 Cobró $X" en el chip
        // verde de entrega. RECHAZADA se excluye porque el admin la rechazo (no es plata).
        var cobradoDict = ventaIds.Count == 0
            ? new Dictionary<int, decimal>()
            : await _db.CafeCobranzasPendientes
                .Where(p => ventaIds.Contains(p.VentaId) && p.Estado != "RECHAZADA")
                .GroupBy(p => p.VentaId)
                .Select(g => new { VentaId = g.Key, Total = g.Sum(x => x.Importe) })
                .ToDictionaryAsync(x => x.VentaId, x => x.Total);

        return Ok(list.Select(v => Map(v, migrSet.Contains(v.Id),
            v.EntregadoPorRepartidorId.HasValue && repsDict.TryGetValue(v.EntregadoPorRepartidorId.Value, out var nm) ? nm : null,
            escDic.TryGetValue(v.Id, out var esc) ? esc.Nombre : null,
            escDic.TryGetValue(v.Id, out var esc2) ? esc2.CreatedAt : (DateTime?)null,
            v.ClienteId.HasValue && codigosDict.TryGetValue(v.ClienteId.Value, out var ci) ? ci : null,
            cobradoDict.TryGetValue(v.Id, out var cob) ? cob : (decimal?)null
        )).ToList());
    }

    /// <summary>Devuelve TODAS las ventas tipo FA/FB/FC que NO estan autorizadas en ARCA
    /// (estado pendiente, rechazado, o cualquier otro distinto a "autorizado"). Sirve para
    /// la pantalla 'Errores ARCA' donde el operador puede ver de un vistazo que facturas
    /// quedaron colgadas y reintentarlas. Devuelve hasta 500 ventas, ordenado por fecha desc.</summary>
    [HttpGet("arca/errores")]
    public async Task<IActionResult> GetArcaErrores()
    {
        var ventas = await _db.CafeVentas.Include(v => v.Items)
            .Where(v => (v.TipoComprobante == "FA" || v.TipoComprobante == "FB" || v.TipoComprobante == "FC")
                && v.ArcaEstado != "autorizado"
                && v.Estado != "anulado")
            .OrderByDescending(v => v.Fecha).ThenByDescending(v => v.Id)
            .Take(500)
            .ToListAsync();
        return Ok(ventas.Select(v => Map(v)).ToList());
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
        // Necesitamos ArcaImpTotal ademas de Total: en facturas A/B/C con IVA, ese es el monto real cobrable.
        var ventas = await q.Where(v => v.Estado != "anulado").Select(v => new { v.Id, v.Total, v.ArcaImpTotal }).ToListAsync();
        var ventaIds = ventas.Select(v => v.Id).ToList();
        var pagados = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId != null && ventaIds.Contains(c.VentaId!.Value)
                && c.Cobranza!.Estado == "VIGENTE")
            .GroupBy(c => c.VentaId!.Value)
            .Select(g => new { VentaId = g.Key, Pagado = g.Sum(x => x.Importe) })
            .ToListAsync();
        var dict = pagados.ToDictionary(p => p.VentaId, p => p.Pagado);
        var result = ventas.Select(v =>
        {
            var totalCobrar = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
            var pagado = dict.TryGetValue(v.Id, out var p) ? p : 0m;
            return new VentaSaldoDto(v.Id, totalCobrar, pagado, totalCobrar - pagado);
        }).ToList();
        return Ok(result);
    }

    /// <summary>
    /// 2026-06-24: Devuelve TODAS las ventas impagas (saldo > 0) de un cliente, sin paginar.
    /// Necesario porque desde el 22/06 el listado principal pagina (50 por pagina) y el formulario
    /// de "Nueva venta" no podia ver las deudas viejas que quedaban fuera de esa pagina.
    /// Si se pasa excludeVentaId, se excluye esa venta del resultado (caso edicion).
    /// </summary>
    [HttpGet("cliente/{clienteId:int}/impagas")]
    public async Task<IActionResult> GetImpagasCliente(int clienteId, [FromQuery] int? excludeVentaId = null)
    {
        var ventas = await _db.CafeVentas.Include(v => v.Items)
            .Where(v => v.ClienteId == clienteId
                     && v.Estado == "emitido"
                     && (excludeVentaId == null || v.Id != excludeVentaId.Value))
            .ToListAsync();
        if (ventas.Count == 0) return Ok(new List<CafeVentaDto>());

        var ventaIds = ventas.Select(v => v.Id).ToList();
        var pagados = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId != null && ventaIds.Contains(c.VentaId!.Value)
                && c.Cobranza!.Estado == "VIGENTE")
            .GroupBy(c => c.VentaId!.Value)
            .Select(g => new { VentaId = g.Key, Pagado = g.Sum(x => x.Importe) })
            .ToListAsync();
        var pagadosDict = pagados.ToDictionary(p => p.VentaId, p => p.Pagado);

        var impagas = ventas.Where(v =>
        {
            var totalCobrar = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
            var pagado = pagadosDict.TryGetValue(v.Id, out var p) ? p : 0m;
            return totalCobrar - pagado > 0.01m;
        }).OrderBy(v => v.Fecha).ThenBy(v => v.Id).ToList();

        return Ok(impagas.Select(v => Map(v)).ToList());
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
        var esFacturaArca = v.TipoComprobante is "FA" or "FB" or "FC" or "NCA" or "NCB" or "NCC";
        var autorizada = v.ArcaEstado == "autorizado"
                         && !string.IsNullOrEmpty(v.ArcaCae)
                         && v.ArcaCbteNro.HasValue
                         && v.ArcaPtoVta.HasValue
                         && v.ArcaCbteTipoNum.HasValue;

        if (esFacturaArca && autorizada)
        {
            var pdfBytes = BuildArcaPdf(v, cfg!);
            return File(pdfBytes, "application/pdf", BuildPdfFilename(v));
        }

        var qr = await _qrRepartidorService.GenerarQrAsync(v.PublicToken);
        // 2026-06-08: combosMap para agrupar items de combos en el PDF (visualización para el cliente)
        var comboIds = v.Items.Where(x => x.ComboOrigenId.HasValue).Select(x => x.ComboOrigenId!.Value).Distinct().ToList();
        var combosMap = comboIds.Count > 0
            ? await _db.Set<CafeCombo>().Where(c => comboIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => (c.Nombre, c.Sku))
            : null;
        await HydrateCfgFromEmisorAsync(cfg);
        var bytes = _pdfService.GenerarPdfBytes(v, cfg, qr, combosMap);
        return File(bytes, "application/pdf", BuildPdfFilename(v));
    }

    /// <summary>
    /// Genera el PDF del comprobante (cotización o factura ARCA según el tipo) y lo sube a
    /// Google Drive. Devuelve el ID del archivo y un link para abrirlo. Registra DriveFileId
    /// y DriveSubidoAt en la venta para mostrar "Ver en Drive" en lugar del botón de subir.
    /// </summary>
    [HttpPost("{id:int}/drive-upload")]
    public async Task<IActionResult> SubirADrive(int id, [FromServices] GoogleDriveService driveSvc)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).ThenInclude(i => i.ProductoNav).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        var cfg = await _db.CafeSettings.FindAsync(1);

        // 2026-05-28: subir a Drive equivale a "mandar al tablero IMPRIMIR PEDIDOS DE OSMAR".
        // 2026-06-12: el tablero YA NO depende de que Drive funcione. Si Google corta el permiso
        // (token vencido/revocado), la venta entra igual a Preparacion de pedidos y solo se
        // pierde (temporalmente) el PDF en Drive. Antes la venta quedaba invisible para el armador.
        // 2026-06-03 fix: si la venta ya estaba armada (LISTO/EN_CAMINO/ENTREGADO) y el usuario
        // la edita + re-sube, la revivimos al tablero con flag "MODIFICADO". El armador va a ver
        // un chip naranja en la card avisando que el pedido cambio.
        var yaArmada = v.EstadoPreparacion == "LISTO" || v.EstadoPreparacion == "EN_CAMINO" || v.EstadoPreparacion == "ENTREGADO";
        if (yaArmada)
        {
            var estadoAnt = v.EstadoPreparacion;
            v.EstadoPreparacion = "PARA_PREPARAR";
            v.PreparacionUpdatedAt = DateTime.UtcNow;
            v.ModificadoDespuesDeArmar = true;
            // 2026-06-09: log obligatorio cuando un drive-upload revive una venta ya armada.
            // Antes pasaba sin log y el armador no entendia por que reaparecia en el tablero.
            _db.CafeVentaPreparacionLogs.Add(new CafeVentaPreparacionLog
            {
                VentaId = v.Id,
                EstadoAnterior = estadoAnt,
                EstadoNuevo = "PARA_PREPARAR",
                OperadorNombre = NormOperatorName(Request.Headers["X-Operator-Name"].FirstOrDefault()),
                Notas = "Revivida por re-subida a Drive (probablemente edicion de la venta)",
                CreatedAt = DateTime.UtcNow
            });
        }
        else if (string.IsNullOrEmpty(v.EstadoPreparacion))
        {
            v.EstadoPreparacion = "PARA_PREPARAR";
            v.PreparacionUpdatedAt = DateTime.UtcNow;
            // 2026-06-09: log primera vez que entra al tablero
            _db.CafeVentaPreparacionLogs.Add(new CafeVentaPreparacionLog
            {
                VentaId = v.Id,
                EstadoAnterior = null,
                EstadoNuevo = "PARA_PREPARAR",
                OperadorNombre = NormOperatorName(Request.Headers["X-Operator-Name"].FirstOrDefault()),
                Notas = "Entro al tablero por primera subida a Drive",
                CreatedAt = DateTime.UtcNow
            });
        }
        // Si la venta estaba "oculta" del tablero, al re-subir a Drive la volvemos a mostrar.
        v.PreparacionOcultoAt = null;

        string? fileId = null, link = null, driveError = null;
        try
        {
            var pdfBytes = await GenerarPdfBytesAsync(v, cfg);
            var fileName = BuildPdfFilename(v);
            (fileId, link) = await driveSvc.UploadFileAsync(fileName, pdfBytes, "application/pdf");

            v.DriveFileId = fileId;
            v.DriveSubidoAt = DateTime.UtcNow;
            v.DriveSubidasCount = v.DriveSubidasCount + 1;
        }
        catch (Exception ex)
        {
            driveError = ex.Message;
        }

        await _db.SaveChangesAsync();

        if (driveError is not null)
            return BadRequest(new { error = "La venta SI entro a Preparacion de pedidos, pero el PDF no se pudo subir a Drive: " + driveError });

        return Ok(new { ok = true, fileId, link, subidoAt = v.DriveSubidoAt, subidasCount = v.DriveSubidasCount });
    }

    /// <summary>Genera los bytes del PDF de una venta (ARCA si esta autorizada, cotizacion sino).
    /// Centralizado aca para poder reusarlo desde drive-upload Y desde imprimir-pdf-combinado.</summary>
    [NonAction]
    public async Task<byte[]> GenerarPdfBytesAsync(Models.CafeVenta v, Models.CafeSetting? cfg)
    {
        var esFacturaArca = v.TipoComprobante is "FA" or "FB" or "FC" or "NCA" or "NCB" or "NCC";
        var autorizada = v.ArcaEstado == "autorizado"
                         && !string.IsNullOrEmpty(v.ArcaCae)
                         && v.ArcaCbteNro.HasValue
                         && v.ArcaPtoVta.HasValue
                         && v.ArcaCbteTipoNum.HasValue;
        if (esFacturaArca && autorizada)
        {
            return BuildArcaPdf(v, cfg!);
        }
        var qr = await _qrRepartidorService.GenerarQrAsync(v.PublicToken);
        // 2026-06-08: combosMap para agrupar items que vienen del mismo combo en una sola línea
        var comboIds = v.Items.Where(x => x.ComboOrigenId.HasValue).Select(x => x.ComboOrigenId!.Value).Distinct().ToList();
        var combosMap = comboIds.Count > 0
            ? await _db.Set<CafeCombo>().Where(c => comboIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => (c.Nombre, c.Sku))
            : null;
        await HydrateCfgFromEmisorAsync(cfg);
        return _pdfService.GenerarPdfBytes(v, cfg, qr, combosMap);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TOKEN PUBLICO + ENDPOINTS PUBLICOS PARA COMPARTIR EL COMPROBANTE
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Genera un token aleatorio ~22 chars base64-url-safe para el link publico.
    /// Usa Guid.NewGuid() codificado en base64 sin padding ni chars conflictivos en URLs.</summary>
    private static string GeneratePublicToken()
    {
        var g = Guid.NewGuid();
        var bytes = g.ToByteArray();
        return Convert.ToBase64String(bytes)
            .Replace("/", "_").Replace("+", "-").TrimEnd('=');
    }

    /// <summary>Si la venta no tiene PublicToken (caso de ventas viejas pre-feature),
    /// genera uno y lo persiste. Devuelve el token actual (nuevo o existente).</summary>
    private async Task<string> EnsurePublicTokenAsync(CafeVenta v)
    {
        if (!string.IsNullOrEmpty(v.PublicToken)) return v.PublicToken;
        v.PublicToken = GeneratePublicToken();
        await _db.SaveChangesAsync();
        return v.PublicToken;
    }

    /// <summary>Endpoint PUBLICO (sin auth) para que el cliente abra el link compartido
    /// por WhatsApp/email y vea su comprobante online. Devuelve el mismo CafeVentaDto
    /// que el endpoint privado por id, pero limitado al token (no enumerable).</summary>
    [HttpGet("publica/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetByPublicToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var v = await _db.CafeVentas.Include(x => x.Items).ThenInclude(i => i.ProductoNav)
            .FirstOrDefaultAsync(x => x.PublicToken == token);
        if (v is null) return NotFound(new { error = "Comprobante no encontrado" });
        return Ok(Map(v));
    }

    /// <summary>Endpoint PUBLICO (sin auth) para descargar el PDF del comprobante via token.
    /// Si la venta es Factura A/B/C autorizada → PDF ARCA; si no → PDF de cotizacion interno.</summary>
    [HttpGet("publica/{token}/pdf")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPdfByPublicToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return NotFound();
        var v = await _db.CafeVentas.Include(x => x.Items).ThenInclude(i => i.ProductoNav)
            .FirstOrDefaultAsync(x => x.PublicToken == token);
        if (v is null) return NotFound(new { error = "Comprobante no encontrado" });
        var cfg = await _db.CafeSettings.FindAsync(1);
        var esFacturaArca = v.TipoComprobante is "FA" or "FB" or "FC" or "NCA" or "NCB" or "NCC";
        var autorizada = v.ArcaEstado == "autorizado" && !string.IsNullOrEmpty(v.ArcaCae)
                         && v.ArcaCbteNro.HasValue && v.ArcaPtoVta.HasValue && v.ArcaCbteTipoNum.HasValue;
        var qr = await _qrRepartidorService.GenerarQrAsync(v.PublicToken);
        // 2026-06-08: combosMap para agrupar items que vienen del mismo combo en el PDF
        Dictionary<int, (string Nombre, string? Sku)>? combosMapPub = null;
        if (!(esFacturaArca && autorizada))
        {
            var comboIdsPub = v.Items.Where(x => x.ComboOrigenId.HasValue).Select(x => x.ComboOrigenId!.Value).Distinct().ToList();
            combosMapPub = comboIdsPub.Count > 0
                ? await _db.Set<CafeCombo>().Where(c => comboIdsPub.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => (c.Nombre, c.Sku))
                : null;
        }
        if (!(esFacturaArca && autorizada)) await HydrateCfgFromEmisorAsync(cfg);
        byte[] pdfBytes = (esFacturaArca && autorizada) ? BuildArcaPdf(v, cfg!) : _pdfService.GenerarPdfBytes(v, cfg, qr, combosMapPub);
        return File(pdfBytes, "application/pdf", BuildPdfFilename(v));
    }

    /// <summary>Asegura un token publico y devuelve la URL publica + token. Usado por el
    /// frontend antes de mandar mail/WhatsApp para construir el link a compartir.</summary>
    [HttpPost("{id:int}/ensure-public-token")]
    public async Task<IActionResult> EnsurePublicToken(int id)
    {
        var v = await _db.CafeVentas.FindAsync(id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        var token = await EnsurePublicTokenAsync(v);
        return Ok(new { token });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  ENVIO DE COMPROBANTE: Email + WhatsApp Interno (con PDF adjunto)
    // ═══════════════════════════════════════════════════════════════════════

    public record SendEmailRequest(string To, string? Subject, string? Body, string? PublicUrl);
    public record SendWhatsappInternoRequest(string Phone, string? Caption);

    /// <summary>Genera el PDF del comprobante y lo manda por email al destinatario indicado.
    /// Usa la configuracion SMTP guardada en Integrations (provider: "email-smtp").</summary>
    [HttpPost("{id:int}/send-email")]
    public async Task<IActionResult> SendEmail(int id, [FromBody] SendEmailRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.To))
            return BadRequest(new { error = "Destinatario vacio" });

        var v = await _db.CafeVentas.Include(x => x.Items).ThenInclude(i => i.ProductoNav).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        // 1) Leer configuracion SMTP
        var integration = await _integrationService.GetByProviderAsync("email-smtp");
        if (integration is null)
            return BadRequest(new { error = "No hay configuracion de email. Configurala en Integraciones." });
        var secret = await _integrationService.GetSecretAsync("email-smtp");
        if (string.IsNullOrEmpty(secret))
            return BadRequest(new { error = "No hay contraseña SMTP configurada" });

        string smtpHost = "smtp.gmail.com";
        int smtpPort = 587;
        bool smtpTls = true;
        string fromAddress = "";
        string fromName = "";
        string username = "";

        if (!string.IsNullOrEmpty(integration.Settings))
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(integration.Settings);
                var root = doc.RootElement;
                if (root.TryGetProperty("smtpHost", out var h)) smtpHost = h.GetString() ?? smtpHost;
                if (root.TryGetProperty("smtpPort", out var p)) smtpPort = p.GetInt32();
                if (root.TryGetProperty("smtpTls", out var t)) smtpTls = t.GetBoolean();
                if (root.TryGetProperty("fromAddress", out var f)) fromAddress = f.GetString() ?? "";
                if (root.TryGetProperty("fromName", out var n)) fromName = n.GetString() ?? "";
                if (root.TryGetProperty("username", out var u)) username = u.GetString() ?? "";
            }
            catch { }
        }
        if (string.IsNullOrEmpty(fromAddress))
            return BadRequest(new { error = "No hay email de remitente configurado en Integraciones" });

        // 2) Generar el PDF
        var cfg = await _db.CafeSettings.FindAsync(1);
        byte[] pdfBytes;
        var esFacturaArca = v.TipoComprobante is "FA" or "FB" or "FC" or "NCA" or "NCB" or "NCC";
        var autorizada = v.ArcaEstado == "autorizado" && !string.IsNullOrEmpty(v.ArcaCae)
                         && v.ArcaCbteNro.HasValue && v.ArcaPtoVta.HasValue && v.ArcaCbteTipoNum.HasValue;
        if (esFacturaArca && autorizada)
            pdfBytes = BuildArcaPdf(v, cfg!);
        else
        {
            var qr = await _qrRepartidorService.GenerarQrAsync(v.PublicToken);
            // 2026-06-08: combosMap para PDF
            var comboIdsM = v.Items.Where(x => x.ComboOrigenId.HasValue).Select(x => x.ComboOrigenId!.Value).Distinct().ToList();
            var combosMapM = comboIdsM.Count > 0
                ? await _db.Set<CafeCombo>().Where(c => comboIdsM.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => (c.Nombre, c.Sku))
                : null;
            await HydrateCfgFromEmisorAsync(cfg);
            pdfBytes = _pdfService.GenerarPdfBytes(v, cfg, qr, combosMapM);
        }

        // 3) Armar y enviar el email
        var subject = string.IsNullOrWhiteSpace(req.Subject)
            ? $"Comprobante {v.Numero} - {cfg?.NegocioNombre ?? "Frikaf"}"
            : req.Subject!;
        // Si vino una PublicUrl del frontend, la sumamos al final del body por default
        // (asi el cliente tambien puede ver el comprobante online si prefiere no abrir el PDF).
        var linkLine = !string.IsNullOrWhiteSpace(req.PublicUrl)
            ? $"\n\nTambién lo podés ver online acá: {req.PublicUrl}"
            : "";
        // Monto a mostrar al cliente = total real con IVA si es factura ARCA, sino Total.
        var montoCliente = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
        var body = string.IsNullOrWhiteSpace(req.Body)
            ? $"Hola{(string.IsNullOrWhiteSpace(v.ClienteNombreSnapshot) ? "" : " " + v.ClienteNombreSnapshot)},\n\n" +
              $"Te adjuntamos el comprobante {v.Numero} por ${montoCliente:N2}." + linkLine + "\n\n" +
              $"Cualquier consulta, escribinos.\n\n" +
              $"Saludos,\n{cfg?.NegocioNombre ?? "Frikaf"}"
            : req.Body!;

        try
        {
            using var client = new System.Net.Mail.SmtpClient(smtpHost, smtpPort)
            {
                Credentials = new System.Net.NetworkCredential(
                    string.IsNullOrEmpty(username) ? fromAddress : username, secret),
                EnableSsl = smtpTls,
                Timeout = 30000
            };
            using var message = new System.Net.Mail.MailMessage
            {
                From = new System.Net.Mail.MailAddress(fromAddress, string.IsNullOrEmpty(fromName) ? fromAddress : fromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = false
            };
            message.To.Add(req.To);
            using var ms = new MemoryStream(pdfBytes);
            var attachment = new System.Net.Mail.Attachment(ms, BuildPdfFilename(v), "application/pdf");
            message.Attachments.Add(attachment);
            await client.SendMailAsync(message);
            return Ok(new { sent = true, message = "Email enviado a " + req.To });
        }
        catch (Exception ex)
        {
            return BadRequest(new { sent = false, error = "Error enviando email: " + ex.Message });
        }
    }

    /// <summary>Genera el PDF del comprobante y lo manda por WhatsApp via el container
    /// (Playwright /whatsapp/send-with-pdf). El contenedor debe estar vinculado.</summary>
    [HttpPost("{id:int}/send-whatsapp-interno")]
    public async Task<IActionResult> SendWhatsappInterno(int id, [FromBody] SendWhatsappInternoRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Phone))
            return BadRequest(new { error = "Telefono vacio" });

        var v = await _db.CafeVentas.Include(x => x.Items).ThenInclude(i => i.ProductoNav).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        // Generar el PDF (mismo criterio que en GetPdf)
        var cfg = await _db.CafeSettings.FindAsync(1);
        byte[] pdfBytes;
        var esFacturaArca = v.TipoComprobante is "FA" or "FB" or "FC" or "NCA" or "NCB" or "NCC";
        var autorizada = v.ArcaEstado == "autorizado" && !string.IsNullOrEmpty(v.ArcaCae)
                         && v.ArcaCbteNro.HasValue && v.ArcaPtoVta.HasValue && v.ArcaCbteTipoNum.HasValue;
        if (esFacturaArca && autorizada)
            pdfBytes = BuildArcaPdf(v, cfg!);
        else
        {
            var qr = await _qrRepartidorService.GenerarQrAsync(v.PublicToken);
            // 2026-06-08: combosMap para PDF
            var comboIdsW = v.Items.Where(x => x.ComboOrigenId.HasValue).Select(x => x.ComboOrigenId!.Value).Distinct().ToList();
            var combosMapW = comboIdsW.Count > 0
                ? await _db.Set<CafeCombo>().Where(c => comboIdsW.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => (c.Nombre, c.Sku))
                : null;
            await HydrateCfgFromEmisorAsync(cfg);
            pdfBytes = _pdfService.GenerarPdfBytes(v, cfg, qr, combosMapW);
        }

        // Caption por default si no viene. Monto = total real con IVA si es factura ARCA.
        var montoCaption = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
        var caption = string.IsNullOrWhiteSpace(req.Caption)
            ? $"Hola{(string.IsNullOrWhiteSpace(v.ClienteNombreSnapshot) ? "" : " " + v.ClienteNombreSnapshot)}, te paso el comprobante {v.Numero} por ${montoCaption:N2}. Saludos!"
            : req.Caption!;

        var result = await _whatsAppService.SendMessageWithPdfAsync(req.Phone, caption, pdfBytes, BuildPdfFilename(v));
        if (result.Success)
            return Ok(new { sent = true, message = result.Message });
        return BadRequest(new { sent = false, error = result.Message });
    }

    /// <summary>
    /// Arma el PdfEmisor + PdfComprobante + PdfReceptor a partir de los datos de la venta
    /// del Café y los datos del negocio, y genera el PDF de factura ARCA (con CAE y QR).
    /// </summary>
    [NonAction]
    public byte[] BuildArcaPdf(CafeVenta v, CafeSetting cfg)
    {
        // El emisor del PDF debe ser el CUIT con el que se FACTURÓ (no el CUIT del negocio por default),
        // así una factura emitida con la sociedad de hecho sale con SUS datos y su QR de AFIP.
        var cuitEmisorPdf = cfg?.NegocioCuit ?? "";
        if (v.ArcaWebserviceAccountId.HasValue && v.ArcaWebserviceAccountId.Value > 0)
        {
            var cuenta = _db.ArcaWebserviceAccounts
                .FirstOrDefault(a => a.Id == v.ArcaWebserviceAccountId.Value);
            if (cuenta is not null) cuitEmisorPdf = cuenta.Cuit;
        }

        var ficha = _emisorService.GetEntityByCuitAsync(cuitEmisorPdf).GetAwaiter().GetResult();
        var emisor = new PdfEmisor
        {
            Cuit = ficha?.Cuit ?? new string((cuitEmisorPdf ?? "").Where(char.IsDigit).ToArray()),
            RazonSocial = ficha?.RazonSocial ?? cfg?.NegocioRazonSocial ?? cfg?.NegocioNombre ?? "—",
            CondicionIva = ficha?.CondicionIva ?? "Responsable Inscripto",
            Domicilio = ficha?.Domicilio ?? cfg?.NegocioDireccion,
            IIBBTipo = ficha?.IIBBTipo,
            IIBBNumero = ficha?.IIBBNumero ?? cfg?.NegocioIngresosBrutos,
            InicioActividades = ficha?.InicioActividades ?? cfg?.NegocioInicioActividad,
            LogoBytes = _emisorService.TryGetLogoBytes(ficha?.LogoPath),
            // 2026-06-16: contacto + datos bancarios. Fallback al Cafe_Settings si la ficha no los tiene.
            Telefono = ficha?.Telefono ?? cfg?.NegocioTelefono,
            Telefono2 = ficha?.Telefono2 ?? cfg?.NegocioTelefono2,
            Email = ficha?.Email ?? cfg?.NegocioEmail,
            Web = ficha?.Web ?? cfg?.NegocioWeb,
            Web2 = ficha?.Web2 ?? cfg?.NegocioWeb2,
            BancoNombre = ficha?.BancoNombre,
            BancoCbu = ficha?.BancoCbu,
            BancoAlias = ficha?.BancoAlias,
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
            NumeroInterno = v.Numero,    // 2026-06-16: ref interna debajo del numero ARCA
            Fecha = v.Fecha.ToString("yyyyMMdd"),
            Concepto = v.Concepto,
            ImpNeto = neto,
            ImpTotal = totalConIva,
            Cae = v.ArcaCae,
            CaeVto = v.ArcaCaeVto?.ToString("yyyyMMdd") ?? "",
            // Extras UX (con prolijidad fiscal — no son obligatorios pero ayudan al lector):
            IsPaid = v.IsPaid,
            TipoClienteTag = v.ClienteTipoSnapshot,
            DiasVisita = v.WeekDays,
            EnRadar = v.EnRadar,
            Retira = v.Retira,
            ComentariosCliente = v.ClienteComentariosComprobante,
            Observaciones = v.Observaciones,
            CondicionPago = v.CondicionPago,
            DomicilioEntrega = v.ClienteDomicilioEntregaSnapshot,
            EntregaPor = v.EntregaPor,
            // 2026-06-12: el QR de entrega del repartidor también va en las facturas con CAE
            QrRepartidorBytes = _qrRepartidorService.GenerarQrAsync(v.PublicToken).GetAwaiter().GetResult(),
        };

        // 2026-06-08: Pre-cargar mapping de combos {Id → (Nombre, Sku)} usado para agrupar
        // items que vinieron de un combo en UNA sola línea en la factura ARCA.
        var comboIdsEnVenta = v.Items.Where(x => x.ComboOrigenId.HasValue).Select(x => x.ComboOrigenId!.Value).Distinct().ToList();
        var combosMapArca = comboIdsEnVenta.Count > 0
            ? _db.Set<CafeCombo>().Where(c => comboIdsEnVenta.Contains(c.Id)).ToDictionary(c => c.Id, c => (c.Nombre, c.Sku))
            : new Dictionary<int, (string Nombre, string? Sku)>();

        // Pre-agrupar items por ComboOrigenId. Cada grupo se renderiza como UNA fila con
        // nombre del combo y suma de subtotales (cantidad=1, precio = subtotal).
        var emittedCombos = new HashSet<int>();
        foreach (var it in v.Items)
        {
            // Item de combo: emitir UNA sola fila por grupo de combo, ignorar siguientes del mismo grupo.
            if (it.ComboOrigenId.HasValue)
            {
                var cid = it.ComboOrigenId.Value;
                if (emittedCombos.Contains(cid)) continue;
                emittedCombos.Add(cid);
                var grupo = v.Items.Where(x => x.ComboOrigenId == cid).ToList();
                var subtotalGrupo = grupo.Sum(x => x.Subtotal);
                string nombreCombo;
                string? skuCombo;
                if (combosMapArca.TryGetValue(cid, out var cInfo)) { nombreCombo = cInfo.Nombre; skuCombo = cInfo.Sku; }
                else { nombreCombo = grupo[0].ProductoNombreSnapshot; skuCombo = null; }
                comp.Items.Add(new PdfItem
                {
                    Descripcion = nombreCombo,
                    Sku = skuCombo,
                    Producto = nombreCombo,
                    Formato = "Combo",
                    Cantidad = 1,
                    PrecioUnitario = subtotalGrupo,
                    AlicPct = letra == "C" ? 0 : 21m,
                    PrecioOriginal = null,
                    DescuentoPct = null
                });
                continue;
            }

            // Item suelto (no combo) — lógica original
            var puConDesc = it.DescuentoPct > 0 && it.Cantidad > 0
                ? Math.Round(it.Subtotal / it.Cantidad, 2, MidpointRounding.AwayFromZero)
                : it.PrecioUnitario;
            // Producto: solo el nombre (snapshot) + sufijos d.p./env. plat. si aplica
            var prodName = it.ProductoNombreSnapshot;
            if (it.EsDoyPack) prodName += " (d.p.)";
            else if (it.EsEnvasePlateado) prodName += " (env. plat.)";

            // Formato: molienda + formato físico (ej "EN GRANOS · 1KG")
            var fmtParts = new List<string>();
            if (!string.IsNullOrEmpty(it.Molienda)) fmtParts.Add(it.Molienda!);
            if (!it.EsConceptoLibre) fmtParts.Add(it.Formato);
            var fmtStr = string.Join(" · ", fmtParts);

            // Descripcion (legacy): texto unico que se usa como fallback en PDF si no hay separación
            var desc = prodName + (string.IsNullOrEmpty(fmtStr) ? "" : $" — {fmtStr}");

            comp.Items.Add(new PdfItem
            {
                Descripcion = desc,
                Sku = it.ProductoNav?.Sku,           // SKU separado (columna propia)
                Producto = prodName,                  // Nombre limpio
                Formato = fmtStr,                     // Formato limpio
                Cantidad = it.Cantidad,
                PrecioUnitario = puConDesc,
                AlicPct = letra == "C" ? 0 : 21m, // Hardcoded 21% — coherente con la emisión
                // Si hay descuento de linea, guardamos el precio ORIGINAL para que el PDF
                // pueda mostrarlo tachado + "X% desc." debajo (caso tipico: bonificacion 100%).
                PrecioOriginal = it.DescuentoPct > 0 ? it.PrecioUnitario : (decimal?)null,
                DescuentoPct = it.DescuentoPct > 0 ? it.DescuentoPct : (decimal?)null,
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
                "MERCADOPAGO" => "Mercado Pago",
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
    /// ordenados por cantidad de comprobantes en los que aparecio. Solo cuenta ventas no anuladas.
    /// 2026-06-08: excluye productos descartados (Cafe_ClienteProductoDescartado), salvo que el
    /// cliente haya comprado el producto DESPUÉS del descarte (en ese caso vuelve a aparecer).</summary>
    [HttpGet("top-productos-cliente/{clienteId:int}")]
    public async Task<IActionResult> GetTopProductosByCliente(int clienteId, [FromQuery] int count = 10)
    {
        if (clienteId <= 0) return Ok(new List<CafeTopProductoClienteDto>());
        if (count <= 0) count = 10;

        var grouped = await _db.CafeVentaItems
            .Where(i => i.VentaNav != null
                        && i.VentaNav.ClienteId == clienteId
                        && i.VentaNav.Estado != "anulado"
                        && i.ProductoId != null)
            .GroupBy(i => new { ProductoId = i.ProductoId!.Value, i.Formato })
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
            // 2026-06-08: pedimos más para tener margen después del filtro de descartados
            .Take(Math.Max(count * 2, 30))
            .ToListAsync();

        if (grouped.Count == 0) return Ok(new List<CafeTopProductoClienteDto>());

        // 2026-06-08: traer los descartes vigentes del cliente para excluirlos.
        // Si la última compra es POSTERIOR al descarte, ignoramos el descarte (el operador
        // volvió a vendérselo, así que la sugerencia vuelve sola).
        var descartes = await _db.CafeClienteProductosDescartados
            .Where(d => d.ClienteId == clienteId)
            .Select(d => new { d.ProductoId, d.DescartadoAt })
            .ToListAsync();
        var descartesDict = descartes.ToDictionary(d => d.ProductoId, d => d.DescartadoAt);

        var filtered = grouped
            .Where(g => !descartesDict.ContainsKey(g.ProductoId)
                        || g.LastPurchase > descartesDict[g.ProductoId])
            .Take(count)
            .ToList();

        if (filtered.Count == 0) return Ok(new List<CafeTopProductoClienteDto>());

        var ids = filtered.Select(x => x.ProductoId).Distinct().ToList();
        var productos = await _db.CafeProductos
            .Include(p => p.OemNav)
            .Where(p => ids.Contains(p.Id) && p.IsActive)
            .ToListAsync();

        var cliente = await _db.CafeClientes.FindAsync(clienteId);
        var tipo = CafePricingService.ResolverTipo(cliente?.Tipo);
        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };

        var result = new List<CafeTopProductoClienteDto>();
        foreach (var g in filtered)
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

    // ═══════════════════════════════════════════════════════════════════════
    //  2026-06-08: Descartar / restaurar productos de "Más comprados" por cliente
    // ═══════════════════════════════════════════════════════════════════════

    public record DescartarTopProductoRequest(int ProductoId);

    /// <summary>Descarta un producto de la lista "Más comprados" para un cliente puntual.
    /// El producto deja de aparecer hasta que el cliente lo vuelva a comprar.</summary>
    [HttpPost("top-productos-cliente/{clienteId:int}/descartar")]
    public async Task<IActionResult> DescartarTopProducto(int clienteId, [FromBody] DescartarTopProductoRequest req)
    {
        if (clienteId <= 0 || req?.ProductoId is null or <= 0)
            return BadRequest(new { error = "ClienteId y ProductoId son requeridos." });
        // Idempotente: si ya existe, no falla. Si no existe, lo crea.
        var existente = await _db.CafeClienteProductosDescartados
            .FirstOrDefaultAsync(d => d.ClienteId == clienteId && d.ProductoId == req.ProductoId);
        var operador = HttpContext.User?.Identity?.Name;
        if (existente is null)
        {
            _db.CafeClienteProductosDescartados.Add(new CafeClienteProductoDescartado
            {
                ClienteId = clienteId,
                ProductoId = req.ProductoId,
                DescartadoAt = DateTime.UtcNow,
                DescartadoPor = operador
            });
            await _db.SaveChangesAsync();
        }
        else
        {
            // refrescar fecha — si ya estaba descartado, esto extiende el descarte
            existente.DescartadoAt = DateTime.UtcNow;
            existente.DescartadoPor = operador;
            await _db.SaveChangesAsync();
        }
        return Ok(new { ok = true });
    }

    /// <summary>Restaura un producto descartado (vuelve a aparecer en sugerencias).
    /// Si ProductoId es null/0 → restaura TODOS los descartados del cliente.</summary>
    [HttpPost("top-productos-cliente/{clienteId:int}/restaurar")]
    public async Task<IActionResult> RestaurarTopProducto(int clienteId, [FromBody] DescartarTopProductoRequest? req)
    {
        if (clienteId <= 0) return BadRequest(new { error = "ClienteId requerido." });
        var q = _db.CafeClienteProductosDescartados.Where(d => d.ClienteId == clienteId);
        if (req?.ProductoId is int pid and > 0) q = q.Where(d => d.ProductoId == pid);
        var rows = await q.ToListAsync();
        if (rows.Count > 0)
        {
            _db.CafeClienteProductosDescartados.RemoveRange(rows);
            await _db.SaveChangesAsync();
        }
        return Ok(new { ok = true, restaurados = rows.Count });
    }

    /// <summary>Devuelve los IDs de productos descartados de un cliente (para mostrar contador
    /// "🔄 Restaurar N descartados" en el frontend).</summary>
    [HttpGet("top-productos-cliente/{clienteId:int}/descartados")]
    public async Task<IActionResult> GetTopProductosDescartados(int clienteId)
    {
        if (clienteId <= 0) return Ok(new List<int>());
        var ids = await _db.CafeClienteProductosDescartados
            .Where(d => d.ClienteId == clienteId)
            .Select(d => d.ProductoId)
            .ToListAsync();
        return Ok(ids);
    }

    /// <summary>Cotización en vivo: NO crea la venta, solo calcula precios + verifica stock.</summary>
    [HttpPost("cotizar")]
    public async Task<IActionResult> Cotizar([FromBody] CafeCotizarRequest req)
    {
        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        var tipo = await ResolverTipoAsync(req.ClienteId, req.ClienteTipo);
        // 2026-06-18: si se está editando una venta, le pasamos su Id al cotizador para que
        // sume al stock disponible las cantidades que esa misma venta ya tiene reservadas
        // (evita falso "stock insuficiente" al editar/convertir-a-factura una cotización).
        return Ok(await CotizarInternoAsync(req.Items, tipo, req.Descuento, settings, req.EditandoVentaId));
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
            EnRadar = req.EnRadar,
            Retira = req.Retira,
            PorTransporte = req.PorTransporte,
            TransporteEmpresa = string.IsNullOrWhiteSpace(req.TransporteEmpresa) ? null : req.TransporteEmpresa.Trim(),
            TransporteDestino = string.IsNullOrWhiteSpace(req.TransporteDestino) ? null : req.TransporteDestino.Trim(),
            IsPaid = req.IsPaid,
            TipoComprobante = NormTipoComprobante(req.TipoComprobante),
            CondicionIva = NormCondicionIva(req.CondicionIva),
            CondicionPago = NormCondicionPago(req.CondicionPago),
            CreatedAt = DateTime.UtcNow,
            ComentarioArmado = string.IsNullOrWhiteSpace(req.ComentarioArmado) ? null : req.ComentarioArmado.Trim(),
        };
        // Pre-cargo los productos referenciados (con su UxB) para que el PDF tenga el ProductoNav.
        // Sin esto, los items BULTO en el preview pierden la informacion del UxB y no muestran
        // las unidades reales en la columna Cant.
        var prodIds = cot.Items.Select(x => x.ProductoId).Where(id => id > 0).Distinct().ToList();
        var prodsCache = await _db.CafeProductos.Where(p => prodIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);

        for (int idxPrev = 0; idxPrev < cot.Items.Count; idxPrev++)
        {
            var ci = cot.Items[idxPrev];
            // 2026-06-08: trasladar ComboOrigenId desde el request para que el PDF preview agrupe
            // los items del mismo combo en una sola línea (igual que la emisión definitiva).
            var reqItemPrev = idxPrev < req.Items.Count ? req.Items[idxPrev] : null;
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
                DescuentoPct = ci.DescuentoPct,
                ComboOrigenId = reqItemPrev?.ComboOrigenId   // 2026-06-08
            });
        }

        var cfg = await _db.CafeSettings.FindAsync(1);
        // Solo usamos el PDF de cotización (incluso si el tipo es FA/FB/FC) — es un PREVIEW,
        // no tiene CAE ni QR todavía. Si querés emitir real, usás el botón Emitir.
        // 2026-06-08: combosMap para agrupar items de combos en el PDF (1 sola línea por combo)
        var comboIdsPrev = ventaPreview.Items.Where(x => x.ComboOrigenId.HasValue).Select(x => x.ComboOrigenId!.Value).Distinct().ToList();
        var combosMapPrev = comboIdsPrev.Count > 0
            ? await _db.Set<CafeCombo>().Where(c => comboIdsPrev.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => (c.Nombre, c.Sku))
            : null;
        await HydrateCfgFromEmisorAsync(cfg);
        var bytes = _pdfService.GenerarPdfBytes(ventaPreview, cfg, null, combosMapPrev);
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
        // 2026-06-22: flag heredado del cliente para pedir firma al entregar.
        bool clienteSolicitarFirmaEntrega = false;
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
            clienteSolicitarFirmaEntrega = cli.SolicitarFirmaEntrega;
            tipo = CafePricingService.ResolverTipo(cli.Tipo);
        }
        else
        {
            // Modo "Venta Rápida": cliente ad-hoc, todos los datos vienen de los overrides.
            clienteNombre = string.IsNullOrWhiteSpace(req.ClienteNombreOverride) ? "Consumidor final" : req.ClienteNombreOverride.Trim();
            clienteRazonSocial = Nz(req.ClienteRazonSocialOverride);
            clienteCuit = Nz(req.ClienteCuitOverride);
            clienteDireccion = Nz(req.ClienteDireccionOverride);
            clienteLocalidad = Nz(req.ClienteLocalidadOverride);
            clienteCiudad = Nz(req.ClienteCiudadOverride);
            clienteCp = Nz(req.ClienteCpOverride);
            clienteTelefono = Nz(req.ClienteTelefonoOverride);
            clienteDomicilioEntrega = Nz(req.ClienteDomicilioEntregaOverride);
        }

        static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
            // 2026-06-22: si el request explicita el flag, usa eso; sino hereda del cliente.
            SolicitarFirmaEntrega = req.SolicitarFirmaEntrega ?? clienteSolicitarFirmaEntrega,
            Estado = "emitido",
            WeekDays = NormWeekDays(req.WeekDays),
            EnRadar = req.EnRadar,
            Retira = req.Retira,
            PorTransporte = req.PorTransporte,
            TransporteEmpresa = string.IsNullOrWhiteSpace(req.TransporteEmpresa) ? null : req.TransporteEmpresa.Trim(),
            TransporteDestino = string.IsNullOrWhiteSpace(req.TransporteDestino) ? null : req.TransporteDestino.Trim(),
            IsPaid = req.IsPaid,
            TipoComprobante = NormTipoComprobante(req.TipoComprobante),
            CondicionIva = NormCondicionIva(req.CondicionIva),
            CondicionPago = NormCondicionPago(req.CondicionPago),
            CreatedAt = DateTime.UtcNow,
            // 2026-06-05: Quien la cargo (header X-Operator-Name del frontend). Sirve para
            // mostrar iniciales OS/GE/GA/etc en el listado + auditoria.
            CreadoPorOperador = NormOperatorName(HttpContext.Request.Headers["X-Operator-Name"].ToString()),
            // Token aleatorio para el link publico /comprobante/{token}. 22 chars
            // base64-url-safe (~131 bits de entropia) — imposible de adivinar.
            PublicToken = GeneratePublicToken(),
            EntregaPor = string.IsNullOrWhiteSpace(req.EntregaPor) ? null : req.EntregaPor.Trim(),
            ComentarioArmado = string.IsNullOrWhiteSpace(req.ComentarioArmado) ? null : req.ComentarioArmado.Trim(),
            // 2026-06-23: Concepto AFIP. Default 1 (Productos). Si 2 o 3, se mandan fechas a ARCA al emitir.
            Concepto = req.Concepto is 1 or 2 or 3 ? req.Concepto : 1,
            ConceptoServDesde = req.ConceptoServDesde,
            ConceptoServHasta = req.ConceptoServHasta,
            // 2026-07-02: link de Maps del domicilio de entrega (override propio de la venta)
            MapeoLink = string.IsNullOrWhiteSpace(req.MapeoLink) ? null : req.MapeoLink.Trim(),
            // 2026-07-03: certificado/CUIT elegido para facturar (multi-sociedad). Null = default.
            ArcaWebserviceAccountId = req.ArcaWebserviceAccountId.HasValue && req.ArcaWebserviceAccountId.Value > 0
                ? req.ArcaWebserviceAccountId.Value : null
        };

        // 2026-07-02: si pidió guardar el link también en la ficha del cliente (para futuras ventas)
        if (req.GuardarMapeoEnCliente && !string.IsNullOrWhiteSpace(req.MapeoLink) && req.ClienteId.HasValue && req.ClienteId.Value > 0)
        {
            var cliMapeo = await _db.CafeClientes.FindAsync(req.ClienteId.Value);
            if (cliMapeo is not null) { cliMapeo.MapeoLink = req.MapeoLink.Trim(); cliMapeo.MapeoLat = null; cliMapeo.MapeoLng = null; }
        }

        // 2026-06-01 — Cargar info de productos "shell" para decremento correcto
        // (productos linkeados a MeLi via componentes: decrementar componentes en lugar del shell vacío).
        var prodIdsCrear = cot.Items.Where(i => i.Categoria != "LIBRE" && i.ProductoId > 0)
            .Select(i => i.ProductoId).Distinct().ToList();
        var shellCompsCrear = await LoadShellComponentsAsync(prodIdsCrear);

        // 2026-06-08: Si es PRESUPUESTO (TipoComprobante=PRO), NO descontar stock — solo
        // se reserva la cotización. Cuando el cliente confirme, se convierte a venta real
        // y ahí sí se descuenta. Este flag controla el bloque de descuento más abajo.
        var esPresupuesto = string.Equals(venta.TipoComprobante, "PRO", StringComparison.OrdinalIgnoreCase);

        // Mapear items + descontar stock fisico (si NO es presupuesto)
        for (int idx = 0; idx < cot.Items.Count; idx++)
        {
            var it = cot.Items[idx];
            // Si el operador editó la descripción de la línea (override), pisa el snapshot del nombre.
            var reqItem = idx < req.Items.Count ? req.Items[idx] : null;
            var nombreOverride = reqItem is not null && !string.IsNullOrWhiteSpace(reqItem.DescripcionOverride)
                ? reqItem.DescripcionOverride.Trim()
                : null;

            // 2026-06-05: Servicio del catalogo (no descuenta stock).
            if (it.Categoria == "SERVICIO")
            {
                venta.Items.Add(new CafeVentaItem
                {
                    ProductoId = null,
                    ServicioId = reqItem?.ServicioId,
                    EsConceptoLibre = false,
                    ProductoNombreSnapshot = nombreOverride ?? it.ProductoNombre,
                    Categoria = "SERVICIO",
                    Formato = "UNIT",
                    Cantidad = it.Cantidad,
                    PrecioUnitario = it.PrecioUnitario,
                    CostoUnitario = 0m,
                    Subtotal = it.Subtotal,
                    GramosDescontados = 0m,
                    Molienda = null,
                    EsDoyPack = false,
                    DescuentoPct = it.DescuentoPct,
                    ComboOrigenId = reqItem?.ComboOrigenId   // 2026-06-08
                });
                continue;
            }

            // Concepto libre: item sin producto del catálogo (no descuenta stock).
            if (it.Categoria == "LIBRE")
            {
                venta.Items.Add(new CafeVentaItem
                {
                    ProductoId = null,
                    EsConceptoLibre = true,
                    ProductoNombreSnapshot = nombreOverride ?? it.ProductoNombre,
                    Categoria = "LIBRE",
                    Formato = "UNIT",
                    Cantidad = it.Cantidad,
                    PrecioUnitario = it.PrecioUnitario,
                    CostoUnitario = 0m,
                    Subtotal = it.Subtotal,
                    GramosDescontados = 0m,
                    Molienda = null,
                    EsDoyPack = false,
                    DescuentoPct = it.DescuentoPct,
                    ComboOrigenId = reqItem?.ComboOrigenId   // 2026-06-08
                });
                continue;
            }

            var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
            if (prod is null) return BadRequest(new { error = $"Producto {it.ProductoId} no encontrado" });

            venta.Items.Add(new CafeVentaItem
            {
                ProductoId = prod.Id,
                EsConceptoLibre = false,
                ProductoNombreSnapshot = nombreOverride ?? prod.Nombre,
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
                DescuentoPct = it.DescuentoPct,
                ComboOrigenId = reqItem?.ComboOrigenId   // 2026-06-08
            });

            // 2026-06-08: Si es PRESUPUESTO (PRO), saltear el descuento de stock — el stock
            // solo se descuenta cuando el presupuesto se confirma y se convierte a venta real.
            if (esPresupuesto)
            {
                // No descontamos stock. El item ya quedó cargado arriba con su cantidad/precio.
                continue;
            }

            // Descontar stock. Si el formato es BULTO, 1 unidad cargada = UxB unidades reales.
            if (prod.Categoria == "CAFE")
            {
                var antesG = (int)Math.Round(prod.StockGramos);
                prod.StockGramos = Math.Max(0m, prod.StockGramos - it.GramosNecesarios);
                await _stockLogger.LogAsync(prod.Id, "VENTA_NUESTRA", antesG, (int)Math.Round(prod.StockGramos),
                    comentario: $"Venta #{venta.Numero} · {it.Cantidad}x{it.Formato} · -{(int)Math.Round(it.GramosNecesarios)}g",
                    saveChanges: false);
                prod.UpdatedAt = DateTime.UtcNow;
                prod.StockChangedAt = DateTime.UtcNow;
                await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
            }
            else if (shellCompsCrear.TryGetValue(prod.Id, out var compsAct))
            {
                // 2026-06-01 — SHELL: el prod no tiene stock propio. Decrementar componentes.
                // El stock del shell queda en 0; el push a MeLi se dispara por cada componente
                // (CafeProductosController FireAndForgetPushMeli al cambiar su stock).
                var unidadesADescontar = UnidadesRealesStock(prod, it.Formato, it.Cantidad);
                foreach (var (compId, cantPorUnidadShell) in compsAct)
                {
                    var compProd = await _db.CafeProductos.FindAsync(compId);
                    if (compProd is null) continue;
                    var totalDescontar = (int)Math.Ceiling(cantPorUnidadShell * unidadesADescontar);
                    var antes = compProd.StockUnidades;
                    compProd.StockUnidades = Math.Max(0, compProd.StockUnidades - totalDescontar);
                    compProd.UpdatedAt = DateTime.UtcNow;
                    compProd.StockChangedAt = DateTime.UtcNow;
                    await _stockLogger.LogAsync(compId, "VENTA_NUESTRA_COMP", antes, compProd.StockUnidades,
                        comentario: $"Venta #{venta.Numero} · shell {prod.Sku} {it.Cantidad}x{it.Formato} → comp -{totalDescontar}u",
                        saveChanges: false);
                    await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, compProd);
                }
                // Marca el shell como tocado (no decrementa stock, pero queda timestamp).
                prod.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                // Producto físico normal — decrementa stock propio.
                var unidadesADescontar = UnidadesRealesStock(prod, it.Formato, it.Cantidad);
                var antes = prod.StockUnidades;
                prod.StockUnidades = Math.Max(0, prod.StockUnidades - unidadesADescontar);
                await _stockLogger.LogAsync(prod.Id, "VENTA_NUESTRA", antes, prod.StockUnidades,
                    comentario: $"Venta #{venta.Numero} · {it.Cantidad}x{it.Formato} · -{unidadesADescontar}u",
                    saveChanges: false);
                prod.UpdatedAt = DateTime.UtcNow;
                prod.StockChangedAt = DateTime.UtcNow;
                await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
            }
        }

        // Capturar los productos afectados ANTES del SaveChanges para pushear despues.
        var productosAfectados = venta.Items
            .Where(i => i.ProductoId.HasValue)
            .Select(i => i.ProductoId!.Value)
            // 2026-06-01: incluir componentes de productos shell (porque ellos fueron los que cambiaron
            // de stock, y pueden estar linkeados a otras publicaciones MeLi también).
            .Concat(shellCompsCrear.Values.SelectMany(l => l.Select(c => c.compProductoId)))
            .Distinct().ToList();

        _db.CafeVentas.Add(venta);
        await _db.SaveChangesAsync();

        // 2026-06-05: si tiene EntregaPor matcheable con un repartidor activo, lo asignamos
        // automaticamente a Mis Pedidos (sin necesidad de escanear QR).
        try { await SincronizarRepartidorDeEntregaAsync(venta); } catch { }

        // Push event-driven a MeLi: fire-and-forget. Si MeLi esta caido o falla, queda
        // marcado con StockChangedAt y el job de respaldo (15 min) lo recupera.
        FireAndForgetPushMeli(productosAfectados);

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
            // 1. Resolver el certificado/CUIT con el que se factura.
            //    - Si la venta trae ArcaWebserviceAccountId (el operador eligió una sociedad en
            //      el selector "¿Con qué facturás?"), usamos ESE certificado.
            //    - Si no, fallback al comportamiento histórico: el CUIT del negocio (CafeSetting).
            var cfg = await _db.CafeSettings.FindAsync(1);
            ArcaWebserviceAccount? arcaAccount;

            if (venta.ArcaWebserviceAccountId.HasValue && venta.ArcaWebserviceAccountId.Value > 0)
            {
                arcaAccount = await _db.ArcaWebserviceAccounts
                    .FirstOrDefaultAsync(a => a.Id == venta.ArcaWebserviceAccountId.Value && a.IsActive);
                if (arcaAccount is null)
                {
                    venta.ArcaEstado = "pendiente";
                    venta.ArcaError = "El certificado ARCA elegido para facturar no existe o está desactivado. Revisá Integraciones → ARCA (webservice).";
                    await _db.SaveChangesAsync();
                    return;
                }
            }
            else
            {
                var cuitEmisor = cfg?.NegocioCuit;
                if (string.IsNullOrWhiteSpace(cuitEmisor))
                {
                    venta.ArcaEstado = "pendiente";
                    venta.ArcaError = "Falta cargar el CUIT en Café → Configuración del negocio.";
                    await _db.SaveChangesAsync();
                    return;
                }

                var cuitDigits = new string(cuitEmisor.Where(char.IsDigit).ToArray());
                arcaAccount = await _db.ArcaWebserviceAccounts
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
            }

            // Dejar registrado en la venta con qué certificado se emitió (aunque haya sido el default),
            // así la Nota de Crédito usa el mismo CUIT y el PDF muestra el emisor correcto.
            venta.ArcaWebserviceAccountId = arcaAccount.Id;

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
                PtoVta = arcaAccount.PtoVta, // punto de venta propio del CUIT/certificado elegido
                CbteTipo = cbteTipo,
                Concepto = venta.Concepto, // 2026-06-23: 1=Productos / 2=Servicios / 3=Mixto
                DocTipo = docTipo,
                DocNro = docNro,
                ReceptorNombre = !string.IsNullOrWhiteSpace(venta.ClienteRazonSocialSnapshot)
                    ? venta.ClienteRazonSocialSnapshot!
                    : (venta.ClienteNombreSnapshot ?? "Consumidor Final"),
                ReceptorDomicilio = venta.ClienteDireccionSnapshot,
                CondicionIVAReceptorId = condIvaReceptor,
                Items = items,
                // Pasamos la fecha de la venta (ya normalizada a ART por FechaArgentina).
                // ARCA registra el CAE con esta fecha — fix del bug donde el server UTC
                // del contenedor estaba un dia atras y la factura salia con fecha de ayer.
                Fecha = venta.Fecha,
                // 2026-06-23: fechas de prestacion. Solo se mandan al SOAP cuando Concepto != 1.
                // Si el usuario marca Servicios/Mixto y no carga fechas, usamos la fecha de la venta.
                FchServDesde = venta.Concepto != 1 ? (venta.ConceptoServDesde ?? venta.Fecha) : null,
                FchServHasta = venta.Concepto != 1 ? (venta.ConceptoServHasta ?? venta.Fecha) : null,
                // FchVtoPago (vencimiento de pago) NO puede ser anterior a la fecha del comprobante (ARCA [10036]).
                // Si el período de servicio terminó en el pasado, usamos la fecha del comprobante.
                FchVtoPago = venta.Concepto != 1
                    ? (venta.ConceptoServHasta.HasValue && venta.ConceptoServHasta.Value >= venta.Fecha
                        ? venta.ConceptoServHasta.Value : venta.Fecha)
                    : null,
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
    ///
    /// 2026-06-18: tambien acepta ventas que YA estan como FA/FB/FC pero SIN CAE
    /// (estado inconsistente, tipico de cuando un usuario edito una X y le puso "Factura A"
    /// + "Guardar cambios" sin emitir realmente a ARCA). En ese caso la revertimos
    /// internamente a X antes de seguir, asi queda como cotizacion marcada
    /// "facturada como ..." y la nueva tiene CAE limpio.
    /// </summary>
    [HttpPost("{id:int}/convertir-a-factura")]
    public async Task<IActionResult> ConvertirAFactura(int id, [FromBody] ConvertirAFacturaRequest req)
    {
        var original = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (original is null) return NotFound(new { error = "Venta no encontrada" });
        if (original.Estado == "anulado")
            return BadRequest(new { error = "No se puede facturar una venta anulada." });
        if (original.FacturadaComoVentaId.HasValue)
            return BadRequest(new { error = "Esta venta ya fue convertida a la factura #" + original.FacturadaComoVentaId });

        var esXoPro = original.TipoComprobante is "X" or "PRO";
        var esFacturaSinCae = original.TipoComprobante is "FA" or "FB" or "FC"
                              && string.IsNullOrEmpty(original.ArcaCae);
        if (!esXoPro && !esFacturaSinCae)
            return BadRequest(new { error = "Solo se pueden convertir cotizaciones (X/PRO) o facturas sin CAE." });

        // Caso "FA sin CAE" (atascado): la revertimos a X para que quede limpia como cotizacion facturada.
        if (esFacturaSinCae)
        {
            original.TipoComprobante = "X";
            original.UpdatedAt = DateTime.UtcNow;
        }

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
            // Sociedad/CUIT elegido para facturar (si no viene, hereda el de la proforma o el default).
            ArcaWebserviceAccountId = req.ArcaWebserviceAccountId ?? original.ArcaWebserviceAccountId,
        };

        // 2026-06-18 — La X / PRO ya tenia su stock descontado al crearse. Si vamos a crear una FA
        // que va a descontar stock OTRA VEZ, terminamos con doble descuento (y peor: el cotizador
        // interno del Create dice "no hay stock" si el inventario disponible quedo justo). Solucion:
        // DEVOLVER el stock de la X antes de llamar a Create. Si Create falla, restauramos.
        var stockSnapshots = new List<(int prodId, decimal gramos, int unidades)>();
        foreach (var it in original.Items)
        {
            if (it.EsConceptoLibre || it.ServicioId.HasValue || it.ProductoId is null) continue;
            var prod = await _db.CafeProductos.FindAsync(it.ProductoId.Value);
            if (prod is null) continue;
            if (prod.Categoria == "CAFE")
            {
                prod.StockGramos += it.GramosDescontados;
                stockSnapshots.Add((prod.Id, it.GramosDescontados, 0));
            }
            else
            {
                var unidadesADevolver = UnidadesRealesStock(prod, it.Formato, it.Cantidad);
                prod.StockUnidades += unidadesADevolver;
                stockSnapshots.Add((prod.Id, 0m, unidadesADevolver));
            }
            prod.StockChangedAt = DateTime.UtcNow;
            await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
        }
        await _db.SaveChangesAsync();

        // Reusamos la lógica de Create (más simple que duplicar el código)
        IActionResult createResult;
        try
        {
            createResult = await Create(createReq);
        }
        catch
        {
            await RevertirStockDevueltoAsync(stockSnapshots);
            throw;
        }
        if (createResult is not OkObjectResult ok || ok.Value is not CafeVentaDto creada)
        {
            // Create rebotó (ej. ARCA falla, validacion, etc.): re-descontamos lo que devolvimos.
            await RevertirStockDevueltoAsync(stockSnapshots);
            return createResult;
        }

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

    /// <summary>2026-06-18 — Helper para ConvertirAFactura: si Create falla despues de haber
    /// devuelto el stock de la X, restauramos el descuento para que el inventario no quede inflado.</summary>
    private async Task RevertirStockDevueltoAsync(List<(int prodId, decimal gramos, int unidades)> snapshots)
    {
        foreach (var snap in snapshots)
        {
            var prod = await _db.CafeProductos.FindAsync(snap.prodId);
            if (prod is null) continue;
            prod.StockGramos -= snap.gramos;
            prod.StockUnidades -= snap.unidades;
            prod.StockChangedAt = DateTime.UtcNow;
            await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
        }
        await _db.SaveChangesAsync();
    }

    public record EmitirNotaCreditoRequest(string? Motivo);

    /// <summary>
    /// 2026-06-09 — Fase 1 Nota de Credito (NC TOTAL).
    /// Crea una nueva Cafe_Venta tipo NCA/NCB/NCC clonando la factura original COMPLETA,
    /// la emite contra ARCA con CbtesAsoc apuntando al cbte origen, devuelve stock,
    /// y marca la factura original con NotaCreditoVentaId.
    /// </summary>
    [HttpPost("{id:int}/nota-credito")]
    public async Task<IActionResult> EmitirNotaCredito(int id, [FromBody] EmitirNotaCreditoRequest? req)
    {
        var original = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (original is null) return NotFound(new { error = "Venta no encontrada" });

        // Validaciones
        if (original.Estado != "emitido") return BadRequest(new { error = "Solo se puede emitir NC sobre ventas emitidas" });
        if (string.IsNullOrEmpty(original.ArcaCae)) return BadRequest(new { error = "La factura no tiene CAE — no se puede emitir NC sin CAE" });
        if (original.NotaCreditoVentaId.HasValue) return BadRequest(new { error = $"Esta factura ya tiene una NC emitida (venta #{original.NotaCreditoVentaId})" });
        if (original.VentaOrigenNcId.HasValue) return BadRequest(new { error = "Esta venta YA ES una NC — no se puede emitir NC sobre una NC" });
        if (original.TipoComprobante is not "FA" and not "FB" and not "FC")
            return BadRequest(new { error = $"Tipo de comprobante origen no soportado para NC: {original.TipoComprobante}" });

        // Mapear tipo NC según factura origen: FA(1)→NCA(3), FB(6)→NCB(8), FC(11)→NCC(13)
        var (cbteTipoNc, tipoNcStr) = original.TipoComprobante switch
        {
            "FA" => (3, "NCA"),
            "FB" => (8, "NCB"),
            "FC" => (13, "NCC"),
            _ => (0, "")
        };

        // Cargar la cuenta ARCA. La NC DEBE emitirse con el MISMO CUIT que la factura
        // original (si se facturó con la sociedad de hecho, la NC va contra ese CUIT).
        // Fallback a la primera activa solo para facturas viejas sin cuenta registrada.
        ArcaWebserviceAccount? arcaAccount = null;
        if (original.ArcaWebserviceAccountId.HasValue && original.ArcaWebserviceAccountId.Value > 0)
            arcaAccount = await _db.ArcaWebserviceAccounts
                .FirstOrDefaultAsync(a => a.Id == original.ArcaWebserviceAccountId.Value && a.IsActive);
        arcaAccount ??= await _db.ArcaWebserviceAccounts.FirstOrDefaultAsync(a => a.IsActive);
        if (arcaAccount is null) return BadRequest(new { error = "No hay cuenta ARCA configurada" });

        // ----- Generar numero de comprobante interno (independiente de ARCA) -----
        var numero = await GenerarNumeroAsync();

        // ----- Clonar la venta como NC -----
        var nc = new CafeVenta
        {
            Numero = numero,
            Fecha = DateTime.UtcNow.Date,
            ClienteId = original.ClienteId,
            ClienteNombreSnapshot = original.ClienteNombreSnapshot,
            ClienteRazonSocialSnapshot = original.ClienteRazonSocialSnapshot,
            ClienteTipoSnapshot = original.ClienteTipoSnapshot,
            ClienteTelefonoSnapshot = original.ClienteTelefonoSnapshot,
            ClienteCuitSnapshot = original.ClienteCuitSnapshot,
            ClienteDireccionSnapshot = original.ClienteDireccionSnapshot,
            ClienteLocalidadSnapshot = original.ClienteLocalidadSnapshot,
            ClienteCiudadSnapshot = original.ClienteCiudadSnapshot,
            ClienteCpSnapshot = original.ClienteCpSnapshot,
            ClienteDomicilioEntregaSnapshot = original.ClienteDomicilioEntregaSnapshot,
            CondicionIva = original.CondicionIva,
            CondicionPago = original.CondicionPago,
            TipoComprobante = tipoNcStr,
            Total = original.Total,
            Descuento = original.Descuento,
            IsPaid = false,
            Estado = "emitido",
            Observaciones = string.IsNullOrWhiteSpace(req?.Motivo) ? null : req!.Motivo!.Trim(),
            VentaOrigenNcId = original.Id,
            ArcaWebserviceAccountId = arcaAccount.Id,
            CreatedAt = DateTime.UtcNow,
            // 2026-06-23: la NC hereda el concepto del comprobante original
            Concepto = original.Concepto,
            ConceptoServDesde = original.ConceptoServDesde,
            ConceptoServHasta = original.ConceptoServHasta,
        };
        foreach (var it in original.Items)
        {
            nc.Items.Add(new CafeVentaItem
            {
                ProductoId = it.ProductoId,
                ProductoNombreSnapshot = it.ProductoNombreSnapshot,
                Categoria = it.Categoria,
                Formato = it.Formato,
                Cantidad = it.Cantidad,
                PrecioUnitario = it.PrecioUnitario,
                CostoUnitario = it.CostoUnitario,
                Subtotal = it.Subtotal,
                GramosDescontados = it.GramosDescontados,
                Molienda = it.Molienda,
                EsDoyPack = it.EsDoyPack,
                EsEnvasePlateado = it.EsEnvasePlateado,
                EsConceptoLibre = it.EsConceptoLibre,
                DescuentoPct = it.DescuentoPct,
                ComboOrigenId = it.ComboOrigenId,
                ServicioId = it.ServicioId,
            });
        }
        _db.CafeVentas.Add(nc);
        await _db.SaveChangesAsync(); // nc.Id ya asignado

        // ----- Construir items para ARCA -----
        decimal sumaItems = nc.Items.Sum(i => i.Subtotal);
        decimal factorDescGlobal = (nc.Descuento > 0m && sumaItems > 0m)
            ? Math.Max(0m, 1m - (nc.Descuento / sumaItems)) : 1m;
        var itemsArca = new List<EmitirComprobanteItemDto>();
        foreach (var it in nc.Items)
        {
            var pu = it.DescuentoPct > 0 && it.Cantidad > 0
                ? Math.Round(it.Subtotal / it.Cantidad, 2, MidpointRounding.AwayFromZero)
                : it.PrecioUnitario;
            var puFinal = factorDescGlobal < 1m
                ? Math.Round(pu * factorDescGlobal, 2, MidpointRounding.AwayFromZero) : pu;
            var desc = it.ProductoNombreSnapshot;
            if (!string.IsNullOrEmpty(it.Molienda)) desc += $" — {it.Molienda}";
            if (it.EsDoyPack) desc += " (d.p.)"; else if (it.EsEnvasePlateado) desc += " (env. plat.)";
            if (!it.EsConceptoLibre) desc += $" · {it.Formato}";
            itemsArca.Add(new EmitirComprobanteItemDto { Descripcion = desc, Cantidad = it.Cantidad, PrecioUnitario = puFinal, AlicIvaId = 5 });
        }

        // Receptor doc
        int docTipo = !string.IsNullOrEmpty(nc.ClienteCuitSnapshot) ? 80 : 99;
        string docNro = !string.IsNullOrEmpty(nc.ClienteCuitSnapshot) ? nc.ClienteCuitSnapshot! : "0";
        int condIvaReceptor = (nc.CondicionIva ?? "CF") switch
        {
            "RI" => 1, "EX" => 4, "MO" => 6, _ => 5
        };

        // ----- Request ARCA con CbtesAsoc -----
        var reqArca = new EmitirComprobanteRequest
        {
            PtoVta = original.ArcaPtoVta ?? 2,
            CbteTipo = cbteTipoNc,
            Concepto = nc.Concepto, // 2026-06-23: heredado del original
            DocTipo = docTipo,
            DocNro = docNro,
            ReceptorNombre = nc.ClienteRazonSocialSnapshot ?? nc.ClienteNombreSnapshot ?? "Consumidor Final",
            ReceptorDomicilio = nc.ClienteDireccionSnapshot,
            CondicionIVAReceptorId = condIvaReceptor,
            Items = itemsArca,
            Fecha = nc.Fecha,
            CbtesAsoc = new List<CbteAsocDto>
            {
                new CbteAsocDto
                {
                    Tipo = original.ArcaCbteTipoNum ?? 0,
                    PtoVta = original.ArcaPtoVta ?? 0,
                    Nro = original.ArcaCbteNro ?? 0,
                }
            },
            // 2026-06-23: si la NC es de servicios/mixto, mandar fechas de prestacion al SOAP.
            FchServDesde = nc.Concepto != 1 ? (nc.ConceptoServDesde ?? nc.Fecha) : null,
            FchServHasta = nc.Concepto != 1 ? (nc.ConceptoServHasta ?? nc.Fecha) : null,
            // Igual que en la emisión: FchVtoPago no puede ser anterior a la fecha del comprobante (ARCA [10036]).
            FchVtoPago = nc.Concepto != 1
                ? (nc.ConceptoServHasta.HasValue && nc.ConceptoServHasta.Value >= nc.Fecha
                    ? nc.ConceptoServHasta.Value : nc.Fecha)
                : null,
        };

        // ----- Emitir contra ARCA -----
        ComprobanteEmitidoDto? resultado = null;
        try
        {
            resultado = await _arcaInvoiceService.EmitirComprobanteAsync(arcaAccount.Id, reqArca);
        }
        catch (Exception ex)
        {
            nc.ArcaEstado = "pendiente";
            nc.ArcaError = "Error inesperado: " + ex.Message;
            await _db.SaveChangesAsync();
            return BadRequest(new { error = "ARCA rechazó la NC: " + ex.Message, ncVentaId = nc.Id });
        }
        if (resultado is null || !resultado.Success)
        {
            nc.ArcaEstado = "pendiente";
            nc.ArcaError = resultado?.Error ?? "ARCA rechazó la NC.";
            await _db.SaveChangesAsync();
            return BadRequest(new { error = nc.ArcaError, ncVentaId = nc.Id });
        }

        // ----- Persistir resultado ARCA en la NC -----
        nc.ArcaEstado = "autorizado";
        nc.ArcaCae = resultado.Cae;
        if (DateTime.TryParseExact(resultado.CaeVto, "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var caeVto))
            nc.ArcaCaeVto = caeVto;
        nc.ArcaPtoVta = resultado.PtoVta;
        nc.ArcaCbteNro = resultado.CbteNro;
        nc.ArcaCbteTipoNum = resultado.CbteTipo;
        nc.ArcaImpNeto = resultado.ImpNeto;
        nc.ArcaImpIVA = resultado.ImpIVA;
        nc.ArcaImpTotal = resultado.ImpTotal;
        nc.ArcaError = null;

        // ----- Marca la factura original con NotaCreditoVentaId -----
        original.NotaCreditoVentaId = nc.Id;
        original.UpdatedAt = DateTime.UtcNow;

        // ----- Devolver stock al inventario (Fase 1: devolucion COMPLETA) -----
        foreach (var it in original.Items)
        {
            if (it.EsConceptoLibre || it.ServicioId.HasValue || it.ProductoId is null) continue;
            var prod = await _db.CafeProductos.FindAsync(it.ProductoId.Value);
            if (prod is null) continue;
            if (prod.Categoria == "CAFE")
            {
                prod.StockGramos += it.GramosDescontados;
            }
            else
            {
                var unidadesADevolver = UnidadesRealesStock(prod, it.Formato, it.Cantidad);
                prod.StockUnidades += unidadesADevolver;
            }
            prod.StockChangedAt = DateTime.UtcNow;
            await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
        }

        await _db.SaveChangesAsync();

        return Ok(new
        {
            ok = true,
            ncVentaId = nc.Id,
            ncNumero = nc.Numero,
            ncTipo = tipoNcStr,
            cae = nc.ArcaCae,
            mensaje = $"✅ NC {tipoNcStr} {nc.ArcaPtoVta:0000}-{nc.ArcaCbteNro:00000000} emitida con CAE {nc.ArcaCae}"
        });
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
            DescripcionLibre = i.EsConceptoLibre ? i.ProductoNombreSnapshot : null,
            // 2026-06-02 FIX: traer el precio del comprobante original. Sino el sistema recotiza
            // con el precio actual del catalogo y se pierde la info historica (ej: si subieron
            // los precios desde la venta original, la duplicada saldria mas cara).
            PrecioUnitarioOverride = i.PrecioUnitario
        }).ToList();

        var payload = new DuplicarVentaPayloadDto(
            ClienteId: v.ClienteId,
            ClienteNombre: v.ClienteNombreSnapshot,
            ClienteTipo: v.ClienteTipoSnapshot ?? "OTRO",
            TipoComprobante: v.TipoComprobante,
            CondicionIva: v.CondicionIva,
            CondicionPago: v.CondicionPago,
            WeekDays: v.WeekDays,
            EnRadar: v.EnRadar,
            Retira: v.Retira,
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
        if (req.EnRadar.HasValue) v.EnRadar = req.EnRadar.Value;
        if (req.Retira.HasValue) v.Retira = req.Retira.Value;
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

        // 2026-06-08: Si era PRESUPUESTO (PRO), no había stock descontado → no devolver.
        var eraPresupuesto = string.Equals(v.TipoComprobante, "PRO", StringComparison.OrdinalIgnoreCase);

        // Restaurar stock (concepto libre se saltea, no descontó stock)
        var productosAnular = new List<int>();
        if (!eraPresupuesto)
        {
            foreach (var it in v.Items)
            {
                if (it.EsConceptoLibre || it.ProductoId is null) continue;
                var prod = await _db.CafeProductos.FindAsync(it.ProductoId.Value);
                if (prod is null) continue;
                if (prod.Categoria == "CAFE")
                    prod.StockGramos += it.GramosDescontados;
                else
                {
                    // BULTO / PACK_N → expandir a unidades reales (UxB o N).
                    var unidadesADevolver = UnidadesRealesStock(prod, it.Formato, it.Cantidad);
                    prod.StockUnidades += unidadesADevolver;
                }
                prod.UpdatedAt = DateTime.UtcNow;
                prod.StockChangedAt = DateTime.UtcNow;
                await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
                productosAnular.Add(prod.Id);
            }
        }
        v.Estado = "anulado";
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        FireAndForgetPushMeli(productosAnular);
        return Ok(Map(v));
    }

    /// <summary>Recupera una venta anulada: vuelve a descontar stock + estado pasa de "anulado" a "emitido".
    /// Requiere operador permitido + clave (misma config que delete).</summary>
    [HttpPost("{id:int}/recuperar")]
    public async Task<IActionResult> Recuperar(int id, [FromBody] DeleteCafeVentaRequest req)
    {
        var op = HttpContext.Request.Headers["X-Operator-Name"].ToString();
        try
        {
            await ValidateDeletePermissionAsync(op, req.Password);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }

        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        if (v.Estado != "anulado") return BadRequest(new { error = $"La venta no esta anulada (estado actual: {v.Estado}). Solo se pueden recuperar ventas anuladas." });

        // 2026-06-08: Si es PRESUPUESTO (PRO), no descontar stock al recuperar (nunca se descontó).
        var esPresupuestoRecup = string.Equals(v.TipoComprobante, "PRO", StringComparison.OrdinalIgnoreCase);

        // Volver a descontar stock (inverso del Anular). Concepto libre se saltea.
        var productosRecuperar = new List<int>();
        if (!esPresupuestoRecup)
        {
            foreach (var it in v.Items)
            {
                if (it.EsConceptoLibre || it.ProductoId is null) continue;
                var prod = await _db.CafeProductos.FindAsync(it.ProductoId.Value);
                if (prod is null) continue;
                if (prod.Categoria == "CAFE")
                    prod.StockGramos -= it.GramosDescontados;
                else
                {
                    var unidadesADescontar = UnidadesRealesStock(prod, it.Formato, it.Cantidad);
                    prod.StockUnidades -= unidadesADescontar;
                }
                prod.UpdatedAt = DateTime.UtcNow;
                prod.StockChangedAt = DateTime.UtcNow;
                await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
                productosRecuperar.Add(prod.Id);
            }
        }
        v.Estado = "emitido";
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        FireAndForgetPushMeli(productosRecuperar);
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
        var prodIds = await DeleteVentaInternalAsync(v);
        await _db.SaveChangesAsync();
        FireAndForgetPushMeli(prodIds);
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

        var allBulkProdIds = new HashSet<int>();
        foreach (var v in borrables)
        {
            var ids = await DeleteVentaInternalAsync(v);
            foreach (var id2 in ids) allBulkProdIds.Add(id2);
        }

        await _db.SaveChangesAsync();
        FireAndForgetPushMeli(allBulkProdIds.ToList());
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
        if (req.EnRadar.HasValue) v.EnRadar = req.EnRadar.Value;
        if (req.Retira.HasValue) v.Retira = req.Retira.Value;
        if (req.PorTransporte.HasValue) v.PorTransporte = req.PorTransporte.Value;
        if (req.TransporteEmpresa is not null) v.TransporteEmpresa = string.IsNullOrWhiteSpace(req.TransporteEmpresa) ? null : req.TransporteEmpresa.Trim();
        if (req.TransporteDestino is not null) v.TransporteDestino = string.IsNullOrWhiteSpace(req.TransporteDestino) ? null : req.TransporteDestino.Trim();
        if (req.IsPaid.HasValue) v.IsPaid = req.IsPaid.Value;
        if (req.EntregaPor is not null) v.EntregaPor = string.IsNullOrWhiteSpace(req.EntregaPor) ? null : req.EntregaPor.Trim();
        if (req.ComentarioArmado is not null) v.ComentarioArmado = string.IsNullOrWhiteSpace(req.ComentarioArmado) ? null : req.ComentarioArmado.Trim();
        // 2026-06-23: Concepto AFIP. Solo aplica al editar antes de emitir (post-emision no toca esto).
        if (req.Concepto.HasValue && (req.Concepto.Value == 1 || req.Concepto.Value == 2 || req.Concepto.Value == 3))
            v.Concepto = req.Concepto.Value;
        if (req.ConceptoServDesde.HasValue) v.ConceptoServDesde = req.ConceptoServDesde.Value;
        if (req.ConceptoServHasta.HasValue) v.ConceptoServHasta = req.ConceptoServHasta.Value;
        // 2026-07-02: link de Maps de la venta + opcional guardar en la ficha del cliente
        if (req.MapeoLink is not null) v.MapeoLink = string.IsNullOrWhiteSpace(req.MapeoLink) ? null : req.MapeoLink.Trim();
        if (req.GuardarMapeoEnCliente && !string.IsNullOrWhiteSpace(req.MapeoLink))
        {
            var cliMapeoId = req.ClienteId is > 0 ? req.ClienteId.Value : v.ClienteId;
            if (cliMapeoId is > 0)
            {
                var cliMapeo = await _db.CafeClientes.FindAsync(cliMapeoId.Value);
                if (cliMapeo is not null) { cliMapeo.MapeoLink = req.MapeoLink.Trim(); cliMapeo.MapeoLat = null; cliMapeo.MapeoLng = null; }
            }
        }

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
                // Modo "Venta Rápida": cliente ad-hoc — todos los datos vienen de los overrides.
                v.ClienteId = null;
                v.ClienteNombreSnapshot = string.IsNullOrWhiteSpace(req.ClienteNombreOverride)
                    ? "Consumidor final" : req.ClienteNombreOverride.Trim();
                v.ClienteTipoSnapshot = CafePricingService.ResolverTipo(req.ClienteTipoOverride);
                v.ClienteTelefonoSnapshot = NzU(req.ClienteTelefonoOverride);
                v.ClienteRazonSocialSnapshot = NzU(req.ClienteRazonSocialOverride);
                v.ClienteDomicilioEntregaSnapshot = NzU(req.ClienteDomicilioEntregaOverride);
                v.ClienteComentariosComprobante = null;
                v.ClienteCuitSnapshot = NzU(req.ClienteCuitOverride);
                v.ClienteDireccionSnapshot = NzU(req.ClienteDireccionOverride);
                v.ClienteLocalidadSnapshot = NzU(req.ClienteLocalidadOverride);
                v.ClienteCiudadSnapshot = NzU(req.ClienteCiudadOverride);
                v.ClienteCpSnapshot = NzU(req.ClienteCpOverride);
            }
        }
        else if (!v.ClienteId.HasValue && !string.IsNullOrWhiteSpace(req.ClienteNombreOverride))
        {
            v.ClienteNombreSnapshot = req.ClienteNombreOverride.Trim();
            if (!string.IsNullOrWhiteSpace(req.ClienteTipoOverride))
                v.ClienteTipoSnapshot = CafePricingService.ResolverTipo(req.ClienteTipoOverride);
            // En venta rápida, también permitimos actualizar el resto de los datos sin remapear ClienteId.
            if (req.ClienteRazonSocialOverride is not null) v.ClienteRazonSocialSnapshot = NzU(req.ClienteRazonSocialOverride);
            if (req.ClienteCuitOverride is not null) v.ClienteCuitSnapshot = NzU(req.ClienteCuitOverride);
            if (req.ClienteDireccionOverride is not null) v.ClienteDireccionSnapshot = NzU(req.ClienteDireccionOverride);
            if (req.ClienteLocalidadOverride is not null) v.ClienteLocalidadSnapshot = NzU(req.ClienteLocalidadOverride);
            if (req.ClienteCiudadOverride is not null) v.ClienteCiudadSnapshot = NzU(req.ClienteCiudadOverride);
            if (req.ClienteCpOverride is not null) v.ClienteCpSnapshot = NzU(req.ClienteCpOverride);
            if (req.ClienteTelefonoOverride is not null) v.ClienteTelefonoSnapshot = NzU(req.ClienteTelefonoOverride);
            if (req.ClienteDomicilioEntregaOverride is not null) v.ClienteDomicilioEntregaSnapshot = NzU(req.ClienteDomicilioEntregaOverride);
        }

        static string? NzU(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
                var productosUpdate = new HashSet<int>();
                foreach (var item in v.Items)
                {
                    if (item.EsConceptoLibre || item.ProductoId is null) continue;
                    var prod = await _db.CafeProductos.FindAsync(item.ProductoId.Value);
                    if (prod is null) continue;
                    if (prod.Categoria == "CAFE") prod.StockGramos += item.GramosDescontados;
                    else
                    {
                        var unidadesADevolver = UnidadesRealesStock(prod, item.Formato, item.Cantidad);
                        prod.StockUnidades += unidadesADevolver;
                    }
                    prod.UpdatedAt = DateTime.UtcNow;
                    prod.StockChangedAt = DateTime.UtcNow;
                    await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
                    productosUpdate.Add(prod.Id);
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
                for (int idx = 0; idx < cot.Items.Count; idx++)
                {
                    var ci = cot.Items[idx];
                    var reqItem = idx < req.Items.Count ? req.Items[idx] : null;
                    var nombreOverride = reqItem is not null && !string.IsNullOrWhiteSpace(reqItem.DescripcionOverride)
                        ? reqItem.DescripcionOverride.Trim()
                        : null;

                    // 2026-06-05: Servicio del catalogo (no descuenta stock).
                    if (ci.Categoria == "SERVICIO")
                    {
                        v.Items.Add(new CafeVentaItem
                        {
                            ProductoId = null,
                            ServicioId = reqItem?.ServicioId,
                            EsConceptoLibre = false,
                            ProductoNombreSnapshot = nombreOverride ?? ci.ProductoNombre,
                            Categoria = "SERVICIO",
                            Formato = "UNIT",
                            Cantidad = ci.Cantidad,
                            PrecioUnitario = ci.PrecioUnitario,
                            CostoUnitario = 0m,
                            Subtotal = ci.Subtotal,
                            GramosDescontados = 0m,
                            Molienda = null,
                            EsDoyPack = false,
                            DescuentoPct = ci.DescuentoPct,
                            ComboOrigenId = reqItem?.ComboOrigenId   // 2026-06-08
                        });
                        continue;
                    }

                    // Concepto libre: item sin producto del catálogo (no descuenta stock).
                    if (ci.Categoria == "LIBRE")
                    {
                        v.Items.Add(new CafeVentaItem
                        {
                            ProductoId = null,
                            EsConceptoLibre = true,
                            ProductoNombreSnapshot = nombreOverride ?? ci.ProductoNombre,
                            Categoria = "LIBRE",
                            Formato = "UNIT",
                            Cantidad = ci.Cantidad,
                            PrecioUnitario = ci.PrecioUnitario,
                            CostoUnitario = 0m,
                            Subtotal = ci.Subtotal,
                            GramosDescontados = 0m,
                            Molienda = null,
                            EsDoyPack = false,
                            DescuentoPct = ci.DescuentoPct,
                            ComboOrigenId = reqItem?.ComboOrigenId   // 2026-06-08
                        });
                        continue;
                    }

                    var prod = await _db.CafeProductos.FindAsync(ci.ProductoId);
                    if (prod is null) return BadRequest(new { error = $"Producto {ci.ProductoId} no encontrado" });
                    v.Items.Add(new CafeVentaItem
                    {
                        ProductoId = prod.Id,
                        EsConceptoLibre = false,
                        ProductoNombreSnapshot = nombreOverride ?? prod.Nombre,
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
                        DescuentoPct = ci.DescuentoPct,
                        ComboOrigenId = reqItem?.ComboOrigenId   // 2026-06-08
                    });
                    if (prod.Categoria == "CAFE")
                    {
                        var antesG = (int)Math.Round(prod.StockGramos);
                        prod.StockGramos = Math.Max(0m, prod.StockGramos - ci.GramosNecesarios);
                        await _stockLogger.LogAsync(prod.Id, "VENTA_NUESTRA", antesG, (int)Math.Round(prod.StockGramos),
                            comentario: $"Edit venta #{v.Numero} · {ci.Cantidad}x{ci.Formato} · -{(int)Math.Round(ci.GramosNecesarios)}g",
                            saveChanges: false);
                    }
                    else
                    {
                        var unidadesADescontar = UnidadesRealesStock(prod, ci.Formato, ci.Cantidad);
                        var antes = prod.StockUnidades;
                        prod.StockUnidades = Math.Max(0, prod.StockUnidades - unidadesADescontar);
                        await _stockLogger.LogAsync(prod.Id, "VENTA_NUESTRA", antes, prod.StockUnidades,
                            comentario: $"Edit venta #{v.Numero} · {ci.Cantidad}x{ci.Formato} · -{unidadesADescontar}u",
                            saveChanges: false);
                    }
                    prod.UpdatedAt = DateTime.UtcNow;
                    prod.StockChangedAt = DateTime.UtcNow;
                    await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
                    productosUpdate.Add(prod.Id);
                }

                v.Subtotal = cot.Subtotal;
                v.Descuento = cot.Descuento;
                v.Total = cot.Total;
                v.CostoTotal = cot.CostoTotal;
                v.Margen = cot.Margen;
                v.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
                FireAndForgetPushMeli(productosUpdate.ToList());
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

        // 2026-06-05: si cambio EntregaPor, sincronizar Mis Pedidos
        try { await SincronizarRepartidorDeEntregaAsync(v); } catch { }

        return Ok(Map(v));
    }

    /// <summary>2026-06-05: Normaliza el header X-Operator-Name. Recorta, vacios -> null,
    /// uppercase para que en el listado se vea homogeneo (OSMAR, GERMAN, etc).</summary>
    private static string? NormOperatorName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim().ToUpperInvariant();
        return s.Length > 20 ? s[..20] : s;
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

    /// <summary>2026-06-05: Endpoint para validar la clave al activar un operador protegido
    /// (OSMAR). Reusa la misma password que la de eliminar comprobantes (sales.delete_password)
    /// para que sea una sola clave de admin para todo. Devuelve 200 si OK, 401 si incorrecta.</summary>
    public class ValidateProtectedOperatorRequest { public string Password { get; set; } = ""; }

    [HttpPost("operador-protegido/validar")]
    public async Task<IActionResult> ValidateProtectedOperator([FromBody] ValidateProtectedOperatorRequest req)
    {
        var expectedPassword = (await _db.AppSettings.FindAsync("sales.delete_password"))?.Value ?? "";
        if (string.IsNullOrEmpty(expectedPassword))
            return StatusCode(401, new { error = "No hay clave configurada en el sistema." });
        if (req?.Password != expectedPassword)
            return StatusCode(401, new { error = "Clave incorrecta." });
        return Ok(new { ok = true });
    }

    private async Task<List<int>> DeleteVentaInternalAsync(CafeVenta v)
    {
        // Si estaba emitida, restaurar stock antes de borrar. Concepto libre se saltea.
        var productosDelete = new List<int>();
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
                    var unidadesADevolver = UnidadesRealesStock(prod, it.Formato, it.Cantidad);
                    prod.StockUnidades += unidadesADevolver;
                }
                prod.StockChangedAt = DateTime.UtcNow;
                await Api.Services.CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
                productosDelete.Add(prod.Id);
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
        return productosDelete;
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

    /// <summary>2026-06-01 — Carga los componentes de productos "shell" (con OEM/precio pero sin
    /// stock propio, linkeados a publicaciones MeLi via MeliItemComponentes). Devuelve un dict
    /// productoId → lista de (componenteId, cantidadPorUnidadShell). Usado en venta para validar
    /// stock contra el armable y para decrementar componentes en lugar del producto vacío.</summary>
    private async Task<Dictionary<int, List<(int compProductoId, decimal cantidad)>>> LoadShellComponentsAsync(IEnumerable<int> productIds)
    {
        var dict = new Dictionary<int, List<(int, decimal)>>();
        var prodIdsList = productIds.Distinct().ToList();
        if (prodIdsList.Count == 0) return dict;

        // 2026-06-01 (revertido): volvemos a filtrar por Status active/paused. Las MLAs
        // closed pueden tener composicion fantasma cargada erroneamente desde Contabilium
        // (producto individual mapeado como combo), y aplicarla generaria falsos shells.
        var meliItems = await _db.MeliItems.AsNoTracking()
            .Where(mi => mi.CafeProductoId != null
                && prodIdsList.Contains(mi.CafeProductoId.Value)
                && (mi.Status == "active" || mi.Status == "paused"))
            .Select(mi => new { mi.MeliItemId, ProdId = mi.CafeProductoId!.Value })
            .ToListAsync();
        if (meliItems.Count == 0) return dict;

        var meliIds = meliItems.Select(x => x.MeliItemId).Distinct().ToList();
        var comps = await _db.MeliItemComponentes.AsNoTracking()
            .Where(c => meliIds.Contains(c.MeliItemId))
            .Select(c => new { c.MeliItemId, c.CafeProductoId, c.Cantidad })
            .ToListAsync();
        if (comps.Count == 0) return dict;

        var compsByMeliId = comps.GroupBy(c => c.MeliItemId).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var prodGroup in meliItems.GroupBy(x => x.ProdId))
        {
            // Tomar componentes del primer MLA linkeado (asumimos consistencia entre los MLAs de un mismo producto)
            var firstMeliId = prodGroup.Select(x => x.MeliItemId).FirstOrDefault();
            if (firstMeliId == null) continue;
            if (compsByMeliId.TryGetValue(firstMeliId, out var compsList) && compsList.Count > 0)
            {
                // 2026-06-18: ignorar autoreferencias triviales — si la unica composicion es el propio
                // producto con Cantidad=1, eso NO es un combo armable, es un producto suelto linkeado
                // a MeLi. Crearia un loop logico ("para vender X, armarlo a partir de X").
                // Bug detectado en prod (18/06): 2987 productos sueltos no se podian vender por este motivo.
                if (compsList.Count == 1
                    && compsList[0].CafeProductoId == prodGroup.Key
                    && compsList[0].Cantidad == 1m)
                {
                    continue;
                }
                dict[prodGroup.Key] = compsList.Select(c => (c.CafeProductoId, c.Cantidad)).ToList();
            }
        }
        return dict;
    }

    private async Task<CafeCotizadoDto> CotizarInternoAsync(List<CafeCotizarItemRequest> items, string tipo, decimal descuento, CafeSetting settings, int? editandoVentaId = null)
    {
        var cotizadoItems = new List<CafeCotizadoItemDto>();
        decimal subtotal = 0m, costoTotal = 0m;
        bool todoOk = true;

        // 2026-06-18: si se está editando una venta existente, traemos las cantidades que esa
        // venta ya tiene reservadas (descontadas) para sumarlas al stock disponible y NO
        // contar la propia venta como un conflicto. Sin esto, una cotización X que descontó
        // stock y se quiere editar/convertir-a-factura tiraría "stock insuficiente" porque
        // ese mismo stock ya está reservado por ella.
        var reservaPropiaPorProd = new Dictionary<int, int>();
        var reservaPropiaGramosPorProd = new Dictionary<int, decimal>();
        if (editandoVentaId.HasValue)
        {
            var itemsPropios = await _db.CafeVentaItems.AsNoTracking()
                .Where(vi => vi.VentaId == editandoVentaId.Value && vi.ProductoId != null)
                .Include(vi => vi.ProductoNav)
                .ToListAsync();
            foreach (var vi in itemsPropios)
            {
                if (vi.ProductoId is null || vi.ProductoNav is null) continue;
                var prodId = vi.ProductoId.Value;
                var esCafePropio = vi.ProductoNav.Categoria == "CAFE";
                if (esCafePropio)
                {
                    var gramos = CafePricingService.GramosPorUnidad(vi.Formato) * vi.Cantidad;
                    reservaPropiaGramosPorProd.TryGetValue(prodId, out var accG);
                    reservaPropiaGramosPorProd[prodId] = accG + gramos;
                }
                else
                {
                    var unidades = UnidadesRealesStock(vi.ProductoNav, vi.Formato, vi.Cantidad);
                    reservaPropiaPorProd.TryGetValue(prodId, out var accU);
                    reservaPropiaPorProd[prodId] = accU + unidades;
                }
            }
        }

        // 2026-06-01 — Cargar info de productos "shell" (linkeados a MeLi via componentes).
        // Para esos productos, la validación de stock se hace contra el armable de componentes,
        // no contra StockUnidades del shell (que siempre es 0).
        var prodIdsCotizar = items.Where(i => !i.EsConceptoLibre && i.ProductoId > 0)
            .Select(i => i.ProductoId).Distinct().ToList();
        var shellComps = await LoadShellComponentsAsync(prodIdsCotizar);
        Dictionary<int, (int stockUnidades, int reserva)> compStocksCotizar = new();
        if (shellComps.Count > 0)
        {
            var allCompProdIds = shellComps.Values.SelectMany(l => l.Select(c => c.compProductoId)).Distinct().ToList();
            compStocksCotizar = await _db.CafeProductos.AsNoTracking()
                .Where(p => allCompProdIds.Contains(p.Id))
                .Select(p => new { p.Id, p.StockUnidades, Reserva = p.StockMinimoMeLi ?? 0 })
                .ToDictionaryAsync(p => p.Id, p => (p.StockUnidades, p.Reserva));
        }

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

            // ---- 2026-06-05: Servicio del catálogo (envio, mano de obra, etc) ----
            // Se cargan desde /cafe/servicios. No descuenta stock. Toma el precio del catálogo
            // salvo que el operador haya puesto override.
            if (it.ServicioId.HasValue && it.ServicioId.Value > 0)
            {
                var serv = await _db.CafeServicios.AsNoTracking().FirstOrDefaultAsync(s => s.Id == it.ServicioId.Value);
                if (serv is null)
                {
                    cotizadoItems.Add(new CafeCotizadoItemDto(
                        ProductoId: 0, ProductoNombre: $"Servicio #{it.ServicioId} no encontrado",
                        Categoria: "SERVICIO", Formato: "UNIT", Cantidad: it.Cantidad,
                        PrecioUnitario: 0m, CostoUnitario: 0m, Subtotal: 0m,
                        GramosNecesarios: 0m, StockGramosDisponible: 0m, StockUnidadesDisponible: 0,
                        StockOk: false, Aviso: "Servicio no encontrado", Molienda: null,
                        EsDoyPack: false, DescuentoPct: descPctManual));
                    todoOk = false;
                    continue;
                }
                var pcuServ = it.PrecioUnitarioOverride.HasValue && it.PrecioUnitarioOverride.Value >= 0m
                    ? Math.Round(it.PrecioUnitarioOverride.Value, 2, MidpointRounding.AwayFromZero)
                    : Math.Round(serv.Precio, 2, MidpointRounding.AwayFromZero);
                var finalServ = Math.Round(pcuServ * (1m - descPctManual / 100m), 2, MidpointRounding.AwayFromZero);
                var subServ = Math.Round(finalServ * it.Cantidad, 2, MidpointRounding.AwayFromZero);
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    ProductoId: 0,
                    ProductoNombre: serv.Nombre,
                    Categoria: "SERVICIO",
                    Formato: "UNIT",
                    Cantidad: it.Cantidad,
                    PrecioUnitario: pcuServ,
                    CostoUnitario: 0m,
                    Subtotal: subServ,
                    GramosNecesarios: 0m,
                    StockGramosDisponible: 0m,
                    StockUnidadesDisponible: 0,
                    StockOk: true,
                    Aviso: null,
                    Molienda: null,
                    EsDoyPack: false,
                    DescuentoPct: descPctManual));
                subtotal += subServ;
                continue;
            }

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

            if (!IsFormatoValido(it.Formato))
            {
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    it.ProductoId, "?", "?", it.Formato, it.Cantidad, 0m, 0m, 0m, 0m, 0m, 0,
                    false, "Formato inválido", NormMolienda(it.Molienda), it.EsDoyPack, descPctManual));
                todoOk = false;
                continue;
            }

            var prod = await _db.CafeProductos.Include(p => p.Packs).Include(p => p.OemNav).FirstOrDefaultAsync(p => p.Id == it.ProductoId);
            if (prod is null)
            {
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    it.ProductoId, "?", "?", it.Formato, it.Cantidad, 0m, 0m, 0m, 0m, 0m, 0,
                    false, "Producto no encontrado", NormMolienda(it.Molienda), it.EsDoyPack, descPctManual));
                todoOk = false;
                continue;
            }

            // Validar combinación: formato unitario / bulto / pack solo para OTROS, formatos kg solo para CAFE.
            var esCafe = prod.Categoria == "CAFE";
            var esFormatoCafe = it.Formato is "1KG" or "MEDIO" or "CUARTO";
            var esFormatoBulto = it.Formato == "BULTO";
            var packUnidades = CafePricingService.ParsePackUnidades(it.Formato);
            var esFormatoPack = packUnidades.HasValue;
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
            // PACK_N requiere que el producto tenga un Cafe_ProductoPack activo con Cantidad = N.
            if (esFormatoPack)
            {
                var packRow = prod.Packs?.FirstOrDefault(p => p.IsActive && p.Cantidad == packUnidades!.Value);
                if (packRow is null)
                {
                    cotizadoItems.Add(new CafeCotizadoItemDto(
                        prod.Id, prod.Nombre, prod.Categoria, it.Formato, it.Cantidad, 0m, 0m, 0m, 0m, prod.StockGramos, prod.StockUnidades,
                        false, $"Este producto no tiene un pack x {packUnidades.Value} cargado",
                        NormMolienda(it.Molienda), it.EsDoyPack, descPctManual));
                    todoOk = false;
                    continue;
                }
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
            // BULTO → cantidad × UxB, PACK_N → cantidad × N, sino 1 a 1.
            var unidadesNecesarias = UnidadesRealesStock(prod, it.Formato, it.Cantidad);

            // 2026-06-01 — Stock disponible efectivo. Si es "shell" (producto sin stock propio
            // linkeado a publicación MeLi via componentes), calcular armable desde los componentes.
            // Si es producto físico normal, usar StockUnidades directo.
            bool esShell = shellComps.ContainsKey(prod.Id);
            int stockUnidadesDisponibleEfectivo = prod.StockUnidades;
            // 2026-06-18: si estamos editando una venta, sumar lo que ESA venta ya tenía reservado
            // para no contar la propia venta como conflicto.
            if (reservaPropiaPorProd.TryGetValue(prod.Id, out var reservaUnidades))
                stockUnidadesDisponibleEfectivo += reservaUnidades;
            if (esShell)
            {
                int armable = int.MaxValue;
                foreach (var (compId, cantPorUnidad) in shellComps[prod.Id])
                {
                    if (!compStocksCotizar.TryGetValue(compId, out var cs)) { armable = 0; break; }
                    int dispComp = Math.Max(0, cs.stockUnidades - cs.reserva);
                    int armableEsteComp = cantPorUnidad > 0 ? (int)(dispComp / cantPorUnidad) : 0;
                    if (armableEsteComp < armable) armable = armableEsteComp;
                }
                stockUnidadesDisponibleEfectivo = armable == int.MaxValue ? 0 : armable;
            }

            // 2026-06-18: stock café disponible efectivo, sumando lo reservado por la propia venta si se está editando.
            decimal stockGramosDisponibleEfectivo = prod.StockGramos;
            if (esCafe && reservaPropiaGramosPorProd.TryGetValue(prod.Id, out var reservaGramos))
                stockGramosDisponibleEfectivo += reservaGramos;

            var stockOk = esCafe ? gramosNecesarios <= stockGramosDisponibleEfectivo + 0.001m : unidadesNecesarias <= stockUnidadesDisponibleEfectivo;
            string? aviso = null;
            if (!stockOk)
            {
                string detalleCantidad = esFormatoBulto ? $"{it.Cantidad} bulto×{prod.UxB}"
                    : esFormatoPack ? $"{it.Cantidad} pack×{packUnidades}"
                    : $"{it.Cantidad}";
                aviso = esCafe
                    ? $"Stock insuficiente. Disponible: {stockGramosDisponibleEfectivo:0} g, necesitás {gramosNecesarios:0} g."
                    : esShell
                        ? $"Stock insuficiente. Disponible: {stockUnidadesDisponibleEfectivo} u. 🧩 armable, necesitás {unidadesNecesarias} u ({detalleCantidad})."
                        : $"Stock insuficiente. Disponible: {stockUnidadesDisponibleEfectivo} u, necesitás {unidadesNecesarias} u ({detalleCantidad}).";
                todoOk = false;
            }

            cotizadoItems.Add(new CafeCotizadoItemDto(
                prod.Id, prod.Nombre, prod.Categoria, it.Formato, it.Cantidad,
                precioUnit, costoUnit, subtotalLinea,
                gramosNecesarios, prod.StockGramos, stockUnidadesDisponibleEfectivo,
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

    // 2026-06-08: corregido "MOLIDO ESPRESS" → "MOLIDO EXPRESS" + agregado "MOLIDO CAFETERA ITALIANA"
    private static readonly string[] MoliendasValidas = { "EN GRANOS", "MOLIDO FILTRO", "MOLIDO EXPRESS", "MOLIDO MOKA", "MOLIDO BODUM", "MOLIDO PRENSA FRANCESA", "MOLIDO CAFETERA ITALIANA", "MOLIDO A LA TURCA", "MINI EXPRESS" };
    private static string? NormMolienda(string? m)
    {
        if (string.IsNullOrWhiteSpace(m)) return null;
        var v = m.Trim().ToUpperInvariant();
        // 2026-06-08: alias retro-compatible — el viejo typo "ESPRESS" mapea al nuevo "EXPRESS"
        if (v == "MOLIDO ESPRESS") v = "MOLIDO EXPRESS";
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

    public class UpdatePinNotaRequest { public string? Nota { get; set; } }

    /// <summary>Guarda / actualiza / borra la nota tipo post-it pegada a una venta. Si Nota es null
    /// o vacía, borra la nota (despinea).</summary>
    [HttpPut("{id:int}/pin")]
    public async Task<IActionResult> UpdatePinNota(int id, [FromBody] UpdatePinNotaRequest req)
    {
        var v = await _db.CafeVentas.FindAsync(id);
        if (v is null) return NotFound();
        v.PinNota = string.IsNullOrWhiteSpace(req.Nota) ? null : req.Nota.Trim();
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, pinNota = v.PinNota });
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

    // ═══════════════════════════════════════════════════════════════════════════
    // PREPARACION DE PEDIDOS (2026-05-19)
    // Modulo para que los chicos del deposito armen pedidos: cada venta puede entrar
    // a un flujo de 5 estados (PARA_PREPARAR -> EN_PREPARACION -> LISTO -> EN_CAMINO
    // -> ENTREGADO). El usuario decide cuando una venta entra al flujo apretando un
    // boton "A preparacion" en /cafe/ventas. Cada cambio se loguea en la tabla de
    // auditoria Cafe_VentaPreparacionLog.
    // ═══════════════════════════════════════════════════════════════════════════
    private static readonly string[] EstadosPreparacion = { "PARA_PREPARAR", "EN_PREPARACION", "LISTO", "EN_CAMINO", "ENTREGADO" };

    public record CambiarEstadoPreparacionRequest(string EstadoNuevo, string? OperadorNombre, string? Notas);

    [HttpPatch("{id:int}/estado-preparacion")]
    public async Task<IActionResult> CambiarEstadoPreparacion(int id, [FromBody] CambiarEstadoPreparacionRequest req)
    {
        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound();
        var nuevo = req.EstadoNuevo?.Trim().ToUpperInvariant() ?? "";
        // null como string vacio = "salir del flujo" (sacar la venta del tablero)
        var salirDelFlujo = string.IsNullOrEmpty(nuevo);
        if (!salirDelFlujo && !EstadosPreparacion.Contains(nuevo))
            return BadRequest(new { error = $"Estado invalido. Valores: {string.Join(", ", EstadosPreparacion)}" });

        var anterior = v.EstadoPreparacion;
        v.EstadoPreparacion = salirDelFlujo ? null : nuevo;
        v.PreparacionUpdatedAt = DateTime.UtcNow;
        // 2026-06-03: si el pedido tenia flag "MODIFICADO" y ahora se marca LISTO -> limpiamos el flag.
        // El armador ya re-armo con los cambios y no necesita seguir viendo el chip naranja.
        if (nuevo == "LISTO" && v.ModificadoDespuesDeArmar)
        {
            v.ModificadoDespuesDeArmar = false;
        }
        _db.CafeVentaPreparacionLogs.Add(new CafeVentaPreparacionLog
        {
            VentaId = v.Id,
            EstadoAnterior = anterior,
            EstadoNuevo = v.EstadoPreparacion ?? "(SALIO_FLUJO)",
            OperadorNombre = req.OperadorNombre?.Trim(),
            Notas = req.Notas?.Trim(),
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return Ok(new { id = v.Id, estado = v.EstadoPreparacion });
    }

    [HttpGet("preparacion")]
    public async Task<IActionResult> ListarPreparacion([FromQuery] int dias = 7)
    {
        var desde = DateTime.UtcNow.Date.AddDays(-Math.Max(1, dias));
        // 2026-06-05 v3: SOLO ventas con Drive subido aparecen en el tablero. Las que se
        // cargaron desde oficina (sin tildar "Enviar a IMPRIMIR PEDIDOS DE OSMAR") no van
        // a aparecer porque su DriveSubidoAt queda en null. Si una venta de armado falla
        // al subir, el operador puede re-subirla a mano desde el listado de ventas (boton ☁️).
        var ventas = await _db.CafeVentas
            .Include(v => v.Items)
            .Where(v => v.PreparacionOcultoAt == null
                && (v.EstadoPreparacion == null
                    || v.EstadoPreparacion == "PARA_PREPARAR"
                    || v.EstadoPreparacion == "EN_PREPARACION")
                && v.Estado != "anulado"
                && v.DriveSubidoAt != null
                && v.CreatedAt >= desde)
            // Orden: las que tienen Drive primero por DriveSubidoAt desc, las sin Drive al final por CreatedAt desc
            .OrderByDescending(v => v.DriveSubidoAt ?? v.CreatedAt)
            .Select(v => new
            {
                id = v.Id,
                numero = v.Numero,
                fecha = v.Fecha,
                clienteNombre = v.ClienteNombreSnapshot ?? "Consumidor final",
                clienteRazon = v.ClienteRazonSocialSnapshot,
                clienteLocalidad = v.ClienteLocalidadSnapshot,
                clienteCiudad = v.ClienteCiudadSnapshot,
                clienteTipo = v.ClienteTipoSnapshot,
                // 2026-05-30: info adicional para que el armador del depósito tenga más contexto
                clienteTelefono = v.ClienteTelefonoSnapshot,
                domicilioEntrega = v.ClienteDomicilioEntregaSnapshot,
                observaciones = v.Observaciones,
                comentariosCliente = v.ClienteComentariosComprobante,
                weekDays = v.WeekDays,
                entregaPor = v.EntregaPor,
                estadoPreparacion = v.EstadoPreparacion,
                preparacionUpdatedAt = v.PreparacionUpdatedAt,
                // Total real cobrable (con IVA si es factura ARCA)
                total = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total,
                // Drive: para mostrar boton "Ver PDF" / "Subir a Drive" en la tarjeta de preparacion.
                driveFileId = v.DriveFileId,
                driveSubidoAt = v.DriveSubidoAt,
                // 2026-06-05: chip rojo "SIN DRIVE" cuando la venta entro a preparacion pero el PDF
                // no se pudo subir (token expirado, error transitorio, etc).
                sinDrive = v.DriveSubidoAt == null,
                // Mini impresora: flag por cliente + tracking de impresion (2026-05-28)
                tieneMiniImpresora = v.ClienteId != null && _db.CafeClientes.Where(c => c.Id == v.ClienteId).Select(c => c.TieneMiniImpresora).FirstOrDefault(),
                impresaAt = v.ImpresaAt,
                impresaCount = v.ImpresaCount,
                // 2026-06-02: nota interna del armado (post-it amarillo en la card)
                comentarioArmado = v.ComentarioArmado,
                // 2026-06-03: flag MODIFICADO — el pedido se edito despues de armado y se re-subio
                modificadoDespuesDeArmar = v.ModificadoDespuesDeArmar,
                items = v.Items.Select(i => new
                {
                    id = i.Id,
                    productoNombre = i.ProductoNombreSnapshot,
                    // 2026-05-30: SKU del producto (si está linkeado al catálogo) — pedido del depósito
                    sku = i.ProductoId != null ? _db.CafeProductos.Where(p => p.Id == i.ProductoId).Select(p => p.Sku).FirstOrDefault() : null,
                    // 2026-06-15: stock del sistema al lado del SKU — el armador ve cuánto debería haber físico.
                    stockUnidades = i.ProductoId != null ? _db.CafeProductos.Where(p => p.Id == i.ProductoId).Select(p => (int?)p.StockUnidades).FirstOrDefault() : null,
                    stockGramos = i.ProductoId != null ? _db.CafeProductos.Where(p => p.Id == i.ProductoId).Select(p => (decimal?)p.StockGramos).FirstOrDefault() : null,
                    formato = i.Formato,
                    cantidad = i.Cantidad,
                    molienda = i.Molienda,
                    esDoyPack = i.EsDoyPack,
                    esEnvasePlateado = i.EsEnvasePlateado,
                    categoria = i.Categoria,
                    esConceptoLibre = i.EsConceptoLibre,
                    // 2026-06-08: si el item viene de un combo, exponerlo para que el armador vea
                    // el header "📦 COMBO XYZ" arriba de los componentes en /cafe/preparacion
                    comboOrigenId = i.ComboOrigenId,
                    comboOrigenNombre = i.ComboOrigenId != null ? _db.Set<CafeCombo>().Where(c => c.Id == i.ComboOrigenId).Select(c => c.Nombre).FirstOrDefault() : null,
                    comboOrigenSku = i.ComboOrigenId != null ? _db.Set<CafeCombo>().Where(c => c.Id == i.ComboOrigenId).Select(c => c.Sku).FirstOrDefault() : null,
                    // 2026-06-17: unidades por bulto del producto — el armador necesita saber cuantas unidades trae cada bulto
                    uxB = i.ProductoId != null ? _db.CafeProductos.Where(p => p.Id == i.ProductoId).Select(p => p.UxB).FirstOrDefault() : null
                }).ToList()
            })
            .ToListAsync();
        return Ok(ventas);
    }

    /// <summary>Oculta UNA venta del tablero de Preparacion. La venta y el PDF en Drive
    /// siguen intactos — solo deja de mostrarse en /cafe/preparacion. Usado por el boton X
    /// de cada card y por "Limpiar tablero" (que llama a este endpoint en bucle).</summary>
    [HttpPost("preparacion/ocultar/{id:int}")]
    public async Task<IActionResult> OcultarDeTablero(int id)
    {
        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound();
        if (v.PreparacionOcultoAt is not null) return Ok(new { id, oculto = true, mensaje = "Ya estaba oculto" });
        v.PreparacionOcultoAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { id, oculto = true });
    }

    /// <summary>Oculta TODAS las ventas que estan actualmente en el tablero (mismo filtro que
    /// /preparacion). Despues de esto, /cafe/preparacion queda vacio. Las ventas nuevas que se
    /// suban a Drive de aca en mas SI aparecen (porque su PreparacionOcultoAt arranca en null).
    /// Pedido del usuario 2026-05-28: "arranquemos el tablero limpio".</summary>
    [HttpPost("preparacion/limpiar-tablero")]
    public async Task<IActionResult> LimpiarTablero([FromQuery] int dias = 7)
    {
        var desde = DateTime.UtcNow.Date.AddDays(-Math.Max(1, dias));
        var ahora = DateTime.UtcNow;
        var ventas = await _db.CafeVentas
            .Where(v => v.DriveSubidoAt != null
                && v.PreparacionOcultoAt == null
                && (v.EstadoPreparacion == null
                    || v.EstadoPreparacion == "PARA_PREPARAR"
                    || v.EstadoPreparacion == "EN_PREPARACION")
                && v.CreatedAt >= desde
                && v.Estado != "anulado")
            .ToListAsync();
        foreach (var v in ventas) v.PreparacionOcultoAt = ahora;
        await _db.SaveChangesAsync();
        return Ok(new { ocultas = ventas.Count });
    }

    /// <summary>Lista las ventas ya MARCADAS COMO LISTO (historial) — para la seccion colapsable
    /// "Ya armados" del tablero. Mismo filtro que /preparacion pero invertido en estado.
    /// Pedido 2026-05-28. Rango actualizado 2026-06-03: hoy/ayer/7d/30d/todos.</summary>
    [HttpGet("preparacion/armados")]
    public async Task<IActionResult> ListarArmados([FromQuery] string rango = "7d", [FromQuery] int? dias = null)
    {
        // Calcular fecha "hoy" en hora Argentina (UTC-3) y volver a UTC para la query
        var argHoy = DateTime.UtcNow.AddHours(-3).Date;
        DateTime? desdeUtc = null;
        DateTime? hastaUtc = null;

        // Compat: si llega ?dias=N (uso viejo) lo respetamos
        if (dias.HasValue)
        {
            desdeUtc = DateTime.UtcNow.Date.AddDays(-Math.Max(1, dias.Value));
        }
        else
        {
            switch ((rango ?? "7d").ToLowerInvariant())
            {
                case "hoy":
                    desdeUtc = argHoy.AddHours(3);                 // 00:00 ART → UTC
                    hastaUtc = argHoy.AddDays(1).AddHours(3);      // mañana 00:00 ART → UTC
                    break;
                case "ayer":
                    desdeUtc = argHoy.AddDays(-1).AddHours(3);
                    hastaUtc = argHoy.AddHours(3);
                    break;
                case "30d":
                    desdeUtc = argHoy.AddDays(-30).AddHours(3);
                    break;
                case "todos":
                    desdeUtc = null;
                    break;
                case "7d":
                default:
                    desdeUtc = argHoy.AddDays(-7).AddHours(3);
                    break;
            }
        }

        var query = _db.CafeVentas
            .Include(v => v.Items)
            .Where(v => v.DriveSubidoAt != null
                && v.PreparacionOcultoAt == null
                && (v.EstadoPreparacion == "LISTO"
                    || v.EstadoPreparacion == "EN_CAMINO"
                    || v.EstadoPreparacion == "ENTREGADO")
                && v.Estado != "anulado");

        if (desdeUtc.HasValue) query = query.Where(v => v.CreatedAt >= desdeUtc.Value);
        if (hastaUtc.HasValue) query = query.Where(v => v.CreatedAt < hastaUtc.Value);

        var ventas = await query
            .OrderByDescending(v => v.PreparacionUpdatedAt ?? v.DriveSubidoAt)
            .Select(v => new
            {
                id = v.Id,
                numero = v.Numero,
                fecha = v.Fecha,
                clienteNombre = v.ClienteNombreSnapshot ?? "Consumidor final",
                clienteLocalidad = v.ClienteLocalidadSnapshot,
                clienteCiudad = v.ClienteCiudadSnapshot,
                estadoPreparacion = v.EstadoPreparacion,
                preparacionUpdatedAt = v.PreparacionUpdatedAt,
                driveFileId = v.DriveFileId,
                driveSubidoAt = v.DriveSubidoAt,
                tieneMiniImpresora = v.ClienteId != null && _db.CafeClientes.Where(c => c.Id == v.ClienteId).Select(c => c.TieneMiniImpresora).FirstOrDefault(),
                impresaAt = v.ImpresaAt,
                impresaCount = v.ImpresaCount,
                itemsCount = v.Items.Count,
                // 2026-06-03: traer items para tooltip al hacer hover en /cafe/preparacion seccion "Ya armados"
                items = v.Items.Select(i => new
                {
                    id = i.Id,
                    productoNombre = i.ProductoNombreSnapshot,
                    formato = i.Formato,
                    cantidad = i.Cantidad,
                    molienda = i.Molienda,
                    esDoyPack = i.EsDoyPack,
                    esEnvasePlateado = i.EsEnvasePlateado,
                    categoria = i.Categoria,
                    esConceptoLibre = i.EsConceptoLibre,
                    // 2026-06-08: idem que en /preparacion — desglose visible para el armador
                    comboOrigenId = i.ComboOrigenId,
                    comboOrigenNombre = i.ComboOrigenId != null ? _db.Set<CafeCombo>().Where(c => c.Id == i.ComboOrigenId).Select(c => c.Nombre).FirstOrDefault() : null,
                    comboOrigenSku = i.ComboOrigenId != null ? _db.Set<CafeCombo>().Where(c => c.Id == i.ComboOrigenId).Select(c => c.Sku).FirstOrDefault() : null,
                    // 2026-06-17: UxB para que el chip "Bulto x N u." funcione tambien en la seccion "Ya armados"
                    uxB = i.ProductoId != null ? _db.CafeProductos.Where(p => p.Id == i.ProductoId).Select(p => p.UxB).FirstOrDefault() : null
                }).ToList()
            })
            .ToListAsync();
        return Ok(ventas);
    }

    /// <summary>Marca una venta como impresa (actualiza ImpresaAt + incrementa ImpresaCount).
    /// Pedido 2026-05-28 — botón mini impresora en /cafe/preparacion.</summary>
    [HttpPost("{id:int}/marcar-impresa")]
    public async Task<IActionResult> MarcarImpresa(int id)
    {
        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound();
        v.ImpresaAt = DateTime.UtcNow;
        v.ImpresaCount = v.ImpresaCount + 1;
        await _db.SaveChangesAsync();
        return Ok(new { id, impresaAt = v.ImpresaAt, impresaCount = v.ImpresaCount });
    }

    /// <summary>Devuelve UN PDF que combina los comprobantes de varias ventas (para imprimir
    /// todo junto en una sola operacion). Tambien marca cada una como impresa.
    /// IDs van separados por coma: ?ids=1,2,3 — máximo 50 por request.
    /// Pedido 2026-05-28.</summary>
    [HttpGet("imprimir-pdf-combinado")]
    public async Task<IActionResult> ImprimirPdfCombinado([FromQuery] string ids)
    {
        if (string.IsNullOrWhiteSpace(ids)) return BadRequest(new { error = "Faltan IDs" });
        var idList = ids.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
            .Where(n => n > 0)
            .Distinct()
            .Take(50)
            .ToList();
        if (idList.Count == 0) return BadRequest(new { error = "IDs invalidos" });

        var cfg = await _db.CafeSettings.FindAsync(1);
        var ventas = await _db.CafeVentas
            .Include(x => x.Items).ThenInclude(i => i.ProductoNav)
            .Where(x => idList.Contains(x.Id))
            .ToListAsync();
        // Mantener orden de los IDs recibidos
        ventas = idList.Select(id => ventas.FirstOrDefault(v => v.Id == id)).Where(v => v != null).Cast<Models.CafeVenta>().ToList();
        if (ventas.Count == 0) return NotFound(new { error = "Ninguna venta encontrada" });

        // Generar bytes de cada PDF y concatenar con PdfSharpCore
        var combined = new PdfSharpCore.Pdf.PdfDocument();
        var ahora = DateTime.UtcNow;
        foreach (var v in ventas)
        {
            var bytes = await GenerarPdfBytesAsync(v, cfg);
            using var ms = new MemoryStream(bytes);
            var src = PdfSharpCore.Pdf.IO.PdfReader.Open(ms, PdfSharpCore.Pdf.IO.PdfDocumentOpenMode.Import);
            for (int i = 0; i < src.PageCount; i++)
            {
                combined.AddPage(src.Pages[i]);
            }
            v.ImpresaAt = ahora;
            v.ImpresaCount = v.ImpresaCount + 1;
        }
        await _db.SaveChangesAsync();

        using var outMs = new MemoryStream();
        combined.Save(outMs, false);
        var pdfBytes = outMs.ToArray();
        var fileName = ventas.Count == 1
            ? BuildPdfFilename(ventas[0])
            : $"imprimir-{ventas.Count}-comprobantes-{DateTime.Now:yyyyMMddHHmm}.pdf";
        return File(pdfBytes, "application/pdf", fileName);
    }

    /// <summary>
    /// 2026-06-05: Sincroniza el escaneo "cargado" de Cafe_QrEscaneos con el valor
    /// actual de EntregaPor. Si EntregaPor matchea un repartidor activo por nombre
    /// (case-insensitive), crea/actualiza el escaneo. Si es null/empty/Logistica
    /// tercerizada, borra los escaneos "cargado" de esa venta.
    /// Llamar despues de SaveChangesAsync al crear o editar venta.
    /// </summary>
    private async Task SincronizarRepartidorDeEntregaAsync(CafeVenta v)
    {
        var nombre = v.EntregaPor?.Trim();
        // Borrar escaneos "cargado" existentes (vamos a reemplazar)
        var existentes = await _db.CafeQrEscaneos
            .Where(e => e.VentaId == v.Id && e.Accion == "cargado")
            .ToListAsync();

        // Buscar repartidor que matchea con EntregaPor (case-insensitive, ignorar "Logistica tercerizada")
        CafeRepartidor? repMatch = null;
        if (!string.IsNullOrWhiteSpace(nombre) && !nombre.Contains("tercerizada", StringComparison.OrdinalIgnoreCase))
        {
            repMatch = await _db.CafeRepartidores
                .FirstOrDefaultAsync(r => r.IsActive && r.Nombre.ToLower() == nombre.ToLower());
        }

        // Si el repartidor actual ya esta en la lista, no tocar nada
        if (repMatch is not null && existentes.Any(e => e.RepartidorId == repMatch.Id))
        {
            // Borrar SOLO los de OTROS repartidores (si hay)
            var otros = existentes.Where(e => e.RepartidorId != repMatch.Id).ToList();
            if (otros.Count > 0) _db.CafeQrEscaneos.RemoveRange(otros);
            if (otros.Count > 0) await _db.SaveChangesAsync();
            return;
        }

        // Borrar todos los escaneos "cargado" actuales
        if (existentes.Count > 0) _db.CafeQrEscaneos.RemoveRange(existentes);

        // Si hay match con repartidor del sistema, crear escaneo nuevo
        if (repMatch is not null)
        {
            _db.CafeQrEscaneos.Add(new CafeQrEscaneo
            {
                VentaId = v.Id,
                RepartidorId = repMatch.Id,
                Accion = "cargado",
                CreatedAt = DateTime.UtcNow,
                Ip = "admin-entrega-por"
            });
        }
        if (existentes.Count > 0 || repMatch is not null) await _db.SaveChangesAsync();
    }
}
