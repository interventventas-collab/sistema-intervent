using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>2026-11-23: alertas "entró stock y la publicación MeLi no se activó / se activó con poco %".
/// Las muestra el sistema al centro de la pantalla del admin, de a una, y en la tarjeta MercadoLibre.</summary>
[ApiController]
[Route("api/meli/alertas-pausadas")]
[Authorize(Roles = "admin")]
public class MeliAlertasPausadasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MeliPausadasAlertaService _svc;
    private readonly MeliStockPushService _push;

    public MeliAlertasPausadasController(AppDbContext db, MeliPausadasAlertaService svc, MeliStockPushService push)
    {
        _db = db; _svc = svc; _push = push;
    }

    public record AlertaDto(int Id, string MeliItemId, string Tipo, string Estado, string? Titulo, string? Sku,
        string? Permalink, string? Motivo, decimal? PrecioActual, decimal? MargenPct, decimal? Precio40,
        decimal? Precio50, int Stock, DateTime? PospuestaHasta, DateTime CreatedAt, DateTime UpdatedAt);
    public record PrecioReq(decimal? Precio);
    public record RespuestaDto(bool Ok, string Mensaje);

    private string Quien => (User.Identity?.Name ?? "admin").Trim();

    [HttpGet]
    public async Task<ActionResult<List<AlertaDto>>> Abiertas(CancellationToken ct)
    {
        await _svc.AutoResolverAsync(ct);
        return await _db.MeliAlertasPausadas.AsNoTracking()
            .Where(a => a.Estado == "ABIERTA" || a.Estado == "AUTORIZADA")
            .OrderBy(a => a.Tipo == MeliPausadasAlertaService.NO_ACTIVADA ? 0 : 1).ThenBy(a => a.CreatedAt)
            .Select(a => new AlertaDto(a.Id, a.MeliItemId, a.Tipo, a.Estado, a.Titulo, a.Sku, a.Permalink, a.Motivo,
                a.PrecioActual, a.MargenPct, a.Precio40, a.Precio50, a.Stock, a.PospuestaHasta, a.CreatedAt, a.UpdatedAt))
            .ToListAsync(ct);
    }

    /// <summary>Pausada → la activa. Con precio: primero cambia el precio en MeLi. Sin precio: "Activar así".</summary>
    [HttpPost("{id:int}/activar")]
    public async Task<ActionResult<RespuestaDto>> Activar(int id, [FromBody] PrecioReq req, CancellationToken ct)
    {
        var a = await _db.MeliAlertasPausadas.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        using var http = await _svc.ClienteParaAsync(a.MeliAccountId, ct);
        if (http is null) return Ok(new RespuestaDto(false, "No hay token de MeLi para esa cuenta"));

        if (req.Precio is > 0)
        {
            var err = await _svc.CambiarPrecioAsync(http, a.MeliItemId, req.Precio.Value, ct);
            if (err is not null) return Ok(new RespuestaDto(false, err));
        }
        var accion = req.Precio is > 0 ? $"Activó a {MeliPausadasAlertaService.Plata(req.Precio.Value)}" : "Activó así (sin cambiar precio)";

        if (a.Tipo == MeliPausadasAlertaService.ACTIVADA_POCO)
        {
            // Ya estaba activa: solo se le cambió el precio.
            Cerrar(a, "RESUELTA", req.Precio is > 0 ? $"Subió a {MeliPausadasAlertaService.Plata(req.Precio.Value)}" : "Dejó así");
            await _db.SaveChangesAsync(ct);
            return Ok(new RespuestaDto(true, req.Precio is > 0 ? "Precio cambiado en MeLi" : "Listo"));
        }

        // Autorizada → el push de stock la despierta con la cantidad correcta (también si tiene colores).
        a.Estado = "AUTORIZADA"; a.Accion = accion; a.ResueltaPor = Quien; a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var r = await _push.PushStockForMeliItemsAsync(new List<string> { a.MeliItemId }, ct);
        await _db.Entry(a).ReloadAsync(ct);
        if (a.Estado == "RESUELTA")
            return Ok(new RespuestaDto(true, "Activada en MeLi"));

        // 23/11: puede que ya esté activa aunque el push no la haya despertado: las "gemelas" (mismo
        // producto en MeLi, user_product) se activan juntas al activar una. Se mira en vivo.
        var estado = await _svc.EstadoEnVivoAsync(http, a.MeliItemId, ct);
        if (estado == "active")
        {
            foreach (var row in await _db.MeliItems.Where(x => x.MeliItemId == a.MeliItemId).ToListAsync(ct)) row.Status = "active";
            Cerrar(a, "RESUELTA", accion);
            await _db.SaveChangesAsync(ct);
            return Ok(new RespuestaDto(true, "Activada en MeLi"));
        }
        // No se activó: vuelve a quedar abierta (no queda "autorizada" colgada).
        a.Estado = "ABIERTA"; a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        var detalle = string.Join(" · ", r.Mensajes.Take(2));
        return Ok(new RespuestaDto(false, $"MeLi no la activó (está {estado ?? "?"}): " + (string.IsNullOrWhiteSpace(detalle) ? "sin detalle" : detalle)));
    }

    /// <summary>"Dejar pausada" (no vuelve a avisar hasta que cambie el stock o el costo) o "Dejar así".</summary>
    [HttpPost("{id:int}/dejar")]
    public async Task<ActionResult<RespuestaDto>> Dejar(int id, CancellationToken ct)
    {
        var a = await _db.MeliAlertasPausadas.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        if (a.Tipo == MeliPausadasAlertaService.NO_ACTIVADA) Cerrar(a, "DEJADA", "Dejó pausada");
        else Cerrar(a, "RESUELTA", "Dejó así");
        await _db.SaveChangesAsync(ct);
        return Ok(new RespuestaDto(true, "Listo"));
    }

    /// <summary>"Después": no vuelve a saltar al centro por 1 hora (la tarjeta sigue en rojo).</summary>
    [HttpPost("{id:int}/despues")]
    public async Task<ActionResult<RespuestaDto>> Despues(int id, CancellationToken ct)
    {
        var a = await _db.MeliAlertasPausadas.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (a is null) return NotFound();
        a.PospuestaHasta = DateTime.UtcNow.AddHours(1); a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
        return Ok(new RespuestaDto(true, "Te la vuelvo a mostrar en 1 hora"));
    }

    /// <summary>Diagnóstico (solo lectura, no cambia nada en MeLi): cuánto deja hoy una publicación y a qué
    /// precio dejaría 40% y 50%, con la misma cuenta que usa el robot. Ej: /api/meli/alertas-pausadas/calcular/MLA123</summary>
    [HttpGet("calcular/{meliItemId}")]
    public async Task<ActionResult<object>> Calcular(string meliItemId, [FromServices] MeliPricePushService price, CancellationToken ct)
    {
        var row = await _db.MeliItems.AsNoTracking().FirstOrDefaultAsync(x => x.MeliItemId == meliItemId, ct);
        if (row is null) return NotFound();
        using var http = await _svc.ClienteParaAsync(row.MeliAccountId, ct);
        if (http is null) return Ok(new { error = "sin token" });
        var r = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}", ct);
        if (!r.IsSuccessStatusCode) return Ok(new { error = $"GET {(int)r.StatusCode}" });
        var doc = System.Text.Json.JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement;
        var d = MeliPausadasAlertaService.LeerDatos(meliItemId, doc);
        var costo = await price.CalcularCostoTotalAsync(row, ct);
        decimal? margen = costo is > 0 ? await _svc.MargenEnVivoAsync(http, d, d.PrecioActual, costo.Value, ct) : null;
        decimal? p40 = costo is > 0 ? await _svc.PrecioParaMargenAsync(http, d, costo.Value, 40m, ct) : null;
        decimal? p50 = costo is > 0 ? await _svc.PrecioParaMargenAsync(http, d, costo.Value, 50m, ct) : null;
        return Ok(new { meliItemId, d.PrecioActual, costo, margen, precio40 = p40, precio50 = p50 });
    }

    private void Cerrar(MeliAlertaPausada a, string estado, string accion)
    {
        a.Estado = estado; a.Accion = accion; a.ResueltaPor = Quien; a.ResueltaAt = DateTime.UtcNow; a.UpdatedAt = DateTime.UtcNow;
    }
}
