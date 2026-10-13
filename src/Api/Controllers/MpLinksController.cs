using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Links de pago de Mercado Pago creados desde el sistema (pantalla /mercadopago/cobrar).
/// Pedido 2026-10-13.
/// </summary>
[ApiController]
[Route("api/mercadopago/links")]
[Authorize]
public class MpLinksController : ControllerBase
{
    private readonly MpLinksService _service;
    public MpLinksController(MpLinksService service) { _service = service; }

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] int dias = 60) => Ok(await _service.ListarAsync(dias));

    [HttpGet("comision")]
    public async Task<IActionResult> Comision() => Ok(await _service.GetComisionInfoAsync());

    public record PctRequest(decimal? Pct);

    [HttpPut("comision")]
    public async Task<IActionResult> GuardarComision([FromBody] PctRequest req)
    {
        var (ok, error) = await _service.GuardarPctManualAsync(req.Pct);
        if (!ok) return BadRequest(new { error });
        return Ok(await _service.GetComisionInfoAsync());
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] MpLinksService.CrearLinkRequest req)
    {
        var (ok, error, link) = await _service.CrearAsync(req, User.Identity?.Name);
        if (!ok) return BadRequest(new { error });
        return Ok(link);
    }

    [HttpPost("revisar")]
    public async Task<IActionResult> Revisar()
    {
        var (ok, error, cambiados) = await _service.RevisarPendientesAsync();
        if (!ok) return BadRequest(new { error });
        return Ok(new { cambiados });
    }
}
