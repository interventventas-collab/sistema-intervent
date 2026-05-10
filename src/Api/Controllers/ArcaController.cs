using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// CRUD de cuentas de ARCA (ex AFIP). Las usamos para login automatizado por
/// scraping. La integración como toggle (activa/inactiva, eliminar) se maneja
/// vía /api/integrations con provider="arca" — este controller maneja las
/// cuentas individuales que cuelgan de esa integración.
/// </summary>
[ApiController]
[Route("api/arca/accounts")]
[Authorize]
public class ArcaController : ControllerBase
{
    private readonly ArcaAccountService _service;

    public ArcaController(ArcaAccountService service) { _service = service; }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var list = await _service.GetAllAsync();
        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var dto = await _service.GetByIdAsync(id);
        if (dto is null) return NotFound(new { error = "Cuenta no encontrada" });
        return Ok(dto);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateArcaAccountRequest req)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var (ok, error, dto) = await _service.CreateAsync(req);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateArcaAccountRequest req)
    {
        var (ok, error, dto) = await _service.UpdateAsync(id, req);
        if (!ok)
        {
            if (error == "Cuenta no encontrada") return NotFound(new { error });
            return BadRequest(new { error });
        }
        return Ok(dto);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var deleted = await _service.DeleteAsync(id);
        if (!deleted) return NotFound(new { error = "Cuenta no encontrada" });
        return NoContent();
    }
}
