using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/meli/cotejo")]
[Authorize]
public class MeliCotejoController : ControllerBase
{
    private readonly ContabiliumStagingService _staging;
    private readonly ContabiliumCotejoService _cotejo;

    public MeliCotejoController(ContabiliumStagingService staging, ContabiliumCotejoService cotejo)
    {
        _staging = staging;
        _cotejo = cotejo;
    }

    // Carga (o re-carga) los excels desde /data/files/base de datos contabilium/.
    [HttpPost("import-staging")]
    public async Task<IActionResult> ImportStaging()
    {
        try
        {
            var result = await _staging.ImportFromDefaultFolderAsync();
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen()
    {
        return Ok(await _cotejo.ResumenAsync());
    }

    [HttpGet("listar")]
    public async Task<IActionResult> Listar(
        [FromQuery] string categoria = "todos",
        [FromQuery] string? buscar = null,
        [FromQuery] int? meliAccountId = null,
        [FromQuery] int take = 200,
        [FromQuery] string? marcaContab = null,
        [FromQuery] string? vinculacion = null)
    {
        var rows = await _cotejo.ListarAsync(categoria, buscar, meliAccountId, take, marcaContab, vinculacion);
        return Ok(rows);
    }

    [HttpPost("crear-productos")]
    public async Task<IActionResult> CrearProductos([FromBody] ContabiliumCotejoService.CrearProductosRequest req)
    {
        try
        {
            if (req.Skus is null || req.Skus.Count == 0)
                return BadRequest(new { error = "No seleccionaste ningun SKU." });
            var result = await _cotejo.CrearProductosBatchAsync(req);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("crear-kits")]
    public async Task<IActionResult> CrearKits([FromBody] ContabiliumCotejoService.CrearKitsRequest req)
    {
        try
        {
            if (req.Skus is null || req.Skus.Count == 0)
                return BadRequest(new { error = "No seleccionaste ningun SKU." });
            var result = await _cotejo.CrearKitsBatchAsync(req);
            return Ok(result);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpGet("combo/{skuCombo}")]
    public async Task<IActionResult> DetalleCombo(string skuCombo)
    {
        var det = await _cotejo.DetalleComboAsync(skuCombo);
        if (det is null) return NotFound();
        return Ok(det);
    }
}
