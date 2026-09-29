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

    public CafeBonificacionesController(AppDbContext db, CafeBonificacionService svc)
    {
        _db = db;
        _svc = svc;
    }

    public record GuardarPlanRequest(
        bool Activo, string SkuKg, decimal KgPorRegalo, string? SkuRegalo, int CantidadRegalo,
        decimal PctMensual, string Desde,
        bool AvisarEnCadaVenta = false, List<int>? AvisarPersonaIds = null);

    public record GuardarMesRequest(decimal? KgOtorgado, string? Nota);

    private readonly CafeBonificacionService _svc;

    // ─────────────────────────────────────────────────────────────────────
    //  GET resumen del cliente (ficha, ojito del chat, Nueva Venta)
    // ─────────────────────────────────────────────────────────────────────

    /// <param name="excluirVentaId">Al editar una venta, la Nueva Venta la cuenta ella misma
    /// en pantalla: se excluye acá para no contarla dos veces.</param>
    [HttpGet("cliente/{clienteId:int}")]
    public async Task<ActionResult<CafeBonificacionService.ResumenDto>> GetResumen(int clienteId, [FromQuery] int? excluirVentaId = null)
    {
        return Ok(await _svc.ResumenAsync(clienteId, excluirVentaId));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  PUT plan (ficha del cliente)
    // ─────────────────────────────────────────────────────────────────────

    [HttpPut("cliente/{clienteId:int}/plan")]
    public async Task<ActionResult<CafeBonificacionService.ResumenDto>> GuardarPlan(int clienteId, [FromBody] GuardarPlanRequest req)
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
        plan.AvisarEnCadaVenta = req.AvisarEnCadaVenta;
        await _db.SaveChangesAsync();

        // A quién se le avisa: se reemplaza la lista entera (tildes de la libretita de personas).
        var clave = CafeBonificacionService.ClaveAvisoVenta(clienteId);
        var viejos = await _db.AutoDestinatarios.Where(d => d.AutoKey == clave).ToListAsync();
        _db.AutoDestinatarios.RemoveRange(viejos);
        foreach (var pid in (req.AvisarPersonaIds ?? new()).Distinct())
            _db.AutoDestinatarios.Add(new AutoDestinatario { AutoKey = clave, PersonaId = pid });
        await _db.SaveChangesAsync();

        return Ok(await _svc.CalcularAsync(plan, null));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  PUT kg de un mes (cambiar a mano lo sugerido)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>KgOtorgado null = volver a lo sugerido (borra el cambio a mano).</summary>
    [HttpPut("cliente/{clienteId:int}/mes/{anio:int}/{mes:int}")]
    public async Task<ActionResult<CafeBonificacionService.ResumenDto>> GuardarMes(int clienteId, int anio, int mes, [FromBody] GuardarMesRequest req)
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

        return Ok(await _svc.CalcularAsync(plan, null));
    }
}
