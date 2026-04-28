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

    public FiscalController(FiscalLookupService lookup)
    {
        _lookup = lookup;
    }

    [HttpGet("lookup")]
    public async Task<IActionResult> Lookup([FromQuery] string cuit)
    {
        if (string.IsNullOrWhiteSpace(cuit))
            return BadRequest(new { error = "Debe indicar un CUIT/CUIL." });
        var result = await _lookup.LookupByCuitAsync(cuit);
        return Ok(result);
    }
}
