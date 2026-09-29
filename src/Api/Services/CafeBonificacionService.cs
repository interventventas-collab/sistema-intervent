using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-29 — Cuentas del plan de bonificación de un cliente (Núcleo). Las usan la ficha
/// (CafeBonificacionesController) y el aviso por WhatsApp a Gabriel en cada venta
/// (ClienteAvisosWaService). Reglas en <see cref="CafePlanBonificacion"/>.
/// </summary>
public class CafeBonificacionService
{
    private readonly AppDbContext _db;

    public CafeBonificacionService(AppDbContext db) => _db = db;

    public record PlanDto(
        bool Activo,
        int ProductoKgId, string? ProductoKgSku, string ProductoKgNombre,
        decimal KgPorRegalo,
        int? ProductoRegaloId, string? ProductoRegaloSku, string? ProductoRegaloNombre,
        int CantidadRegalo,
        decimal PctMensual,
        string Desde,
        bool AvisarEnCadaVenta,
        List<int> AvisarPersonaIds);

    /// <summary>Clave en Auto_Destinatarios de a quién se le avisa cada venta de este cliente.</summary>
    public static string ClaveAvisoVenta(int clienteId) => $"bonif-venta:{clienteId}";

    public record MesDto(
        int Anio, int Mes, bool Cerrado,
        decimal KgComprados, decimal KgSugerido, decimal KgOtorgado, bool EditadoAMano, string? Nota,
        decimal KgEntregados, int RegalosGanados, int RegalosEntregados);

    public record VentaPendienteDto(int VentaId, string Numero, DateTime Fecha, int Ganados, int Entregados);

    public record ResumenDto(
        bool TienePlan,
        PlanDto? Plan,
        decimal KgMesActual,
        decimal PctMesActual,
        int RegalosGanadosMes,
        int RegalosEntregadosMes,
        int RegalosPendientes,
        decimal KgOtorgadoTotal,
        decimal KgEntregadoTotal,
        decimal KgAFavor,
        List<MesDto> Meses,
        List<VentaPendienteDto> VentasConRegaloPendiente);

    public static readonly ResumenDto SinPlan =
        new(false, null, 0, 0, 0, 0, 0, 0, 0, 0, new(), new());

    /// <summary>Resumen del cliente; SinPlan si no tiene.</summary>
    public async Task<ResumenDto> ResumenAsync(int clienteId, int? excluirVentaId = null)
    {
        var plan = await _db.CafePlanesBonificacion.AsNoTracking().FirstOrDefaultAsync(p => p.ClienteId == clienteId);
        return plan == null ? SinPlan : await CalcularAsync(plan, excluirVentaId);
    }

    /// <summary>Kg de un renglón según formato. Los que no son café valen 0.</summary>
    private static decimal KgDe(string? formato, int cantidad) => formato switch
    {
        CafePricingService.FORMATO_1KG => cantidad,
        CafePricingService.FORMATO_MEDIO => cantidad * 0.5m,
        CafePricingService.FORMATO_CUARTO => cantidad * 0.25m,
        _ => 0m
    };

    private static decimal Sugerido(decimal kg, decimal pct) =>
        kg <= 0 || pct <= 0 ? 0m : Math.Ceiling(kg * pct / 100m);

    public async Task<ResumenDto> CalcularAsync(CafePlanBonificacion plan, int? excluirVentaId)
    {
        var prods = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.Id == plan.ProductoKgId || p.Id == plan.ProductoRegaloId)
            .Select(p => new { p.Id, p.Sku, p.Nombre })
            .ToListAsync();
        var pKg = prods.FirstOrDefault(p => p.Id == plan.ProductoKgId);
        var pReg = prods.FirstOrDefault(p => p.Id == plan.ProductoRegaloId);

        var planDto = new PlanDto(
            plan.Activo,
            plan.ProductoKgId, pKg?.Sku, pKg?.Nombre ?? "(producto borrado)",
            plan.KgPorRegalo,
            plan.ProductoRegaloId, pReg?.Sku, pReg?.Nombre,
            plan.CantidadRegalo,
            plan.PctMensual,
            plan.Desde.ToString("yyyy-MM"),
            plan.AvisarEnCadaVenta,
            await _db.AutoDestinatarios.AsNoTracking()
                .Where(d => d.AutoKey == ClaveAvisoVenta(plan.ClienteId))
                .Select(d => d.PersonaId).ToListAsync());

        var desde = new DateTime(plan.Desde.Year, plan.Desde.Month, 1);
        var hoy = PanoramaService.AhoraAr();
        var mesActual = new DateTime(hoy.Year, hoy.Month, 1);

