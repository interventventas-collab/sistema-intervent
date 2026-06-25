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

    [HttpGet("stats")]
    public async Task<IActionResult> GetStats()
    {
        var totalItems = await _db.MeliItems.CountAsync();
        var totalProducts = await _db.Products.CountAsync();
        var itemsSinProducto = await _db.MeliItems.CountAsync(i => i.ProductId == null);
        var productosSinItems = await _db.Products.CountAsync(p => !_db.MeliItems.Any(i => i.ProductId == p.Id));

        var accountStats = await _db.MeliAccounts
            .GroupJoin(
                _db.MeliItems,
                a => a.Id,
                i => i.MeliAccountId,
                (a, items) => new
                {
                    accountId = a.Id,
                    nickname = a.Nickname,
                    totalItems = items.Count(),
                    itemsConProducto = items.Count(i => i.ProductId != null),
                    itemsSinProducto = items.Count(i => i.ProductId == null),
                    productosVinculados = items.Where(i => i.ProductId != null).Select(i => i.ProductId).Distinct().Count()
                })
            .ToListAsync();

        return Ok(new
        {
            totalItems,
            totalProducts,
            itemsSinProducto,
            productosSinItems,
            accountStats
        });
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

    // ════════════════════════════════════════════════════════════════════════════════
    // 2026-06-25: Dashboard NUEVO — el "Equipo trabajando ahora" y el "Resumen del día"
    // ════════════════════════════════════════════════════════════════════════════════

    public record DashboardEquipoItem(
        int NomEmpleadoId, string Nombre, string? ApodoKiosko, string? ApodoRepartidor,
        string Estado, string? HoraEntrada, string? HoraSalida, string? Trabajado,
        decimal PorRendir, decimal Pagado, decimal LeDebo, bool TieneRepartidor);

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
        var liqsDelMes = await _db.NomLiquidaciones.Where(l => l.Anio == anio && l.Mes == mes).ToListAsync();
        var liqIds = liqsDelMes.Select(l => l.Id).ToList();
        var pagosDelMes = await _db.NomPagos.Where(p => liqIds.Contains(p.LiquidacionId)).ToListAsync();

        var fichaByEmp = fichas.ToDictionary(f => f.NomEmpleadoId!.Value, f => f);
        var repByEmp = repartidores.GroupBy(r => r.NomEmpleadoId!.Value).ToDictionary(g => g.Key, g => g.First());
        var pendByRep = pendientesRepartidor.ToDictionary(p => p.RepartidorId, p => p.Total);
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

            decimal porRendir = 0m;
            bool tieneRepartidor = repByEmp.TryGetValue(e.Id, out var rep);
            if (tieneRepartidor && rep != null)
            {
                apodoRepartidor = rep.Nombre;
                if (pendByRep.TryGetValue(rep.Id, out var monto)) porRendir = monto;
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
                porRendir, pagado, leDebo, tieneRepartidor);
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
}
