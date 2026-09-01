using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-31 — El motor de la pantalla "Panorama": junta las 4 patas del negocio
/// (Intervent/MercadoLibre, Frikaf/café, Intereventos/alquileres y la logística propia)
/// en un solo resumen, con la serie de 12 meses, los rankings y los avisos.
///
/// Reglas que se respetan para que los números coincidan con el resto del sistema:
///  · Café: se excluyen las anuladas y las proformas YA convertidas en factura
///    (FacturadaComoVentaId != null), si no la misma venta se contaría dos veces.
///    Las notas de crédito (NCA/NCB/NCC) restan.
///  · MercadoLibre: cuentan las órdenes paid/shipped/delivered. El costo sale de la
///    MISMA receta que usa la pantalla de Publicaciones (MeliItemComponentes, dedupe
///    por SKU) y el margen de la MISMA cuenta: (precio − comisión − envío) / 1,21 − costo.
///  · Alquileres: se excluyen las canceladas. NO hay costo cargado en los equipos,
///    así que se informa facturación y ocupación, nunca margen inventado.
///  · Logística: hoy no existe ninguna tabla con lo que se cobra de flete propio.
///    Se cuentan las entregas hechas con gente propia y el dinero queda en null a
///    propósito, con el motivo, hasta que el dueño defina cómo se cuenta.
///
/// Todo se calcula en HORA ARGENTINA (UTC−3), nunca con DateTime.Now del contenedor.
/// </summary>
public class PanoramaService
{
    private readonly AppDbContext _db;
    private readonly CafeSaldosService _saldos;

    private const decimal IVA = 1.21m;
    private static readonly string[] EstadosVentaMeli = { "paid", "shipped", "delivered" };

