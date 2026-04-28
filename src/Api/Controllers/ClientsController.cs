using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ClientsController : ControllerBase
{
    private readonly ClientService _service;
    private readonly BulkImportService _import;

    public ClientsController(ClientService service, BulkImportService import)
    {
        _service = service;
        _import = import;
    }

    [HttpGet("import-template")]
    public IActionResult Template()
    {
        var bytes = _import.BuildClientTemplate();
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "clientes-template.xlsx");
    }

    [HttpPost("bulk-import")]
    public async Task<IActionResult> BulkImport(IFormFile file)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Subi un archivo .xlsx" });
        try
        {
            using var stream = file.OpenReadStream();
            var result = await _import.ImportClientsAsync(stream);
            return Ok(result);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _service.GetAllAsync());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var c = await _service.GetByIdAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        return Ok(c);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateClientRequest request)
    {
        try { return Ok(await _service.CreateAsync(request)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateClientRequest request)
    {
        try
        {
            var updated = await _service.UpdateAsync(id, request);
            if (updated is null) return NotFound(new { error = "Cliente no encontrado" });
            return Ok(updated);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ok = await _service.DeleteAsync(id);
        if (!ok) return NotFound(new { error = "Cliente no encontrado" });
        return Ok(new { deleted = true });
    }
}
