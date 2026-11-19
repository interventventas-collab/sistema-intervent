using System.Globalization;
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
///  · Café: se excluyen las anuladas. Una cotización convertida en factura se cuenta UNA
///    vez, en la fecha de la COTIZACIÓN (cuando se vendió): la factura que salió de ella no
///    cuenta mientras la cotización siga viva. 2026-10-06: antes contaba la factura, y una
///    cotización de julio facturada en octubre pasaba sus kg a octubre.
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
    /// anterior comparable al lado. Desde inclusive, Hasta exclusive.
    ///
    /// Si viene <paramref name="mes"/> con formato yyyy-MM, se planta en ESE mes calendario
    /// (es lo que usan las flechitas y el clic sobre una barra del gráfico).
    ///
    /// El mes EN CURSO se compara contra el mismo tramo del mes anterior, no contra el mes
    /// entero: si no, todos los días 1 la pantalla avisaba que todo se derrumbó 100%.</summary>
    public static Rango ResolverPeriodo(string? periodo, string? mes = null)
    {
        var hoy = AhoraAr().Date;
        var manana = hoy.AddDays(1);

        // ── Mes anclado: "2026-06" ──
        if (!string.IsNullOrWhiteSpace(mes)
            && DateTime.TryParseExact(mes.Trim() + "-01", "yyyy-MM-dd",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.None, out var ancla))
        {
            var iniA = new DateTime(ancla.Year, ancla.Month, 1);
            var esMesEnCurso = iniA.Year == hoy.Year && iniA.Month == hoy.Month;
            var finA = esMesEnCurso ? manana : iniA.AddMonths(1);
            var antA = iniA.AddMonths(-1);
            // Mes en curso → tramo parejo del mes anterior (del 1 al mismo día).
            var finAnt = esMesEnCurso ? MinFecha(antA.AddDays((finA - iniA).Days), iniA) : iniA;
            return new Rango(iniA, finA, antA, finAnt,
                $"{Mes(iniA.Month)} {iniA.Year}",
                esMesEnCurso
                    ? $"comparado con el mismo tramo de {Mes(antA.Month).ToLowerInvariant()}"
                    : $"comparado con {Mes(antA.Month).ToLowerInvariant()}");
        }

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
                var iniM = new DateTime(hoy.Year, hoy.Month, 1);
                var antM = iniM.AddMonths(-1);
                // Mismo criterio que arriba: tramo contra tramo parejo.
                var finAntM = MinFecha(antM.AddDays((manana - iniM).Days), iniM);
                return new Rango(iniM, manana, antM, finAntM,
                    $"{Mes(iniM.Month)} {iniM.Year}",
                    $"comparado con el mismo tramo de {Mes(antM.Month).ToLowerInvariant()}");
            }
        }
    }

    private static DateTime MinFecha(DateTime a, DateTime b) => a < b ? a : b;

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
        // Primer día del primer mes que tiene algún dato cargado. Antes de eso no hay nada
        // en este sistema (está en Contadora), así que la pantalla no deja ir más atrás.
        DateTime? PrimerMesConDatos,
        DateTime GeneradoAt,
        // 2026-11-19: detalle de las tarjetas de Intereventos y Logística.
        AlquilerDetalleDto? Alquiler = null,
        LogisticaDetalleDto? Logistica = null);

    /// <summary>2026-11-19: un renglón "nombre · cantidad" (tipo de entrega, repartidor, etc.).</summary>
    public record ParDto(string Nombre, int Cantidad);

    /// <summary>2026-11-19: entregas que hizo la empresa con gente propia en el período.
    /// Propias = las que tienen un repartidor nuestro anotado. SinRepartidor = Flex/me1 que MeLi
    /// dio por entregados pero nadie los marcó en el mapa ni en el celu (no se sabe quién fue).
    /// NoNuestras = de referencia: correo/colecta, Full y la logística tercerizada.</summary>
    public record LogisticaDetalleDto(int Propias, List<ParDto> PorTipo, List<RepartidorLogDto> PorRepartidor,
        int SinRepartidor, List<ParDto> NoNuestras, CombustibleDto? Combustible = null);

    /// <summary>2026-11-19: una barra del gráfico de Logística. Clave = nombre normalizado, se usa
    /// para pedir la lista de sus entregas. Activo = false si ya no trabaja (Benjamin).</summary>
    public record RepartidorLogDto(string Clave, string Nombre, bool Activo, int Total, List<ParDto> PorTipo);

    /// <summary>2026-11-19: Shell Flota. Saldo = lo último que leyó el robot. Gasto/Litros/Cargas = cargas
    /// del período. CostoPorEntrega = gasto / entregas propias (null si no hay con qué dividir).</summary>
    public record CombustibleDto(decimal? Saldo, DateTime? SaldoAt, decimal Gasto, decimal Litros, int Cargas,
        decimal? CostoPorEntrega);

    /// <summary>2026-11-19: un renglón de la lista que se abre al tocar un repartidor.</summary>
    public record EntregaLogDto(DateTime FechaAr, string Tipo, string Texto, string Fuente);

    /// <summary>2026-11-19: alquileres del período. Facturado = eventos con entrega dentro del período
    /// (hasta hoy). Reservado = lo que ya está reservado de mañana a fin de mes (solo en el mes en
    /// curso). Cobrado y FaltaCobrar suman los dos grupos.</summary>
    public record AlquilerDetalleDto(decimal Facturado, int EventosFacturados,
        decimal Reservado, int EventosReservados, decimal Cobrado, decimal FaltaCobrar);

    // ════════════════════════════════════════════════════════════════════════
    // Punto de entrada: todo en una sola llamada
    // ════════════════════════════════════════════════════════════════════════

    public async Task<PanoramaDto> GetAsync(string? periodo, int meses = 12,
        string? mes = null, CancellationToken ct = default)
    {
        meses = Math.Clamp(meses, 3, 24);
        var r = ResolverPeriodo(periodo, mes);

        // El costo por publicación de MeLi se arma UNA vez y se reusa en todo el cálculo.
        var costoMeli = await CostoPorPublicacionAsync(ct);

        var (patas, totalFact, totalMargen, totalBaseMargen, totalOps, varTotal, kg, kgVar) =
            await ResumenPatasAsync(r, costoMeli, ct);

        var serie = await SerieAsync(meses, costoMeli, ct);
        var rankings = await RankingsAsync(r, costoMeli, ct);
        var avisos = await AvisosAsync(r, costoMeli, ct);
        var primerMes = await PrimerMesConDatosAsync(ct);
        var lgDetalle = await LogisticaDetalleAsync(r.Desde, r.Hasta, ct);
        var alqDet = await AlquilerDetalleAsync(r.Desde, r.Hasta, FinDeMesSiEnCurso(r), ct);

        return new PanoramaDto(
            (periodo ?? "mes").ToLowerInvariant(), r.Etiqueta, r.Comparacion, r.Desde, r.Hasta,
            patas,
            Math.Round(totalFact, 2),
            Math.Round(totalMargen, 2),
            totalBaseMargen > 0 ? Math.Round(totalMargen / totalBaseMargen * 100m, 1) : 0m,
            "Intervent + Frikaf",
            totalOps, varTotal,
            Math.Round(kg, 2), kgVar,
            serie, rankings, avisos, primerMes,
            DateTime.UtcNow, alqDet, lgDetalle);
    }

    /// <summary>El mes más viejo que tiene algo cargado en este sistema, mirando las tres
    /// fuentes que alimentan la pantalla. Sirve para no dejar navegar a meses que siempre
    /// van a estar vacíos: lo de antes vive en Contadora, no acá.</summary>
    private async Task<DateTime?> PrimerMesConDatosAsync(CancellationToken ct)
    {
        var meli = await _db.MeliOrders.AsNoTracking()
            .Where(o => EstadosVentaMeli.Contains(o.Status))
            .MinAsync(o => (DateTime?)o.DateCreated, ct);

        var cafe = await _db.CafeVentas.AsNoTracking()
            .Where(v => v.Estado != "anulado" && !(v.OrigenVentaId != null && _db.CafeVentas.Any(o => o.Id == v.OrigenVentaId && o.FacturadaComoVentaId == v.Id && o.Estado != "anulado"))
                        && !_db.CafeSaldosMigracion.Any(sm => sm.VentaId == v.Id))
            .MinAsync(v => (DateTime?)v.Fecha, ct);

        var alq = await _db.AlqReservas.AsNoTracking()
            .Where(x => x.Estado != "cancelado")
            .MinAsync(x => (DateTime?)x.FechaEntrega, ct);

        var fechas = new[] { meli, cafe, alq }.Where(f => f.HasValue).Select(f => f!.Value).ToList();
        if (fechas.Count == 0) return null;
        var min = fechas.Min();
        return new DateTime(min.Year, min.Month, 1);
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
            select new { c.MeliItemId, c.Cantidad, p.Sku, p.Costo, c.CafeProductoId, c.MeliVariationId }
        ).ToListAsync(ct);

        // 2026-09-18: publicación con colores → el costo de UN color, no la suma (ver MeliCostoPorColor).
        var colores = (await _db.MeliItems.AsNoTracking()
                .Where(r => r.VariationId != null && r.CafeProductoId != null)
                .Select(r => new { r.MeliItemId, Pid = r.CafeProductoId!.Value })
                .ToListAsync(ct))
            .GroupBy(r => r.MeliItemId)
            .ToDictionary(g => g.Key, g => (IReadOnlyCollection<int>)g.Select(x => x.Pid).Distinct().ToList());
        IReadOnlyCollection<int> sinColores = Array.Empty<int>();

        var costo = comps
            .GroupBy(c => c.MeliItemId)
            .ToDictionary(
                g => g.Key,
                // dedupe por SKU: el mismo criterio del motor de precios
                g => MeliCostoPorColor.ComponentesDeUnaUnidad(g.ToList(), x => x.CafeProductoId, x => x.MeliVariationId,
                        x => x.Costo, colores.GetValueOrDefault(g.Key, sinColores))
                    .GroupBy(x => x.Sku).Select(x => x.First()).Sum(x => x.Costo * x.Cantidad));

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
    /// facturas que salieron de convertir una cotización (esa venta se cuenta una sola vez, en la
    /// cotización).</summary>
    private IQueryable<CafeVenta> VentasCafeBase(DateTime d, DateTime h) =>
        _db.CafeVentas.AsNoTracking()
            .Where(v => v.Estado != "anulado"
                        && v.Fecha >= d && v.Fecha < h
                        && !(v.OrigenVentaId != null && _db.CafeVentas.Any(o => o.Id == v.OrigenVentaId && o.FacturadaComoVentaId == v.Id && o.Estado != "anulado"))
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
        // 2026-11-19: antes contaba solo ventas de café con repartidor y alquileres (daba 22 en un
        // mes con cientos de Flex). Ahora sale del mismo detalle que muestra la tarjeta.
        var det = await LogisticaDetalleAsync(d, h, ct);
        return new CifrasPata(0m, null, det.Propias);
    }

    // ── Logística: quién entregó qué ───────────────────────────────────────
    /// <summary>Una entrega hecha (o no) por la empresa. Clave evita contar dos veces lo mismo
    /// cuando aparece en el mapa Y en la venta/alquiler/envío.</summary>
    private record EventoLog(string Clave, string Tipo, string? Repartidor, DateTime AtUtc, bool Nuestra,
        string Texto = "", string Fuente = "", bool Activo = true);

    private static readonly string[] OrdenTipos = { "Flex", "me1", "Frikaf", "Alquileres", "Visitas", "Otras paradas" };

    private static string TipoDeOrigen(string origin) => origin switch
    {
        "flex" => "Flex",
        "me1" => "me1",
        "venta_cafe" => "Frikaf",
        "alquiler" => "Alquileres",
        "visita" => "Visitas",
        _ => "Otras paradas"
    };

    /// <summary>"zzzzzbenjamin" → "Benjamin": las z de adelante son para mandarlo al fondo de las listas
    /// cuando se da de baja a alguien; acá no tienen que verse.</summary>
    private static string NombreLindo(string n)
    {
        n = System.Text.RegularExpressions.Regex.Replace((n ?? "").Trim(), "^[zZ]{3,}\\s*", "");
        if (n.Length == 0) return "(sin nombre)";
        return CultureInfo.GetCultureInfo("es-AR").TextInfo.ToTitleCase(n.ToLowerInvariant());
    }

    private static bool EsTercerizado(string? nombre) =>
        nombre != null && nombre.Contains("TERCERIZ", StringComparison.OrdinalIgnoreCase);

    private static bool EsPrueba(string? nombre) =>
        nombre != null && nombre.Contains("PRUEBA", StringComparison.OrdinalIgnoreCase);

    private readonly Dictionary<(DateTime, DateTime), List<EventoLog>> _eventosLogCache = new();

    /// <summary>Todas las entregas del rango [d, h) (fechas argentinas; lo guardado está en UTC).
    /// Orden de las fuentes = prioridad al deduplicar (gana la primera):
    ///   1) lo que alguien MARCÓ desde el celu (envío MeLi, venta, alquiler): esa persona lo llevó;
    ///   2) las paradas del mapa con chofer (para los Flex es lo único que hay: MeLi avisa que se
    ///      entregó pero no quién; se toma el chofer al que estaba asignada la parada);
    ///   3) Flex/me1 que MeLi dio por entregados sin parada ni marca → "sin repartidor anotado".
    /// 19/11: antes ganaba el mapa, y un envío asignado a una persona pero marcado por otra
    /// quedaba a nombre del asignado. El repartidor "PRUEBAS" no cuenta nunca.</summary>
    private async Task<List<EventoLog>> EventosLogisticaAsync(DateTime d, DateTime h, CancellationToken ct)
    {
        if (_eventosLogCache.TryGetValue((d, h), out var cache)) return cache;
        var dU = d.AddHours(3);
        var hU = h.AddHours(3);
        var ev = new List<EventoLog>();

        var reps = await _db.CafeRepartidores.AsNoTracking()
            .Select(x => new { x.Id, x.Nombre, x.IsActive }).ToDictionaryAsync(x => x.Id, ct);
        var drivers = await _db.MapeoDrivers.AsNoTracking()
            .Select(x => new { x.Id, x.Nombre, x.CafeRepartidorId }).ToListAsync(ct);
        var driverInfo = drivers.ToDictionary(x => x.Id, x =>
            x.CafeRepartidorId != null && reps.TryGetValue(x.CafeRepartidorId.Value, out var r)
                ? (Nombre: r.Nombre, Activo: r.IsActive) : (Nombre: x.Nombre, Activo: true));
        (string? Nombre, bool Activo) Rep(int? id) =>
            id != null && reps.TryGetValue(id.Value, out var r) ? (r.Nombre, r.IsActive) : (null, true);

        void Marcado(string clave, string tipo, int? repId, DateTime at, string texto, string fuente)
        {
            var (n, act) = Rep(repId);
            if (EsPrueba(n)) return;
            var terc = EsTercerizado(n);
            ev.Add(new EventoLog(clave, terc ? "Logística tercerizada" : tipo, terc ? null : n, at, !terc, texto, fuente, act));
        }

        // 1a) Envíos MeLi marcados "Entregué" desde el celu.
        var shipsRep = await _db.MeliShipments.AsNoTracking()
            .Where(m => m.EntregadoPorRepartidorId != null && m.EntregadoPorRepartidorAt >= dU && m.EntregadoPorRepartidorAt < hU)
            .Select(m => new { m.MeliShipmentId, m.LogisticType, m.EntregadoPorRepartidorId, At = m.EntregadoPorRepartidorAt!.Value,
                               m.ReceiverName, m.AddressLine })
            .ToListAsync(ct);
        foreach (var m in shipsRep)
        {
            var origin = m.LogisticType == "self_service" ? "flex" : "me1";
            Marcado(origin + ":" + m.MeliShipmentId, TipoDeOrigen(origin), m.EntregadoPorRepartidorId, m.At,
                $"{m.ReceiverName} · {m.AddressLine}".Trim(' ', '·'), "marcado desde el celu");
        }

        // 1b) Ventas de café entregadas por un repartidor.
        var ventas = await _db.CafeVentas.AsNoTracking()
            .Where(v => v.Estado != "anulado" && v.EntregadoPorRepartidorId != null && v.EntregadoAt >= dU && v.EntregadoAt < hU)
            .Select(v => new { v.Id, v.EntregadoPorRepartidorId, At = v.EntregadoAt!.Value,
                               Cli = v.ClienteRazonSocialSnapshot ?? v.ClienteNombreSnapshot })
            .ToListAsync(ct);
        foreach (var v in ventas)
            Marcado("venta_cafe:" + v.Id, "Frikaf", v.EntregadoPorRepartidorId, v.At, $"Venta {v.Id} · {v.Cli}", "marcado desde el celu");

        // 1c) Alquileres: la entrega y el retiro son dos viajes.
        var alqE = await _db.AlqReservas.AsNoTracking()
            .Where(x => x.Estado != "cancelado" && x.EntregadoPorRepartidorId != null && x.EntregadoAt >= dU && x.EntregadoAt < hU)
            .Select(x => new { x.Id, x.Numero, x.EntregadoPorRepartidorId, At = x.EntregadoAt!.Value,
                               Cli = x.ClienteNav != null ? x.ClienteNav.Nombre : null })
            .ToListAsync(ct);
        foreach (var x in alqE)
            Marcado("alquiler:" + x.Id, "Alquileres", x.EntregadoPorRepartidorId, x.At, $"{x.Numero} entrega · {x.Cli}", "marcado desde el celu");
        var alqR = await _db.AlqReservas.AsNoTracking()
            .Where(x => x.Estado != "cancelado" && x.RetiradoPorRepartidorId != null && x.RetiradoAt >= dU && x.RetiradoAt < hU)
            .Select(x => new { x.Id, x.Numero, x.RetiradoPorRepartidorId, At = x.RetiradoAt!.Value,
                               Cli = x.ClienteNav != null ? x.ClienteNav.Nombre : null })
            .ToListAsync(ct);
        foreach (var x in alqR)
            Marcado("alquiler_retiro:" + x.Id, "Alquileres", x.RetiradoPorRepartidorId, x.At, $"{x.Numero} retiro · {x.Cli}", "marcado desde el celu");

        // 2) Paradas del mapa con chofer (el día de reparto puede ser anterior a la entrega).
        var stops = await _db.MapeoStops.AsNoTracking()
            .Where(s => s.AssignedDriverId != null && s.FechaReparto >= d.AddDays(-10) && s.FechaReparto < h)
            .ToListAsync(ct);
        if (stops.Count > 0)
        {
            var entregas = await new MapeoEntregasService(_db).EntregasAsync(stops);
            foreach (var s in stops)
            {
                if (!entregas.TryGetValue(s.Id, out var at) || at is null || at < dU || at >= hU) continue;
                var (nombre, activo) = driverInfo.TryGetValue(s.AssignedDriverId!.Value, out var di) ? di : (null, true);
                if (EsPrueba(nombre)) continue;
                var terc = EsTercerizado(nombre);
                var clave = s.OriginRefId != null ? s.Origin + ":" + s.OriginRefId : "stop:" + s.Id;
                var quien = !string.IsNullOrWhiteSpace(s.Alias) ? s.Alias : s.ContactName;
                var fuente = s.Origin is "flex" or "me1" && !string.Equals(s.InternalStatus, "entregado", StringComparison.OrdinalIgnoreCase)
                    ? $"asignado en el mapa del {s.FechaReparto:dd/MM} (MeLi lo dio por entregado)"
                    : $"mapa del {s.FechaReparto:dd/MM}";
                ev.Add(new EventoLog(clave, terc ? "Logística tercerizada" : TipoDeOrigen(s.Origin),
                    terc ? null : nombre, at.Value, !terc, $"{quien} · {s.Direccion}".Trim(' ', '·'), fuente, activo));
            }
        }

        // 3) Flex y me1 que MeLi dio por entregados y nadie anotó (no se sabe quién fue).
        var shipsMeli = await _db.MeliShipments.AsNoTracking()
            .Where(m => m.Status == "delivered" && m.DateDelivered >= dU && m.DateDelivered < hU
                        && (m.LogisticType == "self_service" || m.Mode == "me1" || m.LogisticType == "default" || m.LogisticType == "custom"))
            .Select(m => new { m.MeliShipmentId, m.LogisticType, At = m.DateDelivered!.Value })
            .ToListAsync(ct);
        foreach (var m in shipsMeli)
        {
            var origin = m.LogisticType == "self_service" ? "flex" : "me1";
            ev.Add(new EventoLog(origin + ":" + m.MeliShipmentId, TipoDeOrigen(origin), null, m.At, true));
        }

        // 4) De referencia, lo que NO llevamos nosotros: correo/colecta y Full (por fecha de venta).
        var otras = await _db.MeliOrders.AsNoTracking()
            .Where(o => EstadosVentaMeli.Contains(o.Status) && o.DateCreated >= d && o.DateCreated < h
                        && (o.LogisticType == "fulfillment" || o.LogisticType == "cross_docking"
                            || o.LogisticType == "drop_off" || o.LogisticType == "xd_drop_off"))
            .Select(o => new { o.MeliOrderId, o.PackId, o.LogisticType, o.DateCreated })
            .ToListAsync(ct);
        foreach (var o in otras)
        {
            var tipo = o.LogisticType == "fulfillment" ? "Full" : "Correo / colecta";
            // DateCreated de MeliOrders se compara como hora argentina, igual que el resto del Panorama.
            ev.Add(new EventoLog("venta_meli:" + (o.PackId?.ToString() ?? o.MeliOrderId.ToString()),
                tipo, null, o.DateCreated.AddHours(3), false));
        }

        _eventosLogCache[(d, h)] = ev;
        return ev;
    }

    /// <summary>Las entregas que cuentan en el rango, ya sin repetidas.</summary>
    private static List<EventoLog> Deduplicar(IEnumerable<EventoLog> eventos, DateTime d, DateTime h)
    {
        var dU = d.AddHours(3);
        var hU = h.AddHours(3);
        var vistos = new HashSet<string>();
        var res = new List<EventoLog>();
        foreach (var e in eventos)
        {
            if (e.AtUtc < dU || e.AtUtc >= hU) continue;
            if (!vistos.Add(e.Clave)) continue;
            res.Add(e);
        }
        return res;
    }

    private static int OrdenTipo(string t) => Array.IndexOf(OrdenTipos, t) is var i && i >= 0 ? i : 99;

    private static LogisticaDetalleDto ResumirLogistica(IEnumerable<EventoLog> eventos, DateTime d, DateTime h)
    {
        var lista = Deduplicar(eventos, d, h);
        var propias = lista.Where(e => e.Nuestra && e.Repartidor != null).ToList();
        var sinRep = lista.Count(e => e.Nuestra && e.Repartidor == null);

        var porTipo = propias.GroupBy(e => e.Tipo)
            .Select(g => new ParDto(g.Key, g.Count()))
            .OrderBy(p => OrdenTipo(p.Nombre)).ToList();
        var porRep = propias.GroupBy(e => NombreLindo(e.Repartidor!))
            .Select(g => new RepartidorLogDto(g.Key, g.Key, g.Any(x => x.Activo), g.Count(),
                g.GroupBy(x => x.Tipo).Select(t => new ParDto(t.Key, t.Count())).OrderBy(p => OrdenTipo(p.Nombre)).ToList()))
            .OrderByDescending(p => p.Total).ToList();
        var nn = lista.Where(e => !e.Nuestra).GroupBy(e => e.Tipo)
            .Select(g => new ParDto(g.Key, g.Count()))
            .OrderByDescending(p => p.Cantidad).ToList();
        return new LogisticaDetalleDto(propias.Count, porTipo, porRep, sinRep, nn);
    }

    private async Task<LogisticaDetalleDto> LogisticaDetalleAsync(DateTime d, DateTime h, CancellationToken ct)
    {
        var det = ResumirLogistica(await EventosLogisticaAsync(d, h, ct), d, h);
        return det with { Combustible = await CombustibleAsync(d, h, det.Propias, ct) };
    }

    /// <summary>Shell Flota: saldo de la tarjeta (lo último que leyó el robot) y las cargas del período.</summary>
    private async Task<CombustibleDto?> CombustibleAsync(DateTime d, DateTime h, int entregas, CancellationToken ct)
    {
        var cuenta = await _db.ShellAccounts.AsNoTracking().Where(a => a.IsActive)
            .OrderByDescending(a => a.LastSaldoAt).Select(a => new { a.LastSaldo, a.LastSaldoAt })
            .FirstOrDefaultAsync(ct);
        var cargas = await _db.ShellCargas.AsNoTracking()
            .Where(c => c.FechaHoraAr >= d && c.FechaHoraAr < h)
            .Select(c => new { c.Importe, c.Litros }).ToListAsync(ct);
        if (cuenta is null && cargas.Count == 0) return null;

        // El robot guarda el saldo como lo muestra Shell: "557,272.12" (coma de miles, punto decimal).
        decimal? saldo = null;
        if (!string.IsNullOrWhiteSpace(cuenta?.LastSaldo)
            && decimal.TryParse(cuenta.LastSaldo.Replace("$", "").Replace(",", "").Trim(),
                   NumberStyles.Number, CultureInfo.InvariantCulture, out var sv))
            saldo = sv;
        var gasto = cargas.Sum(c => c.Importe);
        return new CombustibleDto(saldo, cuenta?.LastSaldoAt, Math.Round(gasto, 2),
            Math.Round(cargas.Sum(c => c.Litros), 1), cargas.Count,
            entregas > 0 && gasto > 0 ? Math.Round(gasto / entregas, 0) : null);
    }

    /// <summary>La lista que se abre al tocar un repartidor en Logística (la más nueva primero).</summary>
    public async Task<List<EntregaLogDto>> EntregasDeRepartidorAsync(string? periodo, string? mes, string clave,
        CancellationToken ct)
    {
        var r = ResolverPeriodo(periodo, mes);
        var lista = Deduplicar(await EventosLogisticaAsync(r.Desde, r.Hasta, ct), r.Desde, r.Hasta);
        return lista
            .Where(e => e.Nuestra && e.Repartidor != null && NombreLindo(e.Repartidor) == clave)
            .OrderByDescending(e => e.AtUtc)
            .Select(e => new EntregaLogDto(e.AtUtc.AddHours(-3), e.Tipo, e.Texto, e.Fuente))
            .ToList();
    }

    // ── Intereventos: facturado + reservado ────────────────────────────────
    /// <summary>Si el período termina hoy y es parte del mes en curso, devuelve el 1° del mes que
    /// viene (hasta ahí se mira lo reservado). Si no, null: en un mes cerrado no hay "reservado".</summary>
    private static DateTime? FinDeMesSiEnCurso(Rango r)
    {
        var hoy = AhoraAr().Date;
        if (r.Hasta != hoy.AddDays(1)) return null;
        var fin = new DateTime(hoy.Year, hoy.Month, 1).AddMonths(1);
        return fin > r.Hasta ? fin : null;
    }

    public async Task<AlquilerDetalleDto> AlquilerDetalleAsync(DateTime d, DateTime h, DateTime? reservadoHasta,
        CancellationToken ct)
    {
        var hasta = reservadoHasta ?? h;
        var rows = await _db.AlqReservas.AsNoTracking()
            .Where(x => x.Estado != "cancelado" && x.FechaEntrega >= d && x.FechaEntrega < hasta)
            .Select(x => new { x.FechaEntrega, x.MontoTotal, x.Sena, x.MontoCobrado })
            .ToListAsync(ct);
        var fact = rows.Where(x => x.FechaEntrega < h).ToList();
        var res = rows.Where(x => x.FechaEntrega >= h).ToList();
        var cobrado = rows.Sum(x => x.Sena + x.MontoCobrado);
        var falta = rows.Sum(x => Math.Max(0m, x.MontoTotal - x.Sena - x.MontoCobrado));
        return new AlquilerDetalleDto(
            Math.Round(fact.Sum(x => x.MontoTotal), 2), fact.Count,
            Math.Round(res.Sum(x => x.MontoTotal), 2), res.Count,
            Math.Round(cobrado, 2), Math.Round(falta, 2));
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
                  // 2026-09-29: el café tercerizado (FT) no suma a los kilos, igual que la balanza.
                  && !(i.ProductoNav != null && i.ProductoNav.Tercerizado)
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
            .Where(v => v.Estado != "anulado" && !(v.OrigenVentaId != null && _db.CafeVentas.Any(o => o.Id == v.OrigenVentaId && o.FacturadaComoVentaId == v.Id && o.Estado != "anulado"))
                        && !_db.CafeSaldosMigracion.Any(sm => sm.VentaId == v.Id)
                        && v.Fecha >= iniVentana && v.Fecha < finVentana)
            .Select(v => new { v.Id, v.Fecha, v.Total, v.Margen, v.TipoComprobante, v.EntregaPor })
            .ToListAsync(ct);

        // ── Café (kilos) ──
        var kilos = await (
            from i in _db.CafeVentaItems.AsNoTracking()
            join v in _db.CafeVentas.AsNoTracking() on i.VentaId equals v.Id
            where v.Estado != "anulado" && !(v.OrigenVentaId != null && _db.CafeVentas.Any(o => o.Id == v.OrigenVentaId && o.FacturadaComoVentaId == v.Id && o.Estado != "anulado"))
                  && !_db.CafeSaldosMigracion.Any(sm => sm.VentaId == v.Id)
                  && v.Fecha >= iniVentana && v.Fecha < finVentana
                  && i.Categoria == "CAFE"
                  && !(i.ProductoNav != null && i.ProductoNav.Tercerizado)
            select new { v.Fecha, i.GramosDescontados, v.TipoComprobante }
        ).ToListAsync(ct);

        // ── Alquileres ──
        var alq = await _db.AlqReservas.AsNoTracking()
            .Where(x => x.Estado != "cancelado" && x.FechaEntrega >= iniVentana && x.FechaEntrega < finVentana)
            .Select(x => new { x.FechaEntrega, x.MontoTotal, x.EntregadoAt, x.RetiradoAt })
            .ToListAsync(ct);

        // 2026-11-19: entregas propias de cada mes, con el mismo criterio que la tarjeta Logística.
        var eventosLog = await EventosLogisticaAsync(iniVentana, finVentana, ct);

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
                opsIv, delMes.Count, opsFk, ResumirLogistica(eventosLog, ini, fin).Propias,
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
            .Select(m => new { m.MeliItemId, m.Title, m.Price,
                               PromoPrecio = m.PromoHasta != null && m.PromoHasta <= DateTime.UtcNow ? null : m.PromoPrecio,
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