    public PanoramaService(AppDbContext db, CafeSaldosService saldos)
    {
        _db = db;
        _saldos = saldos;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Tiempo argentino y períodos
    // ════════════════════════════════════════════════════════════════════════

    public static DateTime AhoraAr() => DateTime.UtcNow.AddHours(-3);

    public record Rango(DateTime Desde, DateTime Hasta, DateTime PrevDesde, DateTime PrevHasta,
                        string Etiqueta, string Comparacion);

    /// <summary>Traduce "hoy | 7d | mes | 90d | anio" a fechas argentinas, con el período
    /// anterior comparable al lado. Desde inclusive, Hasta exclusive.</summary>
    public static Rango ResolverPeriodo(string? periodo)
    {
        var hoy = AhoraAr().Date;
        var manana = hoy.AddDays(1);

        switch ((periodo ?? "mes").ToLowerInvariant())
        {
            case "hoy":
                return new Rango(hoy, manana, hoy.AddDays(-1), hoy,
                    "Hoy", "comparado con ayer");

            case "7d":
                return new Rango(hoy.AddDays(-6), manana, hoy.AddDays(-13), hoy.AddDays(-6),
                    "Últimos 7 días", "comparados con los 7 previos");

            case "90d":
                return new Rango(hoy.AddDays(-89), manana, hoy.AddDays(-179), hoy.AddDays(-89),
                    "Últimos 90 días", "comparados con los 90 previos");

            case "anio":
            {
                var eneEste = new DateTime(hoy.Year, 1, 1);
                var eneAnt = eneEste.AddYears(-1);
                return new Rango(eneEste, manana, eneAnt, manana.AddYears(-1),
                    $"Enero a {Mes(hoy.Month)} {hoy.Year}", $"comparado con {hoy.Year - 1}");
            }

            default:
            {
                var ini = new DateTime(hoy.Year, hoy.Month, 1);
                var fin = ini.AddMonths(1);
                var iniAnt = ini.AddMonths(-1);
                return new Rango(ini, fin, iniAnt, ini,
                    $"{Mes(ini.Month)} {ini.Year}", $"comparado con {Mes(iniAnt.Month).ToLowerInvariant()}");
            }
        }
    }

    private static string Mes(int m) => m switch
    {
        1 => "Enero", 2 => "Febrero", 3 => "Marzo", 4 => "Abril", 5 => "Mayo", 6 => "Junio",
        7 => "Julio", 8 => "Agosto", 9 => "Septiembre", 10 => "Octubre", 11 => "Noviembre",
        _ => "Diciembre"
    };

    private static string MesCorto(int m) => m switch
    {
        1 => "Ene", 2 => "Feb", 3 => "Mar", 4 => "Abr", 5 => "May", 6 => "Jun",
        7 => "Jul", 8 => "Ago", 9 => "Sep", 10 => "Oct", 11 => "Nov", _ => "Dic"
    };

    // ════════════════════════════════════════════════════════════════════════
    // DTOs
    // ════════════════════════════════════════════════════════════════════════

    public record PataDto(
        string Clave, string Nombre, string Canal,
        decimal Facturado, decimal? Margen, decimal? MargenPct,
        int Operaciones, string UnidadOperacion,
        decimal? VarFacturado, string? SinMargenPorque,
        // MargenCobertura: qué parte de lo facturado (0 a 100) se pudo juzgar con un costo
        // confiable. Null si la pata no tiene margen. Menos de 100 = el resto no se puede saber.
        decimal? MargenCobertura);

    public record PuntoSerieDto(int Anio, int Mes, string Etiqueta,
        decimal Iv, decimal Ie, decimal Fk, decimal Lg,
        decimal MargenIv, decimal MargenFk,
        int OpsIv, int OpsIe, int OpsFk, int OpsLg,
        decimal KgCafe);

    public record FilaRankingDto(string Nombre, string Pata, decimal Valor, decimal? Margen,
        decimal? Var, string? Detalle);

    public record RankingDto(string Clave, string Titulo, string ColumnaNombre, string ColumnaValor,
        string? Nota, List<FilaRankingDto> Filas);

    public record AvisoDto(string Texto, string Detalle, bool EsAlarma, string? Link);

    public record PanoramaDto(
        string Periodo, string Etiqueta, string Comparacion,
        DateTime Desde, DateTime Hasta,
        List<PataDto> Patas,
        decimal TotalFacturado, decimal TotalMargen, decimal TotalMargenPct, string TotalMargenSobre,
        int TotalOperaciones, decimal? VarTotal,
        decimal KgCafe, decimal? KgCafeVar,
        List<PuntoSerieDto> Serie,
        List<RankingDto> Rankings,
        List<AvisoDto> Avisos,
        DateTime GeneradoAt);

    // ════════════════════════════════════════════════════════════════════════
    // Punto de entrada: todo en una sola llamada
    // ════════════════════════════════════════════════════════════════════════

    public async Task<PanoramaDto> GetAsync(string? periodo, int meses = 12, CancellationToken ct = default)
    {
        meses = Math.Clamp(meses, 3, 24);
        var r = ResolverPeriodo(periodo);

        // El costo por publicación de MeLi se arma UNA vez y se reusa en todo el cálculo.
        var costoMeli = await CostoPorPublicacionAsync(ct);

        var (patas, totalFact, totalMargen, totalBaseMargen, totalOps, varTotal, kg, kgVar) =
            await ResumenPatasAsync(r, costoMeli, ct);

        var serie = await SerieAsync(meses, costoMeli, ct);
        var rankings = await RankingsAsync(r, costoMeli, ct);
        var avisos = await AvisosAsync(r, costoMeli, ct);

        return new PanoramaDto(
            (periodo ?? "mes").ToLowerInvariant(), r.Etiqueta, r.Comparacion, r.Desde, r.Hasta,
            patas,
            Math.Round(totalFact, 2),
            Math.Round(totalMargen, 2),
            totalBaseMargen > 0 ? Math.Round(totalMargen / totalBaseMargen * 100m, 1) : 0m,
            "Intervent + Frikaf",
            totalOps, varTotal,
            Math.Round(kg, 2), kgVar,
            serie, rankings, avisos,
            DateTime.UtcNow);
    }

    // ════════════════════════════════════════════════════════════════════════
    // Costo de cada publicación de MeLi — la MISMA receta que usa Publicaciones
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// MeliItemId → costo de armar una unidad. Sale de MeliItemComponentes (la receta),
    /// deduplicando por SKU igual que MeliPublicacionesV2Service, y con el fallback al
    /// vínculo viejo MeliItem.CafeProductoId para las que todavía no tienen receta.
    /// </summary>
    private async Task<Dictionary<string, decimal>> CostoPorPublicacionAsync(CancellationToken ct)
    {
        var comps = await (
            from c in _db.MeliItemComponentes.AsNoTracking()
            join p in _db.CafeProductos.AsNoTracking() on c.CafeProductoId equals p.Id
            select new { c.MeliItemId, c.Cantidad, p.Sku, p.Costo }
        ).ToListAsync(ct);

        var costo = comps
            .GroupBy(c => c.MeliItemId)
            .ToDictionary(
                g => g.Key,
                // dedupe por SKU: el mismo criterio del motor de precios
                g => g.GroupBy(x => x.Sku).Select(x => x.First()).Sum(x => x.Costo * x.Cantidad));

        // Fallback: publicaciones sin receta pero atadas al producto por el modelo viejo.
        var legacy = await (
            from m in _db.MeliItems.AsNoTracking()
            where m.CafeProductoId != null
            join p in _db.CafeProductos.AsNoTracking() on m.CafeProductoId equals p.Id
            select new { m.MeliItemId, p.Costo }
        ).ToListAsync(ct);

        foreach (var l in legacy)
            if (!costo.ContainsKey(l.MeliItemId) && l.Costo > 0)
                costo[l.MeliItemId] = l.Costo;

        return costo;
    }

    /// <summary>Cuánto se puede haber movido el precio desde la venta y que el margen siga
    /// siendo creíble. Del costo solo guardamos el de HOY, no el del día de la venta: si el
    /// precio de la publicación cambió mucho desde entonces, el costo también cambió y
    /// compararlos da cualquier cosa. Medido en agosto 2026 sobre ventas de abril: el mismo
    /// armario pasó de $829.550 a $1.161.370 (+40%), y el margen daba −21%, que es mentira.</summary>
    private const decimal DERIVA_MAX = 0.25m;

    /// <summary>Lo que queda de una venta de MeLi después de la comisión, el envío y el IVA,
    /// menos el costo de armarla. Null cuando NO se puede saber, que son dos casos:
    /// la publicación no tiene costo cargado, o el precio se movió tanto desde la venta que
    /// el costo de hoy ya no sirve para juzgarla.</summary>
    private static decimal? MargenOrden(decimal unitPrice, int cantidad, decimal? costoUnitario,
        decimal? feeAmount, decimal? feePriceSnapshot, decimal? feeShipping, decimal? precioPublicacion)
    {
        if (costoUnitario is null or <= 0 || unitPrice <= 0) return null;

        // Precio de hoy muy distinto al de la venta → el costo de hoy no es comparable.
        if (precioPublicacion is > 0
            && Math.Abs(unitPrice - precioPublicacion.Value) / precioPublicacion.Value > DERIVA_MAX)
            return null;

        // La comisión se guarda en pesos para un precio dado. Se pasa a porcentaje y se
        // aplica al precio REAL de la venta, que puede ser otro (promo, cambio de precio).
        var baseFee = feePriceSnapshot is > 0 ? feePriceSnapshot.Value
                    : precioPublicacion is > 0 ? precioPublicacion.Value
                    : 0m;
        var pctComision = (feeAmount is > 0 && baseFee > 0) ? feeAmount.Value / baseFee : 0m;

        var neto = (unitPrice * (1m - pctComision) - (feeShipping ?? 0m)) / IVA;
        return (neto - costoUnitario.Value) * cantidad;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Las 4 patas
    // ════════════════════════════════════════════════════════════════════════

    private record CifrasPata(decimal Facturado, decimal? Margen, int Operaciones,
        decimal FacturadoConMargen = 0m);

    private async Task<(List<PataDto>, decimal, decimal, decimal, int, decimal?, decimal, decimal?)>
        ResumenPatasAsync(Rango r, Dictionary<string, decimal> costoMeli, CancellationToken ct)
    {
        var ivNow = await MeliCifrasAsync(r.Desde, r.Hasta, costoMeli, ct);
        var ivPrev = await MeliCifrasAsync(r.PrevDesde, r.PrevHasta, costoMeli, ct);

        var fkNow = await CafeCifrasAsync(r.Desde, r.Hasta, ct);
        var fkPrev = await CafeCifrasAsync(r.PrevDesde, r.PrevHasta, ct);

        var ieNow = await AlqCifrasAsync(r.Desde, r.Hasta, ct);
        var iePrev = await AlqCifrasAsync(r.PrevDesde, r.PrevHasta, ct);

        var lgNow = await LogisticaCifrasAsync(r.Desde, r.Hasta, ct);
        var lgPrev = await LogisticaCifrasAsync(r.PrevDesde, r.PrevHasta, ct);

        var kgNow = await KgCafeAsync(r.Desde, r.Hasta, ct);
        var kgPrev = await KgCafeAsync(r.PrevDesde, r.PrevHasta, ct);

        var patas = new List<PataDto>
        {
            Pata("iv", "Intervent", "canal MercadoLibre", ivNow, ivPrev, "ventas", null),
            Pata("ie", "Intereventos", "alquiler de equipos", ieNow, iePrev, "eventos",
                 "los equipos no tienen costo cargado"),
            Pata("fk", "Frikaf", "café, venta directa", fkNow, fkPrev, "comprobantes", null),
            Pata("lg", "Logística", "reparto propio", lgNow, lgPrev, "entregas",
                 "falta definir cómo se cuenta el flete propio")
        };

        var totalFact = ivNow.Facturado + ieNow.Facturado + fkNow.Facturado + lgNow.Facturado;
        var totalPrev = ivPrev.Facturado + iePrev.Facturado + fkPrev.Facturado + lgPrev.Facturado;
        // El margen total suma SOLO las patas que tienen costo de verdad.
        var totalMargen = (ivNow.Margen ?? 0m) + (fkNow.Margen ?? 0m);
        var baseMargen = ivNow.FacturadoConMargen + fkNow.FacturadoConMargen;
        var totalOps = ivNow.Operaciones + ieNow.Operaciones + fkNow.Operaciones + lgNow.Operaciones;

        return (patas, totalFact, totalMargen, baseMargen, totalOps,
                Variacion(totalFact, totalPrev), kgNow, Variacion(kgNow, kgPrev));
    }

    private static PataDto Pata(string clave, string nombre, string canal,
        CifrasPata now, CifrasPata prev, string unidad, string? sinMargenPorque) =>
        new(clave, nombre, canal,
            Math.Round(now.Facturado, 2),
            now.Margen is null ? null : Math.Round(now.Margen.Value, 2),
            // El % va sobre lo que SE PUDO juzgar, no sobre todo lo facturado: si no, un
            // margen calculado sobre la mitad de las ventas se leería como si fuera del total.
            now.Margen is null || now.FacturadoConMargen <= 0 ? null
                : Math.Round(now.Margen.Value / now.FacturadoConMargen * 100m, 1),
            now.Operaciones, unidad,
            Variacion(now.Facturado, prev.Facturado),
            sinMargenPorque,
            now.Margen is null || now.Facturado <= 0 ? null
                : Math.Round(now.FacturadoConMargen / now.Facturado * 100m, 0));

    private static decimal? Variacion(decimal ahora, decimal antes)
    {
        if (antes <= 0) return null;               // sin base de comparación: no se inventa un %
        return Math.Round((ahora - antes) / antes * 100m, 1);
    }

    // ── Intervent (MercadoLibre) ───────────────────────────────────────────
    private async Task<CifrasPata> MeliCifrasAsync(DateTime d, DateTime h,
        Dictionary<string, decimal> costoMeli, CancellationToken ct)
    {
        var ordenes = await (
            from o in _db.MeliOrders.AsNoTracking()
            where EstadosVentaMeli.Contains(o.Status) && o.DateCreated >= d && o.DateCreated < h
            join mi in _db.MeliItems.AsNoTracking() on o.ItemId equals mi.MeliItemId into gj
            from mi in gj.DefaultIfEmpty()
            select new
            {
                o.MeliOrderId, o.PackId, o.TotalAmount, o.UnitPrice, o.Quantity, o.ItemId,
                Fee = mi != null ? mi.SaleFeeAmount : null,
                FeeSnap = mi != null ? mi.SaleFeePriceSnapshot : null,
                FeeShip = mi != null ? mi.SaleFeeShippingCost : null,
                Precio = mi != null ? (decimal?)mi.Price : null
            }).ToListAsync(ct);

        decimal fact = 0m, margen = 0m, factConMargen = 0m;
        foreach (var o in ordenes)
        {
            fact += o.TotalAmount;
            costoMeli.TryGetValue(o.ItemId, out var costo);
            var m = MargenOrden(o.UnitPrice, o.Quantity, costo > 0 ? costo : null,
                                o.Fee, o.FeeSnap, o.FeeShip, o.Precio);
            if (m.HasValue) { margen += m.Value; factConMargen += o.TotalAmount; }
        }

        // Un pack (varios productos en la misma compra) es UNA venta, no varias.
        var ops = ordenes.Select(o => o.PackId?.ToString() ?? o.MeliOrderId.ToString()).Distinct().Count();
        return new CifrasPata(fact, factConMargen > 0 ? margen : null, ops, factConMargen);
    }

    // ── Frikaf (café, venta directa) ───────────────────────────────────────
    /// <summary>Ventas de café que cuentan como facturación real: sin anuladas y sin las
    /// proformas que ya se convirtieron en factura (esas se cuentan una sola vez, en la factura).</summary>
    private IQueryable<CafeVenta> VentasCafeBase(DateTime d, DateTime h) =>
        _db.CafeVentas.AsNoTracking()
            .Where(v => v.Estado != "anulado"
                        && v.Fecha >= d && v.Fecha < h
                        && v.FacturadaComoVentaId == null
                        // Los saldos que se migraron del sistema viejo se guardaron como ventas,
                        // pero NO son ventas: es deuda que ya venía de antes. Contarlas inflaba
                        // enero 2026 en $19 M con "margen" del 100% y 0 kg de café.
                        && !_db.CafeSaldosMigracion.Any(sm => sm.VentaId == v.Id));

    private static int SignoComprobante(string? tipo) =>
        tipo != null && tipo.StartsWith("NC", StringComparison.OrdinalIgnoreCase) ? -1 : 1;

    private async Task<CifrasPata> CafeCifrasAsync(DateTime d, DateTime h, CancellationToken ct)
    {
        var rows = await VentasCafeBase(d, h)
            .Select(v => new { v.Total, v.Margen, v.TipoComprobante })
            .ToListAsync(ct);

        decimal fact = 0m, margen = 0m;
        foreach (var v in rows)
        {
            var s = SignoComprobante(v.TipoComprobante);
            fact += v.Total * s;
            margen += v.Margen * s;
        }
        return new CifrasPata(fact, margen, rows.Count, fact);
    }

    // ── Intereventos (alquileres) ──────────────────────────────────────────
    private async Task<CifrasPata> AlqCifrasAsync(DateTime d, DateTime h, CancellationToken ct)
    {
        var rows = await _db.AlqReservas.AsNoTracking()
            .Where(x => x.Estado != "cancelado" && x.FechaEntrega >= d && x.FechaEntrega < h)
            .Select(x => x.MontoTotal)
            .ToListAsync(ct);

        // Margen null a propósito: los equipos no tienen costo cargado.
        return new CifrasPata(rows.Sum(), null, rows.Count);
    }

    // ── Logística propia ───────────────────────────────────────────────────
    /// <summary>Entregas hechas con gente propia: las ventas de café repartidas por un
    /// repartidor y los movimientos de alquiler (entrega y retiro del evento).
    /// La plata queda en 0 a propósito — todavía no existe dónde guardarla.</summary>
    private async Task<CifrasPata> LogisticaCifrasAsync(DateTime d, DateTime h, CancellationToken ct)
    {
        var ventasRepartidas = await VentasCafeBase(d, h)
            .CountAsync(v => v.EntregaPor != null && v.EntregaPor != "", ct);

        var entregasAlq = await _db.AlqReservas.AsNoTracking()
            .CountAsync(x => x.Estado != "cancelado"
                             && x.EntregadoAt != null && x.EntregadoAt >= d && x.EntregadoAt < h, ct);

        var retirosAlq = await _db.AlqReservas.AsNoTracking()
            .CountAsync(x => x.Estado != "cancelado"
                             && x.RetiradoAt != null && x.RetiradoAt >= d && x.RetiradoAt < h, ct);

        return new CifrasPata(0m, null, ventasRepartidas + entregasAlq + retirosAlq);
    }

    // ── Kilos de café ──────────────────────────────────────────────────────
    /// <summary>Kg de café vendidos: los gramos que descontó cada renglón de categoría CAFE.
    /// Misma cuenta que la balanza del dashboard, para que los dos números coincidan.</summary>
    private async Task<decimal> KgCafeAsync(DateTime d, DateTime h, CancellationToken ct)
    {
        var rows = await (
            from i in _db.CafeVentaItems.AsNoTracking()
            join v in VentasCafeBase(d, h) on i.VentaId equals v.Id
            where i.Categoria == "CAFE"
            select new { i.GramosDescontados, v.TipoComprobante }
        ).ToListAsync(ct);

        return rows.Sum(x => x.GramosDescontados * SignoComprobante(x.TipoComprobante)) / 1000m;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Serie de los últimos N meses
    // ════════════════════════════════════════════════════════════════════════

    private async Task<List<PuntoSerieDto>> SerieAsync(int meses,
        Dictionary<string, decimal> costoMeli, CancellationToken ct)
    {
        var hoy = AhoraAr().Date;
        var finVentana = new DateTime(hoy.Year, hoy.Month, 1).AddMonths(1);
        var iniVentana = finVentana.AddMonths(-meses);

        // ── MeLi ──
        var meli = await (
            from o in _db.MeliOrders.AsNoTracking()
            where EstadosVentaMeli.Contains(o.Status)
                  && o.DateCreated >= iniVentana && o.DateCreated < finVentana
            join mi in _db.MeliItems.AsNoTracking() on o.ItemId equals mi.MeliItemId into gj
            from mi in gj.DefaultIfEmpty()
            select new
            {
                o.DateCreated, o.MeliOrderId, o.PackId, o.TotalAmount, o.UnitPrice, o.Quantity, o.ItemId,
                Fee = mi != null ? mi.SaleFeeAmount : null,
                FeeSnap = mi != null ? mi.SaleFeePriceSnapshot : null,
                FeeShip = mi != null ? mi.SaleFeeShippingCost : null,
                Precio = mi != null ? (decimal?)mi.Price : null
            }).ToListAsync(ct);

        // ── Café (cabecera) ──
        var cafe = await _db.CafeVentas.AsNoTracking()
            .Where(v => v.Estado != "anulado" && v.FacturadaComoVentaId == null
                        && !_db.CafeSaldosMigracion.Any(sm => sm.VentaId == v.Id)
                        && v.Fecha >= iniVentana && v.Fecha < finVentana)
            .Select(v => new { v.Id, v.Fecha, v.Total, v.Margen, v.TipoComprobante, v.EntregaPor })
            .ToListAsync(ct);

        // ── Café (kilos) ──
        var kilos = await (
            from i in _db.CafeVentaItems.AsNoTracking()
            join v in _db.CafeVentas.AsNoTracking() on i.VentaId equals v.Id
            where v.Estado != "anulado" && v.FacturadaComoVentaId == null
                  && !_db.CafeSaldosMigracion.Any(sm => sm.VentaId == v.Id)
                  && v.Fecha >= iniVentana && v.Fecha < finVentana
                  && i.Categoria == "CAFE"
            select new { v.Fecha, i.GramosDescontados, v.TipoComprobante }
        ).ToListAsync(ct);

        // ── Alquileres ──
        var alq = await _db.AlqReservas.AsNoTracking()
            .Where(x => x.Estado != "cancelado" && x.FechaEntrega >= iniVentana && x.FechaEntrega < finVentana)
            .Select(x => new { x.FechaEntrega, x.MontoTotal, x.EntregadoAt, x.RetiradoAt })
            .ToListAsync(ct);

        var puntos = new List<PuntoSerieDto>(meses);
        for (int k = 0; k < meses; k++)
        {
            var ini = iniVentana.AddMonths(k);
            var fin = ini.AddMonths(1);

            decimal fIv = 0m, mIv = 0m;
            foreach (var o in meli.Where(x => x.DateCreated >= ini && x.DateCreated < fin))
            {
                fIv += o.TotalAmount;
                costoMeli.TryGetValue(o.ItemId, out var costo);
                var m = MargenOrden(o.UnitPrice, o.Quantity, costo > 0 ? costo : null,
                                    o.Fee, o.FeeSnap, o.FeeShip, o.Precio);
                if (m.HasValue) mIv += m.Value;
            }
            var opsIv = meli.Where(x => x.DateCreated >= ini && x.DateCreated < fin)
                            .Select(x => x.PackId?.ToString() ?? x.MeliOrderId.ToString())
                            .Distinct().Count();

            decimal fFk = 0m, mFk = 0m; int opsFk = 0, repartidas = 0;
            foreach (var v in cafe.Where(x => x.Fecha >= ini && x.Fecha < fin))
            {
                var s = SignoComprobante(v.TipoComprobante);
                fFk += v.Total * s; mFk += v.Margen * s; opsFk++;
                if (!string.IsNullOrWhiteSpace(v.EntregaPor)) repartidas++;
            }

            var kg = kilos.Where(x => x.Fecha >= ini && x.Fecha < fin)
                          .Sum(x => x.GramosDescontados * SignoComprobante(x.TipoComprobante)) / 1000m;

            var delMes = alq.Where(x => x.FechaEntrega >= ini && x.FechaEntrega < fin).ToList();
            var movAlq = alq.Count(x => x.EntregadoAt >= ini && x.EntregadoAt < fin)
                          + alq.Count(x => x.RetiradoAt >= ini && x.RetiradoAt < fin);

            puntos.Add(new PuntoSerieDto(
                ini.Year, ini.Month, $"{MesCorto(ini.Month)}",
                Math.Round(fIv, 2), Math.Round(delMes.Sum(x => x.MontoTotal), 2),
                Math.Round(fFk, 2), 0m,
                Math.Round(mIv, 2), Math.Round(mFk, 2),
                opsIv, delMes.Count, opsFk, repartidas + movAlq,
                Math.Round(kg, 2)));
        }

        return puntos;
    }

    // ════════════════════════════════════════════════════════════════════════
    // Rankings
    // ════════════════════════════════════════════════════════════════════════

    private async Task<List<RankingDto>> RankingsAsync(Rango r,
        Dictionary<string, decimal> costoMeli, CancellationToken ct)
    {
        const int TOP = 15;
        var lista = new List<RankingDto>();

        lista.Add(await RankClientesAsync(r, TOP, ct));
        lista.Add(await RankProductosAsync(r, TOP, ct));
        lista.Add(await RankPublicacionesAsync(r, costoMeli, TOP, ct));
        lista.Add(await RankKgCafeAsync(r, TOP, ct));
        lista.Add(await RankRubrosAsync(r, TOP, ct));
        lista.Add(await RankProvinciasAsync(r, TOP, ct));
        lista.Add(await RankEquiposAsync(r, TOP, ct));
        lista.Add(await RankPagosAsync(r, TOP, ct));
        lista.Add(await RankDeudoresAsync(TOP, ct));
        lista.Add(await RankDormidosAsync(TOP, ct));

        return lista;
    }

    // ── Mejores clientes: las tres bases de clientes juntas, cada una etiquetada ──
    private async Task<RankingDto> RankClientesAsync(Rango r, int top, CancellationToken ct)
    {
        var filas = new List<FilaRankingDto>();

        var cafeNow = await VentasCafeBase(r.Desde, r.Hasta)
            .Select(v => new { v.ClienteId, v.ClienteNombreSnapshot, v.Total, v.Margen, v.TipoComprobante })
            .ToListAsync(ct);
        var cafePrev = await VentasCafeBase(r.PrevDesde, r.PrevHasta)
            .Select(v => new { v.ClienteId, v.Total, v.TipoComprobante })
            .ToListAsync(ct);

        var prevCafe = cafePrev.Where(x => x.ClienteId != null)
            .GroupBy(x => x.ClienteId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Total * SignoComprobante(x.TipoComprobante)));

        filas.AddRange(cafeNow.Where(x => x.ClienteId != null)
            .GroupBy(x => new { Id = x.ClienteId!.Value, Nombre = x.ClienteNombreSnapshot })
            .Select(g => new FilaRankingDto(
                string.IsNullOrWhiteSpace(g.Key.Nombre) ? $"Cliente #{g.Key.Id}" : g.Key.Nombre!,
                "fk",
                g.Sum(x => x.Total * SignoComprobante(x.TipoComprobante)),
                g.Sum(x => x.Margen * SignoComprobante(x.TipoComprobante)),
                Variacion(g.Sum(x => x.Total * SignoComprobante(x.TipoComprobante)),
                          prevCafe.GetValueOrDefault(g.Key.Id)),
                $"{g.Count()} comprobantes")));

        var alqNow = await (
            from x in _db.AlqReservas.AsNoTracking()
            where x.Estado != "cancelado" && x.FechaEntrega >= r.Desde && x.FechaEntrega < r.Hasta
            join c in _db.AlqClientes.AsNoTracking() on x.ClienteId equals c.Id into gj
            from c in gj.DefaultIfEmpty()
            select new { x.ClienteId, Nombre = c != null ? c.Nombre : null, x.MontoTotal }
        ).ToListAsync(ct);

        filas.AddRange(alqNow
            .GroupBy(x => new { x.ClienteId, x.Nombre })
            .Select(g => new FilaRankingDto(
                string.IsNullOrWhiteSpace(g.Key.Nombre) ? $"Cliente #{g.Key.ClienteId}" : g.Key.Nombre!,
                "ie", g.Sum(x => x.MontoTotal), null, null, $"{g.Count()} eventos")));

        var meliNow = await _db.MeliOrders.AsNoTracking()
            .Where(o => EstadosVentaMeli.Contains(o.Status) && o.DateCreated >= r.Desde && o.DateCreated < r.Hasta)
            .Select(o => new { o.BuyerId, o.BuyerNickname, o.TotalAmount, o.MeliOrderId, o.PackId })
            .ToListAsync(ct);

        filas.AddRange(meliNow
            .GroupBy(o => new { o.BuyerId, o.BuyerNickname })
            .Select(g => new FilaRankingDto(
                string.IsNullOrWhiteSpace(g.Key.BuyerNickname) ? $"Comprador {g.Key.BuyerId}" : g.Key.BuyerNickname,
                "iv", g.Sum(x => x.TotalAmount), null, null,
                $"{g.Select(x => x.PackId?.ToString() ?? x.MeliOrderId.ToString()).Distinct().Count()} compras")));

        return new RankingDto("clientes", "Mejores clientes", "Cliente", "Facturado",
            "Junta las tres bases de clientes: café, eventos y compradores de MercadoLibre.",
            Ordenar(filas, top));
    }

    // ── Productos más vendidos (venta directa de café e insumos) ──
    private async Task<RankingDto> RankProductosAsync(Rango r, int top, CancellationToken ct)
    {
        var now = await (
            from i in _db.CafeVentaItems.AsNoTracking()
            join v in VentasCafeBase(r.Desde, r.Hasta) on i.VentaId equals v.Id
            select new { i.ProductoId, i.ProductoNombreSnapshot, i.Subtotal, i.Cantidad,
                         i.PrecioUnitario, i.CostoUnitario, v.TipoComprobante }
        ).ToListAsync(ct);

        var filas = now
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ProductoNombreSnapshot) ? "(sin nombre)" : x.ProductoNombreSnapshot)
            .Select(g =>
            {
                decimal fact = 0m, mar = 0m; int un = 0;
                foreach (var x in g)
                {
                    var s = SignoComprobante(x.TipoComprobante);
                    fact += x.Subtotal * s;
                    mar += (x.PrecioUnitario - x.CostoUnitario) * x.Cantidad * s;
                    un += x.Cantidad * s;
                }
                return new FilaRankingDto(g.Key, "fk", fact, mar, null, $"{un} unidades");
            }).ToList();

        return new RankingDto("productos", "Productos más vendidos", "Producto", "Facturado",
            "Venta directa (Frikaf). Las ventas de MercadoLibre están en la solapa Publicaciones.",
            Ordenar(filas, top));
    }

