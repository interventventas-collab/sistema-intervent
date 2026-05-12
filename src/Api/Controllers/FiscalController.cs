using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/fiscal")]
[Authorize]
public class FiscalController : ControllerBase
{
    private readonly FiscalLookupService _lookup;
    private readonly ArcaPadronService _padron;

    public FiscalController(FiscalLookupService lookup, ArcaPadronService padron)
    {
        _lookup = lookup;
        _padron = padron;
    }

    /// <summary>Lookup gratis vía scraping (cuitonline.com). Fallback cuando padrón ARCA falla.</summary>
    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup([FromQuery] string cuit)
    {
        if (string.IsNullOrWhiteSpace(cuit))
            return BadRequest(new { error = "Debe indicar un CUIT/CUIL." });
        var result = await _lookup.LookupByCuitAsync(cuit);
        return Ok(result);
    }

    /// <summary>
    /// Consulta oficial al Padrón ARCA (ws_sr_padron_a13). Devuelve datos fiscales
    /// completos: razón social, domicilio fiscal, CP, localidad, provincia, condición IVA.
    /// Requiere certificado ARCA cargado y que el servicio esté autorizado en el
    /// Administrador de Relaciones de Clave Fiscal.
    /// </summary>
    [HttpGet("padron")]
    public async Task<IActionResult> Padron([FromQuery] string cuit, [FromQuery] string? cuitEmisor = null)
    {
        if (string.IsNullOrWhiteSpace(cuit))
            return BadRequest(new { error = "Debe indicar un CUIT/CUIL." });
        var result = await _padron.ConsultarAsync(cuit, cuitEmisor);
        return Ok(result);
    }
}
