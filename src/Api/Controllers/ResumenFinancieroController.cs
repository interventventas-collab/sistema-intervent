using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>2026-07-23: prueba manual del resumen financiero de la mañana (Galicia + Shell +
/// cheques por cubrir). El envío diario automático lo hace ResumenFinancieroDiarioService a las
/// 08:00; este GET permite dispararlo A MANO para verlo ya (abrir la URL logueado y listo).</summary>
[ApiController]
[Route("api/cafe/resumen-financiero")]
[Authorize]
public class ResumenFinancieroController : ControllerBase
{
    private readonly ResumenFinancieroNotifier _notifier;

    public ResumenFinancieroController(ResumenFinancieroNotifier notifier)
    {
        _notifier = notifier;
    }

    [HttpGet("probar")]
    public async Task<IActionResult> Probar()
    {
        var (ok, detalle) = await _notifier.EnviarResumenAsync(HttpContext.RequestAborted);
        return Ok(new { ok, detalle });
    }
}
