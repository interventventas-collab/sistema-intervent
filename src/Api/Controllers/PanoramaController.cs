using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// 2026-08-31 — La pantalla "Panorama": las 4 patas del negocio en una sola vista.
/// Devuelve TODO en una sola llamada (resumen, serie de 12 meses, rankings y avisos)
/// para que cambiar de solapa en la pantalla sea instantáneo y no pegue de nuevo al servidor.
/// Cambiar el período sí vuelve a pedir, porque cambian todos los números.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PanoramaController : ControllerBase
{
    private readonly PanoramaService _svc;

    public PanoramaController(PanoramaService svc) => _svc = svc;

    /// <param name="periodo">hoy | 7d | mes | 90d | anio. Default: mes.</param>
    /// <param name="meses">Cuántos meses trae el gráfico de barras (3 a 24). Default: 12.</param>
    /// <param name="mes">Opcional, formato yyyy-MM. Planta la pantalla en ese mes calendario
    /// (las flechitas y el clic sobre una barra del gráfico). Si viene, manda sobre "periodo".</param>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string? periodo = "mes",
                                         [FromQuery] int meses = 12,
                                         [FromQuery] string? mes = null,
                                         CancellationToken ct = default)
    {
        var data = await _svc.GetAsync(periodo, meses, mes, ct);
        return Ok(data);
    }
}
