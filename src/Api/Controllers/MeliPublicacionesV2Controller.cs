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
        [FromQuery] int pagina = 1,
        [FromQuery] int porPagina = 100)
    {
        var f = new MeliPublicacionesV2Service.Filtros(
            texto, sku, estado, cuentaId, comisionMinPct, cuotas, tipo,
            variosPrecios, precioAMano, precioAMano, sinCosto, pagina, porPagina);
        var res = await _svc.GetAsync(f, HttpContext.RequestAborted);
        return Ok(res);
    }

    /// <summary>Contadores para los chips de filtro.</summary>
    [HttpGet("resumen")]
    public async Task<IActionResult> GetResumen()
        => Ok(await _svc.GetResumenAsync(HttpContext.RequestAborted));
}
