using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class DashboardController : ControllerBase
{
    private readonly AppDbContext _db;

    public DashboardController(AppDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Devuelve la sumatoria de kg de café vendidos en el mes actual desde el módulo
    /// Café (tablas Cafe_Ventas + Cafe_VentaItems). Suma los GramosDescontados de los
    /// items con Categoria='CAFE' de ventas no anuladas del mes en curso y los convierte
    /// a kg. Cuenta también cuántos items y cuántas ventas distintas.
    /// </summary>
    [HttpGet("coffee-monthly-kg")]
    public async Task<IActionResult> GetCoffeeMonthlyKg()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);

        // Items de ventas Café no anuladas, en el mes actual, con categoría "CAFE".
        // GramosDescontados ya viene calculado por línea (formato 1KG/MEDIO/CUARTO * cantidad).
        var rows = await _db.CafeVentaItems
            .Where(i => i.VentaNav != null
                        && i.VentaNav.Estado != "anulado"
                        && i.VentaNav.Fecha >= monthStart
                        && i.VentaNav.Fecha < nextMonthStart
                        && i.Categoria == "CAFE")
            .Select(i => new { i.GramosDescontados, i.VentaId })
            .ToListAsync();

        var gramosTotal = rows.Sum(r => r.GramosDescontados);
        var kgTotal = gramosTotal / 1000m;
        var items = rows.Count;
        var sales = rows.Select(r => r.VentaId).Distinct().Count();

        return Ok(new
        {
            kgTotal = Math.Round(kgTotal, 3),
            items,
            sales,
            periodStart = monthStart,
            periodEnd = nextMonthStart.AddMilliseconds(-1),
            generatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// 2026-06-05: Histórico de kg de café por mes (últimos N meses, default 12).
    /// Para mostrar tooltip al hover sobre la balanza del dashboard.
    /// </summary>
    [HttpGet("coffee-monthly-kg-historico")]
    public async Task<IActionResult> GetCoffeeMonthlyKgHistorico([FromQuery] int meses = 12)
    {
        meses = Math.Clamp(meses, 1, 24);
        var now = DateTime.UtcNow;
        var hasta = new DateTime(now.Year, now.Month, 1).AddMonths(1);
        var desde = hasta.AddMonths(-meses);

        // Agregar por mes
        var rows = await _db.CafeVentaItems
            .Where(i => i.VentaNav != null
                        && i.VentaNav.Estado != "anulado"
                        && i.VentaNav.Fecha >= desde
                        && i.VentaNav.Fecha < hasta
                        && i.Categoria == "CAFE")
            .Select(i => new { i.GramosDescontados, i.VentaNav!.Fecha })
            .ToListAsync();

        var grouped = rows
            .GroupBy(r => new { r.Fecha.Year, r.Fecha.Month })
            .Select(g => new
            {
                year = g.Key.Year,
                month = g.Key.Month,
                kgTotal = Math.Round(g.Sum(x => x.GramosDescontados) / 1000m, 2),
                items = g.Count()
            })
            .OrderByDescending(x => x.year).ThenByDescending(x => x.month)
            .ToList();

        // Rellenar meses sin ventas con 0 (para que aparezcan en el tooltip)
        var resultado = new List<object>();
        for (int i = 0; i < meses; i++)
        {
            var d = hasta.AddMonths(-i - 1);
            var found = grouped.FirstOrDefault(g => g.year == d.Year && g.month == d.Month);
            resultado.Add(new
            {
                year = d.Year,
                month = d.Month,
                periodStart = d,
                kgTotal = found?.kgTotal ?? 0m,
                items = found?.items ?? 0
            });
        }

        return Ok(resultado);
    }

    /// <summary>
    /// Stock total de café disponible (en kg) desde el módulo Café (Cafe_Productos).
    /// Suma StockGramos de todos los productos con Categoria='CAFE' y activos, y lo
    /// convierte a kg. Cuenta variedades totales y cuántas tienen stock > 0.
    /// </summary>
    [HttpGet("coffee-stock-kg")]
    public async Task<IActionResult> GetCoffeeStockKg()
    {
        var rows = await _db.CafeProductos
            .Where(p => p.IsActive && p.Categoria == "CAFE")
            .Select(p => new { p.Id, p.Nombre, p.StockGramos })
            .ToListAsync();

        decimal kgTotal = rows.Sum(r => r.StockGramos) / 1000m;
        int variedades = rows.Count;
        int conStock = rows.Count(r => r.StockGramos > 0m);

        return Ok(new
        {
            kgTotal = Math.Round(kgTotal, 3),
            variedades,
            variedadesConStock = conStock,
            generatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// 2026-07-08: Espacio en disco del servidor para el chip del dashboard.
    /// La API a propósito NO ve el disco del host (el container no monta /proc por
    /// seguridad), así que NO lo calcula acá. El robot de limpieza de las 2 AM
    /// (/usr/local/bin/docker-cache-cleanup.sh) mide el disco por fuera y lo guarda en
    /// AppSettings['system.disk.stats'] como JSON ({totalGb,usedGb,freeGb,pct,at}).
    /// Este endpoint solo devuelve ese JSON tal cual (o null si todavía no se registró).
    /// </summary>
    [HttpGet("disk-usage")]
    public async Task<IActionResult> GetDiskUsage()
    {
        var s = await _db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == "system.disk.stats");
        if (s is null || string.IsNullOrWhiteSpace(s.Value))
            return Ok((object?)null);
        return Content(s.Value, "application/json");
    }

    /// <summary>
    /// Resumen financiero del dashboard, vinculado al módulo Café (Cafe_Ventas):
    /// - Ventas del mes en curso (suma de Total + cantidad de comprobantes,
    ///   incluye cotización/proforma/FA/FB/FC, excluye anuladas).
    /// - Saldos pendientes de cobro: ventas no pagadas, no anuladas, con cliente asignado
    ///   (consumidor final sin pagar lo descartamos — generalmente es venta cash que el
    ///   operador no marcó).
    /// </summary>
    [HttpGet("sales-summary")]
    public async Task<IActionResult> GetSalesSummary()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1);
        var nextMonthStart = monthStart.AddMonths(1);

        // Ventas del mes (no anuladas) — todas: cotización, proforma, FA, FB, FC.
        var monthlySales = await _db.CafeVentas
            .Where(s => s.Estado != "anulado"
                        && s.Fecha >= monthStart
                        && s.Fecha < nextMonthStart)
            .GroupBy(_ => 1)
            .Select(g => new { Total = g.Sum(s => s.Total), Count = g.Count() })
            .FirstOrDefaultAsync();

        // Saldos a cobrar: ventas no anuladas, no pagadas, con cliente asignado.
        var clientBalance = await _db.CafeVentas
            .Where(s => s.Estado != "anulado" && !s.IsPaid && s.ClienteId != null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Sum(s => s.Total),
                Count = g.Count(),
                DistinctClients = g.Select(s => s.ClienteId).Distinct().Count()
            })
            .FirstOrDefaultAsync();

        return Ok(new
        {
            monthlySalesTotal = monthlySales?.Total ?? 0m,
            monthlySalesCount = monthlySales?.Count ?? 0,
            clientBalanceTotal = clientBalance?.Total ?? 0m,
            clientBalanceCount = clientBalance?.Count ?? 0,
            clientsWithBalance = clientBalance?.DistinctClients ?? 0,
            periodStart = monthStart,
            periodEnd = nextMonthStart.AddMilliseconds(-1),
            generatedAt = DateTime.UtcNow
        });
    }

    /// <summary>
    /// 2026-07-04: Historial de ventas de los últimos N meses (default 12) para el mini
    /// gráfico de la card "Ventas del mes". Por cada mes devuelve el desglose entre
    /// cotizaciones (X/PRO — sin IVA por definición) y facturas (FA/FB/FC — con y sin IVA).
    ///
    /// Para "sin IVA" de facturas: preferimos ArcaImpNeto (autoritativo, lo que ARCA registró).
    /// Cuando es null (facturas viejas pre-2026-05-15) hacemos fallback a Subtotal - Descuento.
    /// </summary>
    public record MonthlySalesPointDto(
        int Year, int Month, string MonthLabel,
        decimal TotalGeneral, int TotalCount,
        decimal CotizacionesTotal, int CotizacionesCount,
        decimal FacturasConIva, decimal FacturasSinIva, int FacturasCount);

    [HttpGet("monthly-sales-history")]
    public async Task<IActionResult> GetMonthlySalesHistory([FromQuery] int months = 12)
    {
        if (months < 1) months = 1;
        if (months > 24) months = 24;

        var arNow = DateTime.UtcNow.AddHours(-3);
        var startMonth = new DateTime(arNow.Year, arNow.Month, 1).AddMonths(-(months - 1));
        var startMonthUtc = startMonth.AddHours(3); // 00:00 ART → 03:00 UTC (aprox — sirve para filtro)

        // Traemos los campos minimos que necesitamos para calcular todo en memoria.
        var raw = await _db.CafeVentas
            .Where(v => v.Estado != "anulado" && v.Fecha >= startMonth)
            .Select(v => new
            {
                v.Fecha,
                v.TipoComprobante,
                v.Total,
                v.Subtotal,
                v.Descuento,
                v.ArcaImpNeto,
                v.ArcaImpTotal
            })
            .ToListAsync();

        var esFactura = new HashSet<string> { "FA", "FB", "FC" };
        var esCotiz = new HashSet<string> { "X", "PRO" };
        var cultura = new System.Globalization.CultureInfo("es-AR");

        var byMonth = raw
            .GroupBy(v => new { v.Fecha.Year, v.Fecha.Month })
            .ToDictionary(g => (g.Key.Year, g.Key.Month), g => g.ToList());

        var result = new List<MonthlySalesPointDto>();
        for (int i = 0; i < months; i++)
        {
            var m = startMonth.AddMonths(i);
            var key = (m.Year, m.Month);
            byMonth.TryGetValue(key, out var ventas);
            ventas ??= new();

            decimal totalGeneral = 0m;
            int totalCount = 0;
            decimal cotizTotal = 0m;
            int cotizCount = 0;
            decimal facConIva = 0m;
            decimal facSinIva = 0m;
            int facCount = 0;

            foreach (var v in ventas)
            {
                totalGeneral += v.Total;
                totalCount++;
                var tc = (v.TipoComprobante ?? "").ToUpperInvariant();
                if (esCotiz.Contains(tc))
                {
                    cotizTotal += v.Total;
                    cotizCount++;
                }
                else if (esFactura.Contains(tc))
                {
                    // Preferimos ArcaImpTotal (con IVA autoritativo). Fallback: Total.
                    facConIva += v.ArcaImpTotal ?? v.Total;
                    // Preferimos ArcaImpNeto. Fallback: Subtotal - Descuento.
                    facSinIva += v.ArcaImpNeto ?? (v.Subtotal - v.Descuento);
                    facCount++;
                }
            }

            var label = m.ToString("MMM yyyy", cultura);
            result.Add(new MonthlySalesPointDto(
                m.Year, m.Month, label,
                totalGeneral, totalCount,
                cotizTotal, cotizCount,
                facConIva, facSinIva, facCount));
        }

        return Ok(result);
    }

    // ════════════════════════════════════════════════════════════════════════════════
    // 2026-06-25: Dashboard NUEVO — el "Equipo trabajando ahora" y el "Resumen del día"
    // ════════════════════════════════════════════════════════════════════════════════

    public record DashboardEquipoItem(
        int NomEmpleadoId, string Nombre, string? ApodoKiosko, string? ApodoRepartidor,
        string Estado, string? HoraEntrada, string? HoraSalida, string? Trabajado,
        decimal PorRendir, decimal Pagado, decimal LeDebo, bool TieneRepartidor,
        // 2026-06-26: desglose para mandar al admin a la pantalla de aprobación correcta.
        decimal PorRendirVentas = 0, decimal PorRendirAlquiler = 0,
        // 2026-07-04: carga operativa del repartidor (entregas + alquileres del dia).
        int VentasPendientes = 0, int VentasEntregadasHoy = 0,
        int AlqEntregadosHoy = 0, int AlqRetiradosHoy = 0);

    // 2026-06-25: orden fijo pedido por Osmar — repartidores primero (alexis, walter,
    // benjamin, gonzalo, rodrigo), después oficina (osmar, german, gabriel, miguel).
    // Match por la primera palabra del nombre, sin acentos.
    private static readonly string[] _ordenEquipo = new[]
    {
        "alexis", "walter", "benjamin", "gonzalo", "rodrigo",
        "osmar", "german", "gabriel", "miguel"
    };
    private static int OrdenPersonalizado(string nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre)) return int.MaxValue;
        var first = nombre.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        // Normalize → quitar acentos → lowercase
        var sb = new System.Text.StringBuilder();
        foreach (var c in first.Normalize(System.Text.NormalizationForm.FormD))
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        var normalized = sb.ToString().ToLowerInvariant();
        var idx = Array.IndexOf(_ordenEquipo, normalized);
        return idx >= 0 ? idx : int.MaxValue;
    }

    /// <summary>
    /// 2026-06-25: Devuelve el estado actual del equipo cruzando las 3 tablas
    /// (nominas + fichaje + repartidores) por NomEmpleadoId. Para cada empleado activo
    /// devuelve: estado de fichaje del día, hora entrada/salida, tiempo trabajado,
    /// monto pendiente de rendir (si es repartidor), pagado del mes, y le-debo (neto
    /// liquidado − pagado).
    /// </summary>
    [HttpGet("equipo-dia")]
    public async Task<IActionResult> GetEquipoDia()
    {
        var arNow = DateTime.UtcNow.AddHours(-3);
        var hoy = arNow.Date;
        var anio = arNow.Year;
        var mes = arNow.Month;

        var emps = await _db.NomEmpleados.Where(e => e.IsActive).ToListAsync();
        var fichas = await _db.HorasExtrasEmpleados.Where(f => f.NomEmpleadoId != null).ToListAsync();
        var repartidores = await _db.CafeRepartidores.Where(r => r.NomEmpleadoId != null).ToListAsync();
        var fichaIds = fichas.Select(f => f.Id).ToList();
        var regsHoy = await _db.HorasExtrasRegistros.Where(r => r.Fecha == hoy && fichaIds.Contains(r.EmpleadoId)).ToListAsync();
        var repIds = repartidores.Select(r => r.Id).ToList();
        var pendientesRepartidor = await _db.CafeCobranzasPendientes
            .Where(p => p.Estado == "PENDIENTE" && repIds.Contains(p.RepartidorId))
            .GroupBy(p => p.RepartidorId)
            .Select(g => new { RepartidorId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        // 2026-06-26: sumar también lo pendiente de rendir de ALQUILERES (mismo repartidor).
        var pendientesAlqRepartidor = await _db.AlqCobranzasPendientes
            .Where(p => p.Estado == "PENDIENTE" && repIds.Contains(p.RepartidorId))
            .GroupBy(p => p.RepartidorId)
            .Select(g => new { RepartidorId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        // 2026-07-04: contadores de carga por repartidor para el card del dashboard.
        // Ventas pendientes = escaneadas via QR por el repartidor pero aun sin entregar.
        // Ventas/Alquileres entregados-retirados HOY = fecha ART entre 00:00 y 23:59.
        var hoyUtcDesde = hoy.AddHours(3);       // 00:00 ART -> 03:00 UTC
        var hoyUtcHasta = hoyUtcDesde.AddDays(1);

        var ventasPendByRep = new Dictionary<int, int>();
        var ventasEntHoyByRep = new Dictionary<int, int>();
        var alqEntByRep = new Dictionary<int, int>();
        var alqRetByRep = new Dictionary<int, int>();

        if (repIds.Count > 0)
        {
            // Ventas asignadas (QR) sin entregar aun.
            var pendRows = await _db.CafeQrEscaneos
                .Where(e => e.Accion == "cargado" && repIds.Contains(e.RepartidorId))
                .Select(e => new { e.RepartidorId, e.VentaId })
                .Distinct()
                .Join(_db.CafeVentas, x => x.VentaId, v => v.Id,
                    (x, v) => new { x.RepartidorId, v.EntregadoAt, v.Estado })
                .Where(x => x.EntregadoAt == null && x.Estado != "anulado")
                .GroupBy(x => x.RepartidorId)
                .Select(g => new { RepartidorId = g.Key, Cnt = g.Count() })
                .ToListAsync();
            foreach (var r in pendRows) ventasPendByRep[r.RepartidorId] = r.Cnt;

            var ventasHoyRows = await _db.CafeVentas
                .Where(v => v.EntregadoPorRepartidorId != null && repIds.Contains(v.EntregadoPorRepartidorId!.Value)
                    && v.EntregadoAt != null && v.EntregadoAt >= hoyUtcDesde && v.EntregadoAt < hoyUtcHasta)
                .GroupBy(v => v.EntregadoPorRepartidorId!.Value)
                .Select(g => new { RepartidorId = g.Key, Cnt = g.Count() })
                .ToListAsync();
            foreach (var r in ventasHoyRows) ventasEntHoyByRep[r.RepartidorId] = r.Cnt;

            var alqEntRows = await _db.AlqReservas
                .Where(r => r.EntregadoPorRepartidorId != null && repIds.Contains(r.EntregadoPorRepartidorId!.Value)
                    && r.EntregadoAt != null && r.EntregadoAt >= hoyUtcDesde && r.EntregadoAt < hoyUtcHasta)
                .GroupBy(r => r.EntregadoPorRepartidorId!.Value)
                .Select(g => new { RepartidorId = g.Key, Cnt = g.Count() })
                .ToListAsync();
            foreach (var r in alqEntRows) alqEntByRep[r.RepartidorId] = r.Cnt;

            var alqRetRows = await _db.AlqReservas
                .Where(r => r.RetiradoPorRepartidorId != null && repIds.Contains(r.RetiradoPorRepartidorId!.Value)
                    && r.RetiradoAt != null && r.RetiradoAt >= hoyUtcDesde && r.RetiradoAt < hoyUtcHasta)
                .GroupBy(r => r.RetiradoPorRepartidorId!.Value)
                .Select(g => new { RepartidorId = g.Key, Cnt = g.Count() })
                .ToListAsync();
            foreach (var r in alqRetRows) alqRetByRep[r.RepartidorId] = r.Cnt;
        }

        var liqsDelMes = await _db.NomLiquidaciones.Where(l => l.Anio == anio && l.Mes == mes).ToListAsync();
        var liqIds = liqsDelMes.Select(l => l.Id).ToList();
        var pagosDelMes = await _db.NomPagos.Where(p => liqIds.Contains(p.LiquidacionId)).ToListAsync();

        var fichaByEmp = fichas.ToDictionary(f => f.NomEmpleadoId!.Value, f => f);
        var repByEmp = repartidores.GroupBy(r => r.NomEmpleadoId!.Value).ToDictionary(g => g.Key, g => g.First());
        // Ventas y alquiler separados, para mandar al admin a la pantalla de aprobación correcta.
        var pendVentasByRep = pendientesRepartidor.ToDictionary(p => p.RepartidorId, p => p.Total);
        var pendAlqByRep = pendientesAlqRepartidor.ToDictionary(p => p.RepartidorId, p => p.Total);
        var pendByRep = new Dictionary<int, decimal>();
        foreach (var kv in pendVentasByRep) pendByRep[kv.Key] = kv.Value;
        foreach (var a in pendAlqByRep)
            pendByRep[a.Key] = (pendByRep.TryGetValue(a.Key, out var v) ? v : 0m) + a.Value;
        var liqByEmp = liqsDelMes.ToDictionary(l => l.EmpleadoId, l => l);
        var pagosByLiq = pagosDelMes.GroupBy(p => p.LiquidacionId).ToDictionary(g => g.Key, g => g.Sum(x => x.Monto));
        var regByFicha = regsHoy.ToDictionary(r => r.EmpleadoId, r => r);

        var result = emps.Select(e =>
        {
            string estado = "no_ficha";
            string? horaEntrada = null;
            string? horaSalida = null;
            string? trabajado = null;
            string? apodoKiosko = null;
            string? apodoRepartidor = null;

            if (fichaByEmp.TryGetValue(e.Id, out var ficha))
            {
                apodoKiosko = ficha.Nombre;
                if (regByFicha.TryGetValue(ficha.Id, out var reg))
                {
                    if (reg.HoraEntrada.HasValue)
                    {
                        horaEntrada = reg.HoraEntrada.Value.ToString(@"hh\:mm");
                        if (reg.HoraSalida.HasValue)
                        {
                            estado = "salio";
                            horaSalida = reg.HoraSalida.Value.ToString(@"hh\:mm");
                            var t = reg.HoraSalida.Value - reg.HoraEntrada.Value;
                            trabajado = $"{(int)t.TotalHours}h {t.Minutes}m";
                        }
                        else
                        {
                            estado = "trabajando";
                            var ahora = arNow.TimeOfDay;
                            if (ahora > reg.HoraEntrada.Value)
                            {
                                var t = ahora - reg.HoraEntrada.Value;
                                trabajado = $"{(int)t.TotalHours}h {t.Minutes}m";
                            }
                        }
                    }
                    else estado = "sin-fichar";
                }
                else estado = "sin-fichar";
            }

            decimal porRendir = 0m, porRendirVentas = 0m, porRendirAlq = 0m;
            int ventasPend = 0, ventasEntHoy = 0, alqEntHoy = 0, alqRetHoy = 0;
            bool tieneRepartidor = repByEmp.TryGetValue(e.Id, out var rep);
            if (tieneRepartidor && rep != null)
            {
                apodoRepartidor = rep.Nombre;
                if (pendByRep.TryGetValue(rep.Id, out var monto)) porRendir = monto;
                pendVentasByRep.TryGetValue(rep.Id, out porRendirVentas);
                pendAlqByRep.TryGetValue(rep.Id, out porRendirAlq);
                ventasPendByRep.TryGetValue(rep.Id, out ventasPend);
                ventasEntHoyByRep.TryGetValue(rep.Id, out ventasEntHoy);
                alqEntByRep.TryGetValue(rep.Id, out alqEntHoy);
                alqRetByRep.TryGetValue(rep.Id, out alqRetHoy);
            }

            decimal neto = 0m, pagado = 0m;
            if (liqByEmp.TryGetValue(e.Id, out var liq))
            {
                neto = liq.NetoAPagar;
                pagosByLiq.TryGetValue(liq.Id, out pagado);
            }
            decimal leDebo = neto - pagado;
            if (leDebo < 0) leDebo = 0;

            return new DashboardEquipoItem(
                e.Id, e.Nombre, apodoKiosko, apodoRepartidor,
                estado, horaEntrada, horaSalida, trabajado,
                porRendir, pagado, leDebo, tieneRepartidor,
                porRendirVentas, porRendirAlq,
                ventasPend, ventasEntHoy, alqEntHoy, alqRetHoy);
        })
        .OrderBy(x => OrdenPersonalizado(x.Nombre))
        .ThenBy(x => x.Nombre)
        .ToList();

        var resumen = new
        {
            trabajando = result.Count(r => r.Estado == "trabajando"),
            salio = result.Count(r => r.Estado == "salio"),
            sinFichar = result.Count(r => r.Estado == "sin-fichar"),
            noFicha = result.Count(r => r.Estado == "no_ficha")
        };
        return Ok(new { items = result, resumen, fecha = hoy });
    }

    public record DashboardResumenDiaDto(
        int ChequesHoyCantidad, decimal ChequesHoyImporte,
        int ChequesProxima7DiasCantidad, decimal ChequesProxima7DiasImporte,
        int PreguntasMeliPendientes, int PreguntasMeliNoVistas);

    /// <summary>
    /// 2026-06-25: Resumen del día para el dashboard nuevo. Cheques que tenés que cubrir
    /// hoy + próximos 7 días + preguntas MeLi sin responder.
    /// </summary>
    [HttpGet("resumen-dia")]
    public async Task<IActionResult> GetResumenDia()
    {
        var arNow = DateTime.UtcNow.AddHours(-3);
        var hoy = arNow.Date;
        var en7 = hoy.AddDays(7);

        var chequesHoy = await _db.CafeChequesBanco
            .Where(c => c.Tipo == "EMITIDO"
                && (c.Estado == "Aceptado" || c.Estado == "Disponible")
                && c.FechaPago.HasValue && c.FechaPago.Value.Date == hoy)
            .GroupBy(_ => 1)
            .Select(g => new { Cant = g.Count(), Importe = g.Sum(x => x.Importe) })
            .FirstOrDefaultAsync();

        var chequesProxima7 = await _db.CafeChequesBanco
            .Where(c => c.Tipo == "EMITIDO"
                && (c.Estado == "Aceptado" || c.Estado == "Disponible")
                && c.FechaPago.HasValue
                && c.FechaPago.Value.Date > hoy
                && c.FechaPago.Value.Date <= en7)
            .GroupBy(_ => 1)
            .Select(g => new { Cant = g.Count(), Importe = g.Sum(x => x.Importe) })
            .FirstOrDefaultAsync();

        var preguntasMeli = await _db.MeliQuestions.CountAsync(q => q.Status == "UNANSWERED");
        var preguntasMeliNoVistas = await _db.MeliQuestions.CountAsync(q => q.Status == "UNANSWERED" && q.SeenAt == null);

        return Ok(new DashboardResumenDiaDto(
            chequesHoy?.Cant ?? 0, chequesHoy?.Importe ?? 0m,
            chequesProxima7?.Cant ?? 0, chequesProxima7?.Importe ?? 0m,
            preguntasMeli, preguntasMeliNoVistas));
    }

    /// <summary>
    /// 2026-07-17: código de autorización + horario de la colecta de HOY (colectas/devoluciones MeLi).
    /// El robot los baja del correo. Devuelve la fila del día de hoy (hora Argentina); si todavía no
    /// llegó nada, viene todo en null (la UI muestra "esperando…").
    /// </summary>
    [HttpGet("meli-codigo-colecta")]
    public async Task<IActionResult> GetMeliCodigoColecta()
    {
        var hoyArg = DateTime.UtcNow.AddHours(-3).Date;
        var fila = await _db.MeliCodigosColecta.FirstOrDefaultAsync(x => x.FechaCodigo == hoyArg);

        if (fila is null)
            return Ok(new MeliCodigoColectaDto(null, null, false));

        return Ok(new MeliCodigoColectaDto(
            string.IsNullOrWhiteSpace(fila.Codigo) ? null : fila.Codigo,
            fila.HorarioColecta,
            fila.ColectaCancelada));
    }
}

/// <summary>Código + horario de la colecta de hoy. Todo en null/false si todavía no llegó nada.</summary>
public record MeliCodigoColectaDto(string? Codigo, string? Horario, bool Cancelada);
