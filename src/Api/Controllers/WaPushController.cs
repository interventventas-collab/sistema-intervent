using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// 2026-08-18: alta/baja de los teléfonos que quieren recibir el aviso de WhatsApp con la
/// pantalla cerrada. Ver Api/Services/WaPushService.cs para el por qué del push sin texto.
/// </summary>
[ApiController]
[Route("api/wa-push")]
[Authorize]
public class WaPushController : ControllerBase
{
    private readonly WaPushService _push;
    public WaPushController(WaPushService push) => _push = push;

    /// <summary>Clave pública que el navegador necesita para suscribirse (la genera la primera vez).</summary>
    [HttpGet("clave-publica")]
    public async Task<IActionResult> ClavePublica()
        => Ok(new { clave = await _push.ObtenerClavePublicaAsync(), telefonos = await _push.CantidadAsync() });

    public record SuscribirRequest(string Endpoint, string? Nombre);

    /// <summary>El teléfono avisa que quiere recibir los avisos.</summary>
    [HttpPost("suscribir")]
    public async Task<IActionResult> Suscribir([FromBody] SuscribirRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Endpoint))
            return BadRequest(new { error = "Falta el endpoint de la suscripción" });
        await _push.SuscribirAsync(req.Endpoint, req.Nombre);
        return Ok(new { ok = true });
    }

    /// <summary>El teléfono ya no quiere los avisos.</summary>
    [HttpPost("baja")]
    public async Task<IActionResult> Baja([FromBody] SuscribirRequest req)
    {
        await _push.BajaAsync(req.Endpoint ?? "");
        return Ok(new { ok = true });
    }

    /// <summary>Prueba: despierta a todos los teléfonos suscriptos (para probar sin esperar un mensaje).</summary>
    [HttpPost("probar")]
    public async Task<IActionResult> Probar()
    {
        await _push.AvisarAsync();
        return Ok(new { ok = true, telefonos = await _push.CantidadAsync() });
    }
}