        // Mismas reglas que Panorama: sin anuladas, sin proformas ya facturadas (se cuentan
        // en la factura) y sin presupuestos (PRO, no son venta). Las NC restan.
        var rows = await (
            from i in _db.CafeVentaItems.AsNoTracking()
            join v in _db.CafeVentas.AsNoTracking() on i.VentaId equals v.Id
            where v.ClienteId == plan.ClienteId
                  && v.Estado != "anulado"
                  && v.FacturadaComoVentaId == null
                  && v.TipoComprobante != "PRO"
                  && v.Fecha >= desde
                  && (excluirVentaId == null || v.Id != excluirVentaId)
                  && (i.ProductoId == plan.ProductoKgId || i.ProductoId == plan.ProductoRegaloId)
            select new
            {
                v.Id, v.Numero, v.Fecha, v.TipoComprobante,
                i.ProductoId, i.Formato, i.Cantidad, i.DescuentoPct
            }).ToListAsync();

        var ventas = rows
            .GroupBy(r => new { r.Id, r.Numero, r.Fecha, r.TipoComprobante })
            .Select(g =>
            {
                var signo = g.Key.TipoComprobante != null && g.Key.TipoComprobante.StartsWith("NC", StringComparison.OrdinalIgnoreCase) ? -1 : 1;
                var kgPagados = g.Where(r => r.ProductoId == plan.ProductoKgId && r.DescuentoPct < 100)
                                 .Sum(r => KgDe(r.Formato, r.Cantidad)) * signo;
                var kgBonif = g.Where(r => r.ProductoId == plan.ProductoKgId && r.DescuentoPct >= 100)
                               .Sum(r => KgDe(r.Formato, r.Cantidad)) * signo;
                var ganados = plan.ProductoRegaloId != null && plan.KgPorRegalo > 0 && kgPagados > 0
                    ? (int)Math.Floor(kgPagados / plan.KgPorRegalo) * plan.CantidadRegalo
                    : 0;
                var entregados = g.Where(r => r.ProductoId == plan.ProductoRegaloId && r.DescuentoPct >= 100)
                                  .Sum(r => r.Cantidad) * signo;
                return new { g.Key.Id, g.Key.Numero, g.Key.Fecha, KgPagados = kgPagados, KgBonif = kgBonif, Ganados = ganados, Entregados = entregados };
            })
            .ToList();

        var editados = await _db.CafeBonificacionesMes.AsNoTracking()
            .Where(m => m.ClienteId == plan.ClienteId)
            .ToListAsync();

        var meses = new List<MesDto>();
        for (var m = desde; m <= mesActual; m = m.AddMonths(1))
        {
            var delMes = ventas.Where(v => v.Fecha.Year == m.Year && v.Fecha.Month == m.Month).ToList();
            var kg = delMes.Sum(v => v.KgPagados);
            var sug = Sugerido(kg, plan.PctMensual);
            var ed = editados.FirstOrDefault(e => e.Anio == m.Year && e.Mes == m.Month);
            meses.Add(new MesDto(
                m.Year, m.Month, m < mesActual,
                kg, sug, ed?.KgOtorgado ?? sug, ed != null, ed?.Nota,
                delMes.Sum(v => v.KgBonif),
                delMes.Sum(v => v.Ganados), delMes.Sum(v => v.Entregados)));
        }

        var actual = meses.Last();
        var ganadosTot = ventas.Sum(v => v.Ganados);
        var entregadosTot = ventas.Sum(v => v.Entregados);
        // El mes en curso todavía no se otorgó: se va acumulando.
        var otorgadoTot = meses.Where(x => x.Cerrado).Sum(x => x.KgOtorgado);
        // Los kg regalados en el PRIMER mes del plan pagan el mes anterior, que quedó afuera del
        // plan (caso real Núcleo: el 01/09 se le dieron los 9 kg del 10% de agosto). Si contaran,
        // el "a favor" arrancaría en negativo.
        var primerMesQuePaga = desde.AddMonths(1);
        var entregadoKgTot = ventas.Where(v => v.Fecha >= primerMesQuePaga).Sum(v => v.KgBonif);

        var pendientes = ventas
            .Where(v => v.Ganados > v.Entregados)
            .OrderByDescending(v => v.Fecha).ThenByDescending(v => v.Id)
            .Take(10)
            .Select(v => new VentaPendienteDto(v.Id, v.Numero, v.Fecha, v.Ganados, v.Entregados))
            .ToList();

        return new ResumenDto(
            true, planDto,
            actual.KgComprados,
            actual.KgComprados <= 0 ? 0 : Math.Round(actual.KgComprados * plan.PctMensual / 100m, 2),
            actual.RegalosGanados, actual.RegalosEntregados,
            Math.Max(0, ganadosTot - entregadosTot),
            otorgadoTot, entregadoKgTot, otorgadoTot - entregadoKgTot,
            meses.OrderByDescending(x => x.Anio).ThenByDescending(x => x.Mes).ToList(),
            ganadosTot > entregadosTot ? pendientes : new());
    }

}