    // ── Publicaciones de MeLi que más venden / más dejan ──
    private async Task<RankingDto> RankPublicacionesAsync(Rango r,
        Dictionary<string, decimal> costoMeli, int top, CancellationToken ct)
    {
        var now = await (
            from o in _db.MeliOrders.AsNoTracking()
            where EstadosVentaMeli.Contains(o.Status) && o.DateCreated >= r.Desde && o.DateCreated < r.Hasta
            join mi in _db.MeliItems.AsNoTracking() on o.ItemId equals mi.MeliItemId into gj
            from mi in gj.DefaultIfEmpty()
            select new
            {
                o.ItemId, o.ItemTitle, o.TotalAmount, o.UnitPrice, o.Quantity,
                Fee = mi != null ? mi.SaleFeeAmount : null,
                FeeSnap = mi != null ? mi.SaleFeePriceSnapshot : null,
                FeeShip = mi != null ? mi.SaleFeeShippingCost : null,
                Precio = mi != null ? (decimal?)mi.Price : null
            }).ToListAsync(ct);

        var prev = await _db.MeliOrders.AsNoTracking()
            .Where(o => EstadosVentaMeli.Contains(o.Status)
                        && o.DateCreated >= r.PrevDesde && o.DateCreated < r.PrevHasta)
            .GroupBy(o => o.ItemId)
            .Select(g => new { ItemId = g.Key, Total = g.Sum(x => x.TotalAmount) })
            .ToDictionaryAsync(x => x.ItemId, x => x.Total, ct);

        var filas = now
            .GroupBy(x => new { x.ItemId, x.ItemTitle })
            .Select(g =>
            {
                decimal fact = 0m, mar = 0m; int un = 0; bool hayCosto = false;
                foreach (var x in g)
                {
                    fact += x.TotalAmount; un += x.Quantity;
                    costoMeli.TryGetValue(x.ItemId, out var costo);
                    var m = MargenOrden(x.UnitPrice, x.Quantity, costo > 0 ? costo : null,
                                        x.Fee, x.FeeSnap, x.FeeShip, x.Precio);
                    if (m.HasValue) { mar += m.Value; hayCosto = true; }
                }
                return new FilaRankingDto(g.Key.ItemTitle, "iv", fact, hayCosto ? mar : null,
                    Variacion(fact, prev.GetValueOrDefault(g.Key.ItemId)), $"{un} unidades");
            }).ToList();

        return new RankingDto("publicaciones", "Publicaciones de MercadoLibre", "Publicación", "Facturado",
            "El margen descuenta comisión, envío e IVA — la misma cuenta que la pantalla de Publicaciones.",
            Ordenar(filas, top));
    }

