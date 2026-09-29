using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-09-29 — Plan de bonificación por cliente (Cafe_PlanesBonificacion).
///
/// Pedido de Osmar: *"clientes como Núcleo, cada 5 kg de café F1 le bonificamos una caja de
/// D401 y a fin de cada mes le bonifico en kg de café el 10%"*, con un contador siempre
/// visible de cuántos kg va consumiendo y cuánto lleva acumulado de bonificación.
///
/// Reglas que definió él:
///   - Solo cuentan los kg PAGADOS de F1. Lo bonificado (renglón con 100% de descuento) no
///     suma, ni ese mes ni el siguiente: serían kilos falsos.
///   - El edulcorante se da en la MISMA venta (compra siempre de a 5 kg, no se arrastra resto).
///   - El 10% se redondea para arriba al kilo, y se puede cambiar a mano mes por mes.
///   - El regalo de fin de mes es siempre F1.
///
/// No hay marca especial en los renglones: "bonificado" = DescuentoPct >= 100, que es como
/// ya se viene cargando (así el historial anterior también cuenta).
/// </summary>
[ApiController]
[Route("api/cafe/bonificaciones")]
[Authorize]
public class CafeBonificacionesController : ControllerBase
{
    private readonly AppDbContext _db;

    public CafeBonificacionesController(AppDbContext db) => _db = db;

    public record PlanDto(
        bool Activo,
        int ProductoKgId, string? ProductoKgSku, string ProductoKgNombre,
        decimal KgPorRegalo,
        int? ProductoRegaloId, string? ProductoRegaloSku, string? ProductoRegaloNombre,
        int CantidadRegalo,
        decimal PctMensual,
        string Desde);

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

    public record GuardarPlanRequest(
        bool Activo, string SkuKg, decimal KgPorRegalo, string? SkuRegalo, int CantidadRegalo,
        decimal PctMensual, string Desde);

    public record GuardarMesRequest(decimal? KgOtorgado, string? Nota);

    private static readonly ResumenDto SinPlan =
        new(false, null, 0, 0, 0, 0, 0, 0, 0, 0, new(), new());

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

    // ─────────────────────────────────────────────────────────────────────
    //  GET resumen del cliente (ficha, ojito del chat, Nueva Venta)
    // ─────────────────────────────────────────────────────────────────────

    /// <param name="excluirVentaId">Al editar una venta, la Nueva Venta la cuenta ella misma
    /// en pantalla: se excluye acá para no contarla dos veces.</param>
    [HttpGet("cliente/{clienteId:int}")]
    public async Task<ActionResult<ResumenDto>> GetResumen(int clienteId, [FromQuery] int? excluirVentaId = null)
    {
        var plan = await _db.CafePlanesBonificacion.AsNoTracking().FirstOrDefaultAsync(p => p.ClienteId == clienteId);
        if (plan == null) return Ok(SinPlan);
        return Ok(await CalcularAsync(plan, excluirVentaId));
    }

