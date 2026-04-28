using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SuppliersController : ControllerBase
{
    private readonly SupplierService _service;
    private readonly BulkImportService _import;

    public SuppliersController(SupplierService service, BulkImportService import)
    {
        _service = service;
        _import = import;
    }

    [HttpGet("import-template")]
    public IActionResult Template()
    {
        var bytes = _import.BuildSupplierTemplate();
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "proveedores-template.xlsx");
    }

    [HttpPost("bulk-import")]
    public async Task<IActionResult> BulkImport(IFormFile file)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Subi un archivo .xlsx" });
        try
        {
            using var stream = file.OpenReadStream();
            var result = await _import.ImportSuppliersAsync(stream);
            return Ok(result);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _service.GetAllAsync());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var s = await _service.GetByIdAsync(id);
        if (s is null) return NotFound(new { error = "Proveedor no encontrado" });
        return Ok(s);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSupplierRequest request)
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
    public async Task<IActionResult> Update(int id, [FromBody] UpdateSupplierRequest request)
    {
        try
        {
            var updated = await _service.UpdateAsync(id, request);
            if (updated is null) return NotFound(new { error = "Proveedor no encontrado" });
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
        if (!ok) return NotFound(new { error = "Proveedor no encontrado" });
        return Ok(new { deleted = true });
    }
}
