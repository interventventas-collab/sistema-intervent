using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/pricing")]
[Authorize]
public class PricingController : ControllerBase
{
    private readonly PricingService _service;

    public PricingController(PricingService service) { _service = service; }

    // ===== Companies =====
    [HttpGet("companies")]
    public async Task<IActionResult> GetCompanies() => Ok(await _service.GetCompaniesAsync());

    // ===== Product prices =====
    [HttpGet("products/{productId:int}/prices")]
    public async Task<IActionResult> GetProductPrices(int productId)
        => Ok(await _service.GetProductPricesAsync(productId));

    [HttpPost("products/prices")]
    public async Task<IActionResult> SetProductPrice([FromBody] SetProductCompanyPriceRequest req)
    {
        var dto = await _service.SetProductPriceAsync(req);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpDelete("products/{productId:int}/prices/{companyId:int}")]
    public async Task<IActionResult> DeleteProductPrice(int productId, int companyId)
    {
        var ok = await _service.DeleteProductPriceAsync(productId, companyId);
        return ok ? Ok(new { deleted = true }) : NotFound();
    }

    // ===== Brand markups =====
    [HttpGet("brands/{brandId:int}/markups")]
    public async Task<IActionResult> GetBrandMarkups(int brandId)
        => Ok(await _service.GetBrandMarkupsAsync(brandId));

    [HttpPost("brands/markups")]
    public async Task<IActionResult> SetBrandMarkup([FromBody] SetBrandCompanyMarkupRequest req)
    {
        var dto = await _service.SetBrandMarkupAsync(req);
        return dto is null ? NotFound() : Ok(dto);
    }

    [HttpDelete("brands/{brandId:int}/markups/{companyId:int}")]
    public async Task<IActionResult> DeleteBrandMarkup(int brandId, int companyId)
    {
        var ok = await _service.DeleteBrandMarkupAsync(brandId, companyId);
        return ok ? Ok(new { deleted = true }) : NotFound();
    }

    // ===== Resolver =====
    [HttpGet("resolve")]
    public async Task<IActionResult> ResolvePrice([FromQuery] int productId, [FromQuery] int? companyId)
        => Ok(await _service.ResolvePriceAsync(productId, companyId));
}
