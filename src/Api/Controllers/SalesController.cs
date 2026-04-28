using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SalesController : ControllerBase
{
    private readonly SaleService _service;

    public SalesController(SaleService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _service.GetAllAsync());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var s = await _service.GetByIdAsync(id);
        if (s is null) return NotFound(new { error = "Venta no encontrada" });
        return Ok(s);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSaleRequest request)
    {
        try { return Ok(await _service.CreateAsync(request)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{id}/cancel")]
    public async Task<IActionResult> Cancel(int id)
    {
        var op = HttpContext.Request.Headers["X-Operator-Name"].ToString();
        var s = await _service.CancelAsync(id, op);
        if (s is null) return NotFound(new { error = "Venta no encontrada" });
        return Ok(s);
    }

    [HttpGet("delete-settings")]
    public async Task<IActionResult> GetDeleteSettings()
        => Ok(await _service.GetDeleteSettingsAsync());

    [HttpPost("{id}/delete")]
    public async Task<IActionResult> Delete(int id, [FromBody] DeleteSaleRequest request)
    {
        var op = HttpContext.Request.Headers["X-Operator-Name"].ToString();
        try
        {
            var ok = await _service.DeleteAsync(id, op, request.Password);
            if (!ok) return NotFound(new { error = "Venta no encontrada" });
            return Ok(new { deleted = true });
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPatch("{id}/flags")]
    public async Task<IActionResult> UpdateFlags(int id, [FromBody] UpdateSaleFlagsRequest request)
    {
        var s = await _service.UpdateFlagsAsync(id, request);
        if (s is null) return NotFound(new { error = "Venta no encontrada" });
        return Ok(s);
    }

    [HttpGet("company-info")]
    public async Task<IActionResult> CompanyInfo() => Ok(await _service.GetCompanyInfoAsync());

    [HttpPut("company-info")]
    public async Task<IActionResult> UpdateCompanyInfo([FromBody] CompanyInfoDto dto)
        => Ok(await _service.UpdateCompanyInfoAsync(dto));
}
