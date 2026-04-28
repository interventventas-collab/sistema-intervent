using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Authorize]
public class StockBatchesController : ControllerBase
{
    private readonly StockBatchService _service;

    public StockBatchesController(StockBatchService service)
    {
        _service = service;
    }

    [HttpGet("/api/products/{productId:int}/stock-batches")]
    public async Task<IActionResult> GetByProduct(int productId)
    {
        return Ok(await _service.GetByProductAsync(productId));
    }

    [HttpPost("/api/products/{productId:int}/stock-batches")]
    public async Task<IActionResult> Create(int productId, [FromBody] CreateStockBatchRequest request)
    {
        try { return Ok(await _service.CreateAsync(productId, request)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("/api/stock-batches/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateStockBatchRequest request)
    {
        try
        {
            var updated = await _service.UpdateAsync(id, request);
            if (updated is null) return NotFound(new { error = "Lote no encontrado" });
            return Ok(updated);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("/api/stock-batches/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ok = await _service.DeleteAsync(id);
        if (!ok) return NotFound(new { error = "Lote no encontrado" });
        return Ok(new { deleted = true });
    }
}
