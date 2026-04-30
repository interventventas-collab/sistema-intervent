using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/inventory")]
[Authorize]
public class InventoryController : ControllerBase
{
    private readonly StockMovementService _service;

    public InventoryController(StockMovementService service)
    {
        _service = service;
    }

    [HttpGet("warehouses")]
    public async Task<IActionResult> GetWarehouses() => Ok(await _service.GetWarehousesAsync());

    [HttpPost("stock-adjust")]
    public async Task<IActionResult> AdjustStock([FromBody] AdjustStockRequest req)
    {
        try { return Ok(await _service.AdjustAsync(req)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("movements")]
    public async Task<IActionResult> GetMovements(
        [FromQuery] int? productId = null,
        [FromQuery] int? warehouseId = null,
        [FromQuery] int take = 50)
        => Ok(await _service.GetMovementsAsync(productId, warehouseId, take));
}