    private async Task<ResumenDto> CalcularAsync(CafePlanBonificacion plan, int? excluirVentaId)
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
            plan.Desde.ToString("yyyy-MM"));

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
        var entregadoKgTot = ventas.Sum(v => v.KgBonif);

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

    // ─────────────────────────────────────────────────────────────────────
    //  PUT plan (ficha del cliente)
    // ─────────────────────────────────────────────────────────────────────

    [HttpPut("cliente/{clienteId:int}/plan")]
    public async Task<ActionResult<ResumenDto>> GuardarPlan(int clienteId, [FromBody] GuardarPlanRequest req)
    {
        if (!await _db.CafeClientes.AnyAsync(c => c.Id == clienteId))
            return NotFound(new { error = "No existe el cliente." });

        var skuKg = (req.SkuKg ?? "").Trim();
        var pKg = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Sku == skuKg && p.Categoria == "CAFE");
        if (pKg == null)
            return BadRequest(new { error = $"No encontré un café con código \"{skuKg}\"." });

        int? regaloId = null;
        var skuReg = (req.SkuRegalo ?? "").Trim();
        if (skuReg.Length > 0)
        {
            var pReg = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Sku == skuReg);
            if (pReg == null)
                return BadRequest(new { error = $"No encontré un producto con código \"{skuReg}\"." });
            if (pReg.Id == pKg.Id)
                return BadRequest(new { error = "El regalo por venta no puede ser el mismo café que se cuenta." });
            regaloId = pReg.Id;
        }

        if (req.KgPorRegalo <= 0 && regaloId != null)
            return BadRequest(new { error = "Los kg para cada regalo tienen que ser más que cero." });
        if (req.CantidadRegalo < 1 && regaloId != null)
            return BadRequest(new { error = "La cantidad del regalo tiene que ser al menos 1." });
        if (req.PctMensual < 0 || req.PctMensual > 100)
            return BadRequest(new { error = "El porcentaje mensual tiene que estar entre 0 y 100." });
        if (!DateTime.TryParseExact(req.Desde, "yyyy-MM", null, System.Globalization.DateTimeStyles.None, out var desde))
            return BadRequest(new { error = "Falta el mes desde el que corre el plan." });

        var plan = await _db.CafePlanesBonificacion.FirstOrDefaultAsync(p => p.ClienteId == clienteId);
        if (plan == null)
        {
            plan = new CafePlanBonificacion { ClienteId = clienteId };
            _db.CafePlanesBonificacion.Add(plan);
        }
        else plan.UpdatedAt = DateTime.UtcNow;

        plan.Activo = req.Activo;
        plan.ProductoKgId = pKg.Id;
        plan.KgPorRegalo = req.KgPorRegalo;
        plan.ProductoRegaloId = regaloId;
        plan.CantidadRegalo = Math.Max(1, req.CantidadRegalo);
        plan.PctMensual = req.PctMensual;
        plan.Desde = new DateTime(desde.Year, desde.Month, 1);
        plan.UpdatedBy = User?.Identity?.Name;
        await _db.SaveChangesAsync();

        return Ok(await CalcularAsync(plan, null));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  PUT kg de un mes (cambiar a mano lo sugerido)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>KgOtorgado null = volver a lo sugerido (borra el cambio a mano).</summary>
    [HttpPut("cliente/{clienteId:int}/mes/{anio:int}/{mes:int}")]
    public async Task<ActionResult<ResumenDto>> GuardarMes(int clienteId, int anio, int mes, [FromBody] GuardarMesRequest req)
    {
        var plan = await _db.CafePlanesBonificacion.AsNoTracking().FirstOrDefaultAsync(p => p.ClienteId == clienteId);
        if (plan == null) return NotFound(new { error = "El cliente no tiene plan de bonificación." });
        if (mes < 1 || mes > 12) return BadRequest(new { error = "Mes inválido." });
        if (req.KgOtorgado is < 0) return BadRequest(new { error = "Los kg no pueden ser negativos." });

        var fila = await _db.CafeBonificacionesMes.FirstOrDefaultAsync(m => m.ClienteId == clienteId && m.Anio == anio && m.Mes == mes);
        if (req.KgOtorgado == null)
        {
            if (fila != null) _db.CafeBonificacionesMes.Remove(fila);
        }
        else
        {
            if (fila == null)
            {
                fila = new CafeBonificacionMes { ClienteId = clienteId, Anio = anio, Mes = mes };
                _db.CafeBonificacionesMes.Add(fila);
            }
            fila.KgOtorgado = req.KgOtorgado.Value;
            fila.Nota = string.IsNullOrWhiteSpace(req.Nota) ? null : req.Nota.Trim();
            fila.UpdatedAt = DateTime.UtcNow;
            fila.UpdatedBy = User?.Identity?.Name;
        }
        await _db.SaveChangesAsync();

        return Ok(await CalcularAsync(plan, null));
    }
}
