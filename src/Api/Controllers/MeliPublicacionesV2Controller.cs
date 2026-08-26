using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// 2026-08-25 — Endpoints de la pantalla NUEVA de publicaciones (/publicaciones-nueva).
/// Controller aparte para no tocar MeliController (3.775 líneas) mientras la pantalla nueva
/// está en prueba. Solo LEE: no modifica nada en MeLi ni en el sistema.
/// </summary>
[ApiController]
[Route("api/meli/v2")]
[Authorize]
public class MeliPublicacionesV2Controller : ControllerBase
{
    private readonly MeliPublicacionesV2Service _svc;

    public MeliPublicacionesV2Controller(MeliPublicacionesV2Service svc) => _svc = svc;

    /// <summary>Lista paginada con receta, stock armable, comisión real y familia.</summary>
    [HttpGet("publicaciones")]
    public async Task<IActionResult> GetPublicaciones(
        [FromQuery] string? texto = null,
        [FromQuery] string? sku = null,
        [FromQuery] string? estado = null,
        [FromQuery] int? cuentaId = null,
        [FromQuery] decimal? comisionMinPct = null,
        [FromQuery] string? cuotas = null,
        [FromQuery] string? tipo = null,
        [FromQuery] bool variosPrecios = false,
        [FromQuery] bool precioAMano = false,
        [FromQuery] bool sinCosto = false,
        [FromQuery] decimal? noLleganAlPct = null,
        [FromQuery] bool comisionVieja = false,
        [FromQuery] int pagina = 1,
        [FromQuery] int porPagina = 100)
    {
        var f = new MeliPublicacionesV2Service.Filtros(
            texto, sku, estado, cuentaId, comisionMinPct, cuotas, tipo,
            variosPrecios, precioAMano, precioAMano, sinCosto, noLleganAlPct, comisionVieja, pagina, porPagina);
        var res = await _svc.GetAsync(f, HttpContext.RequestAborted);
        return Ok(res);
    }

    // ─── 2026-08-26: peso y medidas del paquete ───
    // El envío que MeLi cobra sale de acá. Si están mal cargadas, el envío se dispara y no hay
    // precio que lo arregle. Leer es en vivo contra MeLi; guardar además recalcula el envío
    // para ver al instante si sirvió.

    /// <summary>Peso (g) y medidas (cm) del paquete, leídas en vivo de MeLi.</summary>
    [HttpGet("publicaciones/{mla}/medidas")]
    public async Task<IActionResult> GetMedidas(string mla, [FromServices] MeliMedidasService svc)
    {
        var r = await svc.LeerAsync(mla, HttpContext.RequestAborted);
        if (r is null) return NotFound(new { error = "Publicación no encontrada o sin cuenta MeLi" });
        return Ok(r);
    }

    /// <summary>Guarda peso y/o medidas en MeLi y devuelve cuánto cambió el envío.</summary>
    [HttpPut("publicaciones/{mla}/medidas")]
    public async Task<IActionResult> PutMedidas(string mla,
        [FromBody] MeliMedidasService.GuardarRequest req,
        [FromServices] MeliMedidasService svc)
    {
        var r = await svc.GuardarAsync(mla, req, HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    /// <summary>Contadores para los chips de filtro.</summary>
    [HttpGet("resumen")]
    public async Task<IActionResult> GetResumen()
        => Ok(await _svc.GetResumenAsync(HttpContext.RequestAborted));
}
