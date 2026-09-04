using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// 2026-09-04 — Clonar publicaciones de MercadoLibre (pantalla /meli/clonar) y duplicar
/// una publicación propia de una cuenta a la otra.
/// </summary>
[ApiController]
[Route("api/meli/clon")]
[Authorize]
public class MeliClonController : ControllerBase
{
    private readonly MeliClonService _svc;

    public MeliClonController(MeliClonService svc) => _svc = svc;

    /// <summary>Lee una publicación (link o código) y devuelve todo lo clonable. No cambia nada.</summary>
    [HttpGet("traer")]
    public async Task<IActionResult> Traer([FromQuery] string referencia)
        => Ok(await _svc.TraerAsync(referencia));

    /// <summary>Publica el clon ya editado en la cuenta elegida.</summary>
    [HttpPost("publicar")]
    public async Task<IActionResult> Publicar([FromBody] ClonPublicarRequest req)
    {
        var res = await _svc.PublicarAsync(req);
        return res.Ok ? Ok(res) : BadRequest(res);
    }

    /// <summary>Un botón: duplica una publicación propia a la otra cuenta.</summary>
    [HttpPost("duplicar")]
    public async Task<IActionResult> Duplicar([FromBody] ClonDuplicarRequest req)
    {
        var res = await _svc.DuplicarACuentaAsync(req);
        return res.Ok ? Ok(res) : BadRequest(res);
    }
}