    // ── Kilos de café por producto ──
    private async Task<RankingDto> RankKgCafeAsync(Rango r, int top, CancellationToken ct)
    {
        var now = await (
            from i in _db.CafeVentaItems.AsNoTracking()
            join v in VentasCafeBase(r.Desde, r.Hasta) on i.VentaId equals v.Id
            where i.Categoria == "CAFE"
            select new { i.ProductoNombreSnapshot, i.GramosDescontados, i.Subtotal,
                         i.PrecioUnitario, i.CostoUnitario, i.Cantidad, i.Molienda, v.TipoComprobante }
        ).ToListAsync(ct);

        var prev = await (
            from i in _db.CafeVentaItems.AsNoTracking()
            join v in VentasCafeBase(r.PrevDesde, r.PrevHasta) on i.VentaId equals v.Id
            where i.Categoria == "CAFE"
            select new { i.ProductoNombreSnapshot, i.GramosDescontados, v.TipoComprobante }
        ).ToListAsync(ct);

        var prevKg = prev.GroupBy(x => string.IsNullOrWhiteSpace(x.ProductoNombreSnapshot) ? "(sin nombre)" : x.ProductoNombreSnapshot)
            .ToDictionary(g => g.Key,
                          g => g.Sum(x => x.GramosDescontados * SignoComprobante(x.TipoComprobante)) / 1000m);

        var filas = now
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ProductoNombreSnapshot) ? "(sin nombre)" : x.ProductoNombreSnapshot)
            .Select(g =>
            {
                decimal kg = 0m, mar = 0m;
                foreach (var x in g)
                {
                    var s = SignoComprobante(x.TipoComprobante);
                    kg += x.GramosDescontados * s / 1000m;
                    mar += (x.PrecioUnitario - x.CostoUnitario) * x.Cantidad * s;
                }
                var moliendas = g.Where(x => !string.IsNullOrWhiteSpace(x.Molienda))
                                 .Select(x => x.Molienda!).Distinct().Take(3).ToList();
                var detalle = moliendas.Count > 0 ? string.Join(" · ", moliendas) : "grano";
                return new FilaRankingDto(g.Key, "fk", Math.Round(kg, 2), mar,
                    Variacion(kg, prevKg.GetValueOrDefault(g.Key)), detalle);
            }).ToList();

        return new RankingDto("kgcafe", "Kilos de café", "Café", "Kilos",
            "Los gramos que descontó cada venta, pasados a kilos. Misma cuenta que la balanza del inicio.",
            Ordenar(filas, top));
    }

    // ── Rubros ──
    private async Task<RankingDto> RankRubrosAsync(Rango r, int top, CancellationToken ct)
    {
        var cafe = await (
            from i in _db.CafeVentaItems.AsNoTracking()
            join v in VentasCafeBase(r.Desde, r.Hasta) on i.VentaId equals v.Id
            select new { i.Categoria, i.Subtotal, i.PrecioUnitario, i.CostoUnitario, i.Cantidad, v.TipoComprobante }
        ).ToListAsync(ct);

        var filas = cafe
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Categoria) ? "Sin rubro" : x.Categoria!)
            .Select(g =>
            {
                decimal fact = 0m, mar = 0m;
                foreach (var x in g)
                {
                    var s = SignoComprobante(x.TipoComprobante);
                    fact += x.Subtotal * s;
                    mar += (x.PrecioUnitario - x.CostoUnitario) * x.Cantidad * s;
                }
                return new FilaRankingDto(g.Key, "fk", fact, mar, null, null);
            }).ToList();

        // Rubro de MeLi: la primera rama de la categoría de la publicación.
        var meli = await (
            from o in _db.MeliOrders.AsNoTracking()
            where EstadosVentaMeli.Contains(o.Status) && o.DateCreated >= r.Desde && o.DateCreated < r.Hasta
            join mi in _db.MeliItems.AsNoTracking() on o.ItemId equals mi.MeliItemId into gj
            from mi in gj.DefaultIfEmpty()
            select new { o.TotalAmount, Path = mi != null ? mi.CategoryPath : null }
        ).ToListAsync(ct);

        filas.AddRange(meli
            .GroupBy(x => PrimeraRama(x.Path))
            .Select(g => new FilaRankingDto(g.Key, "iv", g.Sum(x => x.TotalAmount), null, null, null)));

        var alq = await _db.AlqReservas.AsNoTracking()
            .Where(x => x.Estado != "cancelado" && x.FechaEntrega >= r.Desde && x.FechaEntrega < r.Hasta)
            .SumAsync(x => (decimal?)x.MontoTotal, ct) ?? 0m;
        if (alq > 0) filas.Add(new FilaRankingDto("Alquiler de equipos", "ie", alq, null, null, null));

        return new RankingDto("rubros", "Rubros", "Rubro", "Facturado",
            "El rubro del café sale de la categoría del producto; el de MercadoLibre, de la categoría de la publicación.",
            Ordenar(filas, top));
    }

    private static string PrimeraRama(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Sin categoría";
        var partes = path.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return partes.Length > 0 ? partes[0] : "Sin categoría";
    }

    // ── Provincias (destino de los envíos de MeLi) ──
    private async Task<RankingDto> RankProvinciasAsync(Rango r, int top, CancellationToken ct)
    {
        var now = await _db.MeliOrders.AsNoTracking()
            .Where(o => EstadosVentaMeli.Contains(o.Status)
                        && o.DateCreated >= r.Desde && o.DateCreated < r.Hasta)
            .Select(o => new { o.ProvinciaDestino, o.TotalAmount, o.MeliOrderId, o.PackId })
            .ToListAsync(ct);

        var prev = await _db.MeliOrders.AsNoTracking()
            .Where(o => EstadosVentaMeli.Contains(o.Status)
                        && o.DateCreated >= r.PrevDesde && o.DateCreated < r.PrevHasta)
            .GroupBy(o => o.ProvinciaDestino)
            .Select(g => new { P = g.Key, Total = g.Sum(x => x.TotalAmount) })
            .ToListAsync(ct);
        var prevMap = prev.ToDictionary(x => x.P ?? "", x => x.Total);

        var filas = now
            .GroupBy(x => string.IsNullOrWhiteSpace(x.ProvinciaDestino) ? "Sin resolver" : x.ProvinciaDestino!)
            .Select(g => new FilaRankingDto(g.Key, "iv", g.Sum(x => x.TotalAmount), null,
                Variacion(g.Sum(x => x.TotalAmount), prevMap.GetValueOrDefault(g.Key == "Sin resolver" ? "" : g.Key)),
                $"{g.Select(x => x.PackId?.ToString() ?? x.MeliOrderId.ToString()).Distinct().Count()} envíos"))
            .ToList();

        return new RankingDto("provincias", "A dónde se manda", "Provincia", "Facturado",
            "Destino de los envíos de MercadoLibre.", Ordenar(filas, top));
    }

    // ── Equipos de evento ──
    private async Task<RankingDto> RankEquiposAsync(Rango r, int top, CancellationToken ct)
    {
        var now = await (
            from i in _db.AlqReservaItems.AsNoTracking()
            join res in _db.AlqReservas.AsNoTracking() on i.ReservaId equals res.Id
            join e in _db.AlqEquipos.AsNoTracking() on i.EquipoId equals e.Id into gj
            from e in gj.DefaultIfEmpty()
            where res.Estado != "cancelado" && res.FechaEntrega >= r.Desde && res.FechaEntrega < r.Hasta
            select new { Nombre = e != null ? e.Nombre : i.Descripcion, i.Cantidad, i.PrecioUnitario, res.Id }
        ).ToListAsync(ct);

        var filas = now
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Nombre) ? "(sin nombre)" : x.Nombre!)
            .Select(g => new FilaRankingDto(g.Key, "ie",
                g.Sum(x => x.Cantidad * x.PrecioUnitario), null, null,
                $"{g.Sum(x => x.Cantidad)} unidades en {g.Select(x => x.Id).Distinct().Count()} eventos"))
            .ToList();

        return new RankingDto("equipos", "Equipos de evento", "Equipo", "Facturado",
            "Los equipos no tienen costo cargado, así que se mide cuánto facturaron y en cuántos eventos salieron.",
            Ordenar(filas, top));
    }

    // ── Formas de pago (lo que realmente entró por caja) ──
    private async Task<RankingDto> RankPagosAsync(Rango r, int top, CancellationToken ct)
    {
        var now = await (
            from m in _db.CafeCobranzasMedios.AsNoTracking()
            join c in _db.CafeCobranzas.AsNoTracking() on m.CobranzaId equals c.Id
            join caja in _db.CafeCajas.AsNoTracking() on m.CajaId equals caja.Id into gj
            from caja in gj.DefaultIfEmpty()
            where c.Estado == "VIGENTE" && c.Fecha >= r.Desde && c.Fecha < r.Hasta
            select new { Nombre = caja != null ? caja.Nombre : "Sin caja", m.Importe }
        ).ToListAsync(ct);

        var filas = now
            .GroupBy(x => x.Nombre ?? "Sin caja")
            .Select(g => new FilaRankingDto(g.Key, "fk", g.Sum(x => x.Importe), null, null,
                $"{g.Count()} movimientos"))
            .ToList();

        return new RankingDto("pagos", "Cómo te pagan", "Forma de pago", "Cobrado",
            "Cobranzas vigentes del período, por caja. Es plata que entró, no facturación.",
            Ordenar(filas, top));
    }

    // ── Quién te debe ──
    private async Task<RankingDto> RankDeudoresAsync(int top, CancellationToken ct)
    {
        var ventas = await _saldos.GetVentasCuentaAsync();
        var hoy = AhoraAr().Date;

        var filas = ventas
            .Where(v => v.Pendiente && v.ClienteId != null)
            .GroupBy(v => new { Id = v.ClienteId!.Value, v.ClienteNombreSnapshot })
            .Select(g =>
            {
                var deuda = g.Sum(x => x.Saldo);
                var masVieja = g.Min(x => x.Fecha);
                var dias = (int)(hoy - masVieja.Date).TotalDays;
                return new FilaRankingDto(
                    string.IsNullOrWhiteSpace(g.Key.ClienteNombreSnapshot) ? $"Cliente #{g.Key.Id}" : g.Key.ClienteNombreSnapshot!,
                    "fk", deuda, null, null,
                    dias > 0 ? $"{g.Count()} comprobantes · el más viejo hace {dias} días"
                             : $"{g.Count()} comprobantes");
            })
            .ToList();

        return new RankingDto("deudores", "Quién te debe", "Cliente", "Deuda",
            "Saldo pendiente de hoy, no del período. Usa la misma cuenta que la pantalla de Saldos.",
            Ordenar(filas, top));
    }

    // ── Plata dormida en el depósito ──
    private record Dormido(string Nombre, decimal Valor, string Detalle);

    /// <summary>Productos con stock que no se movieron por venta directa en 90 días.
    /// Devuelve la lista COMPLETA: el ranking corta el top y el aviso suma todo.</summary>
    private async Task<List<Dormido>> DormidosAsync(CancellationToken ct)
    {
        var hoy = AhoraAr().Date;
        var corte = hoy.AddDays(-90);

        var ultimaVenta = await (
            from i in _db.CafeVentaItems.AsNoTracking()
            join v in _db.CafeVentas.AsNoTracking() on i.VentaId equals v.Id
            where v.Estado != "anulado" && i.ProductoId != null
            group v.Fecha by i.ProductoId!.Value into g
            select new { ProductoId = g.Key, Ultima = g.Max() }
        ).ToDictionaryAsync(x => x.ProductoId, x => x.Ultima, ct);

        var productos = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive && !p.ExcluirDeValuacion && p.Costo > 0 && p.StockUnidades > 0)
            .Select(p => new { p.Id, p.Nombre, p.Costo, p.StockUnidades })
            .ToListAsync(ct);

        return productos
            .Where(p => !ultimaVenta.TryGetValue(p.Id, out var u) || u < corte)
            .Select(p => new Dormido(
                p.Nombre,
                p.Costo * p.StockUnidades,
                ultimaVenta.TryGetValue(p.Id, out var u)
                    ? $"{p.StockUnidades} en stock · última venta hace {(int)(hoy - u.Date).TotalDays} días"
                    : $"{p.StockUnidades} en stock · nunca se vendió por acá"))
            .ToList();
    }

    private async Task<RankingDto> RankDormidosAsync(int top, CancellationToken ct)
    {
        var filas = (await DormidosAsync(ct))
            .Select(d => new FilaRankingDto(d.Nombre, "fk", d.Valor, null, null, d.Detalle))
            .ToList();

        return new RankingDto("dormidos", "Plata dormida", "Producto", "Valor a costo",
            "Stock que no se movió por venta directa en los últimos 90 días, valuado a costo.",
            Ordenar(filas, top));
    }

    /// <summary>Ordena de mayor a menor por el valor, saca los ceros y corta en el top.</summary>
    private static List<FilaRankingDto> Ordenar(List<FilaRankingDto> filas, int top) =>
        filas.Where(f => f.Valor != 0m)
             .OrderByDescending(f => f.Valor)
             .Take(top)
             .Select(f => new FilaRankingDto(f.Nombre, f.Pata, Math.Round(f.Valor, 2),
                 f.Margen is null ? null : Math.Round(f.Margen.Value, 2), f.Var, f.Detalle))
             .ToList();

    // ════════════════════════════════════════════════════════════════════════
    // Qué mirar hoy
    // ════════════════════════════════════════════════════════════════════════

    private async Task<List<AvisoDto>> AvisosAsync(Rango r, Dictionary<string, decimal> costoMeli, CancellationToken ct)
    {
        var avisos = new List<AvisoDto>();
        var hoy = AhoraAr().Date;

        // ── 1) Clientes que se apagan: compraban seguido y se pasaron de su propio ritmo ──
        var desde = hoy.AddYears(-1);
        var historico = await VentasCafeBase(desde, hoy.AddDays(1))
            .Where(v => v.ClienteId != null)
            .Select(v => new { v.ClienteId, v.ClienteNombreSnapshot, v.Fecha, v.Total, v.TipoComprobante })
            .ToListAsync(ct);

        var apagados = historico
            .GroupBy(v => new { Id = v.ClienteId!.Value, v.ClienteNombreSnapshot })
            .Select(g =>
            {
                var fechas = g.Select(x => x.Fecha.Date).Distinct().OrderBy(x => x).ToList();
                if (fechas.Count < 4) return null;                       // muy poca historia para saber su ritmo
                var huecos = fechas.Zip(fechas.Skip(1), (a, b) => (b - a).TotalDays).ToList();
                var ritmo = huecos.Average();
                if (ritmo <= 0) return null;
                var sinComprar = (hoy - fechas.Last()).TotalDays;
                if (sinComprar < ritmo * 2 || sinComprar < 21) return null;   // todavía está en tiempo
                var anual = g.Sum(x => x.Total * SignoComprobante(x.TipoComprobante));
                return new
                {
                    Nombre = string.IsNullOrWhiteSpace(g.Key.ClienteNombreSnapshot)
                        ? $"Cliente #{g.Key.Id}" : g.Key.ClienteNombreSnapshot!,
                    Ritmo = (int)Math.Round(ritmo),
                    Dias = (int)sinComprar,
                    Anual = anual
                };
            })
            .Where(x => x != null)
            .OrderByDescending(x => x!.Anual)
            .Take(3)
            .ToList();

        foreach (var a in apagados)
            avisos.Add(new AvisoDto(
                $"{a!.Nombre} no compra hace {a.Dias} días.",
                $"Compraba cada {a.Ritmo} días en promedio · {Plata(a.Anual)} en el último año.",
                true, "/cafe/clientes"));

        // ── 2) Publicaciones activas que están dando pérdida ──
        var activas = await _db.MeliItems.AsNoTracking()
            .Where(m => m.Status == "active" && m.Price > 0 && m.SaleFeeAmount != null)
            .Select(m => new { m.MeliItemId, m.Title, m.Price, m.PromoPrecio,
                               m.SaleFeeAmount, m.SaleFeePriceSnapshot, m.SaleFeeShippingCost })
            .ToListAsync(ct);

        int enPerdida = 0; string? peor = null; decimal peorPct = 0m;
        foreach (var m in activas)
        {
            if (!costoMeli.TryGetValue(m.MeliItemId, out var costo) || costo <= 0) continue;
            var precio = m.PromoPrecio is > 0 ? m.PromoPrecio.Value : m.Price;
            var g = MargenOrden(precio, 1, costo, m.SaleFeeAmount, m.SaleFeePriceSnapshot,
                                m.SaleFeeShippingCost, m.Price);
            if (g is null) continue;
            var pct = g.Value / costo * 100m;
            if (pct < 0)
            {
                enPerdida++;
                if (peor is null || pct < peorPct) { peor = m.Title; peorPct = pct; }
            }
        }
        if (enPerdida > 0)
            avisos.Add(new AvisoDto(
                $"{enPerdida} publicaciones activas están vendiendo a pérdida.",
                peor is null ? "Revisar precio o costo."
                    : $"La peor es «{Recortar(peor, 60)}», {Nro(peorPct, 0)}% sobre el costo.",
                true, "/publicaciones-nueva"));

        // ── 3) Deuda vencida ──
        var ventasCuenta = await _saldos.GetVentasCuentaAsync();
        var pendientes = ventasCuenta.Where(v => v.Pendiente).ToList();
        var vencida = pendientes.Where(v => (hoy - v.Fecha.Date).TotalDays > 60).ToList();
        if (pendientes.Count > 0)
            avisos.Add(new AvisoDto(
                vencida.Count > 0
                    ? $"{Plata(vencida.Sum(v => v.Saldo))} de deuda pasaron los 60 días."
                    : $"{Plata(pendientes.Sum(v => v.Saldo))} pendientes de cobro.",
                vencida.Count > 0
                    ? $"{vencida.Select(v => v.ClienteId).Distinct().Count()} clientes · sobre {Plata(pendientes.Sum(v => v.Saldo))} de deuda total."
                    : $"{pendientes.Count} comprobantes sin cobrar, ninguno pasó los 60 días.",
                vencida.Count > 0, "/cafe/saldos"));

        // ── 4) Café: kilos del período contra el anterior ──
        var kgNow = await KgCafeAsync(r.Desde, r.Hasta, ct);
        var kgPrev = await KgCafeAsync(r.PrevDesde, r.PrevHasta, ct);
        if (kgNow > 0 || kgPrev > 0)
        {
            var v = Variacion(kgNow, kgPrev);
            avisos.Add(new AvisoDto(
                $"Se vendieron {Nro(kgNow)} kg de café.",
                v is null ? "No hay período anterior con qué compararlo."
                    : $"{(v >= 0 ? "Subió" : "Bajó")} {Nro(Math.Abs(v.Value))}% contra el período anterior ({Nro(kgPrev)} kg).",
                v is < -15m, "/cafe/ventas"));
        }

        // ── 5) Stock dormido — suma TODO, no solo los que entran en el ranking ──
        var dormidos = await DormidosAsync(ct);
        if (dormidos.Count > 0)
            avisos.Add(new AvisoDto(
                $"{Plata(dormidos.Sum(d => d.Valor))} de stock no se movió en 90 días.",
                $"{dormidos.Count} productos distintos, valuados a costo.",
                false, "/stock/valuacion"));

        return avisos;
    }

    private static readonly System.Globalization.CultureInfo Ar =
        System.Globalization.CultureInfo.GetCultureInfo("es-AR");

    /// <summary>Número con coma decimal. El contenedor corre en cultura invariante, así que
    /// sin esto los avisos dirían "2.0 kg" en vez de "2,0 kg".</summary>
    private static string Nro(decimal v, int decimales = 1) =>
        Math.Round(v, decimales).ToString("0.##", Ar);

    private static string Plata(decimal v) =>
        "$" + Math.Round(v, 0).ToString("N0", Ar);

    private static string Recortar(string s, int n) =>
        s.Length <= n ? s : s.Substring(0, n - 1) + "…";
}
