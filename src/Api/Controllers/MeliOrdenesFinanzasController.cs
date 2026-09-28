using System.Security.Claims;
using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-09-28: cuánto deja cada venta de MeLi (lo que informa Mercado Pago + el costo del sistema).
/// Es información de plata: solo la ve quien tiene permiso de "ordenes" (el depósito NO).
/// </summary>
[ApiController]
[Route("api/meli/orders/finanzas")]
[Authorize]
public class MeliOrdenesFinanzasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly RoleService _roles;
    private readonly MeliPricePushService _precios;

    public MeliOrdenesFinanzasController(AppDbContext db, RoleService roles, MeliPricePushService precios)
    {
        _db = db; _roles = roles; _precios = precios;
    }

    /// <summary>Montos con IVA, tal cual los informa MP. Costo = costo del sistema (sin IVA) × cantidad;
    /// null si no se puede calcular (producto sin costo o publicación sin producto).</summary>
    public record OrdenFinanzaDto(long MeliOrderId, long? PackId, decimal Vendido,
        decimal? Comision, decimal? Envio, decimal? Retenciones, decimal? Otros, decimal? Neto,
        decimal? Costo, bool Consultado);

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] DateTime from, [FromQuery] DateTime to, CancellationToken ct)
    {
        if (!await PuedeVerAsync()) return Forbid();

        var ordenes = await _db.MeliOrders.AsNoTracking()
            .Where(o => o.DateCreated >= from && o.DateCreated <= to)
            .Select(o => new { o.MeliOrderId, o.PackId, o.TotalAmount, o.ItemId, o.VariationId, o.Quantity,
                o.FinComision, o.FinEnvio, o.FinRetenciones, o.FinOtros, o.FinNeto, o.FinConsultadoAt })
            .ToListAsync(ct);

        // Costo por publicación (+ variante), con la MISMA cuenta que usa Publicaciones.
        var claves = ordenes.Select(o => (o.ItemId, o.VariationId)).Distinct().ToList();
        var itemIds = claves.Select(k => k.ItemId).Distinct().ToList();
        var items = await _db.MeliItems.AsNoTracking().Where(mi => itemIds.Contains(mi.MeliItemId)).ToListAsync(ct);
        var costoUnit = new Dictionary<(string, string?), decimal?>();
        foreach (var k in claves)
        {
            var mi = items.FirstOrDefault(i => i.MeliItemId == k.ItemId && (k.VariationId == null || i.VariationId == k.VariationId))
                     ?? items.FirstOrDefault(i => i.MeliItemId == k.ItemId);
            decimal? c = null;
            if (mi is not null)
            {
                try { c = await _precios.CalcularCostoTotalAsync(mi, ct); } catch { c = null; }
            }
            costoUnit[k] = c is > 0 ? c : null;
        }

        return Ok(ordenes.Select(o =>
        {
            var cu = costoUnit.TryGetValue((o.ItemId, o.VariationId), out var v) ? v : null;
            return new OrdenFinanzaDto(o.MeliOrderId, o.PackId, o.TotalAmount,
                o.FinComision, o.FinEnvio, o.FinRetenciones, o.FinOtros, o.FinNeto,
                cu.HasValue ? Math.Round(cu.Value * o.Quantity, 2) : null,
                o.FinConsultadoAt != null && o.FinNeto != null);
        }).ToList());
    }

    /// <summary>Completa a pedido hasta <paramref name="max"/> ventas pendientes (solo admin). El proceso
    /// automático ya lo hace solo; esto sirve para adelantar o para probar con ventas más viejas.</summary>
    [HttpPost("completar")]
    public async Task<IActionResult> Completar([FromServices] MeliOrderFinanzasService svc,
        [FromQuery] int max = 50, [FromQuery] int dias = MeliOrderFinanzasService.DiasAtras, CancellationToken ct = default)
    {
        if (User.FindFirst(ClaimTypes.Role)?.Value != "admin") return Forbid();
        var n = await svc.CompletarPendientesAsync(Math.Clamp(max, 1, 200), ct, Math.Clamp(dias, 1, 400));
        return Ok(new { completadas = n });
    }

    private async Task<bool> PuedeVerAsync()
    {
        if (User.FindFirst(ClaimTypes.Role)?.Value == "admin") return true;
        if (!int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId)) return false;
        var roleId = await _db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => (int?)u.RoleId).FirstOrDefaultAsync();
        if (roleId is null) return false;
        var permisos = await _roles.GetPermissionsByRoleIdAsync(roleId.Value);
        return permisos.Contains("ordenes");
    }
}
