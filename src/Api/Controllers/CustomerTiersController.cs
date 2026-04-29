using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/customer-tiers")]
[Authorize]
public class CustomerTiersController : ControllerBase
{
    private readonly CustomerTierService _service;

    public CustomerTiersController(CustomerTierService service)
    {
        _service = service;
    }

    // === Listas (CRUD) ===

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _service.GetAllAsync());

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var t = await _service.GetByIdAsync(id);
        return t is null ? NotFound() : Ok(t);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCustomerTierRequest req)
    {
        try { return Ok(await _service.CreateAsync(req)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCustomerTierRequest req)
    {
        try
        {
            var t = await _service.UpdateAsync(id, req);
            return t is null ? NotFound() : Ok(t);
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var ok = await _service.DeleteAsync(id);
            return ok ? NoContent() : NotFound();
        }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    // === Precios por producto ===

    /// <summary>Devuelve el precio de un producto en cada lista activa.</summary>
    [HttpGet("/api/products/{productId:int}/tier-prices")]
    public async Task<IActionResult> GetPricesForProduct(int productId)
        => Ok(await _service.GetPricesForProductAsync(productId));

    /// <summary>Crea o actualiza un override (precio especial) para un producto en una lista.</summary>
    [HttpPost("price-override")]
    public async Task<IActionResult> SetPriceOverride([FromBody] SetProductPriceOverrideRequest req)
    {
        try { return Ok(await _service.SetPriceOverrideAsync(req)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Elimina un override y vuelve al calculo automatico.</summary>
    [HttpDelete("price-override/{productId:int}/{tierId:int}")]
    public async Task<IActionResult> DeletePriceOverride(int productId, int tierId)
    {
        var ok = await _service.DeletePriceOverrideAsync(productId, tierId);
        return ok ? NoContent() : NotFound();
    }
}
