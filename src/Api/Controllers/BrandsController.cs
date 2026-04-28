using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class BrandsController : ControllerBase
{
    private readonly BrandService _service;

    public BrandsController(BrandService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _service.GetAllAsync());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var b = await _service.GetByIdAsync(id);
        if (b is null) return NotFound(new { error = "Marca no encontrada" });
        return Ok(b);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBrandRequest request)
    {
        try
        {
            var created = await _service.CreateAsync(request);
            return Ok(created);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateBrandRequest request)
    {
        try
        {
            var updated = await _service.UpdateAsync(id, request);
            if (updated is null) return NotFound(new { error = "Marca no encontrada" });
            return Ok(updated);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ok = await _service.DeleteAsync(id);
        if (!ok) return NotFound(new { error = "Marca no encontrada" });
        return Ok(new { deleted = true });
    }
}
