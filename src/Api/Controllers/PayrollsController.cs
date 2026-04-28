using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class PayrollsController : ControllerBase
{
    private readonly PayrollService _service;

    public PayrollsController(PayrollService service)
    {
        _service = service;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? employeeId = null,
        [FromQuery] int? year = null, [FromQuery] int? month = null)
        => Ok(await _service.GetAllAsync(employeeId, year, month));

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _service.GetByIdAsync(id);
        if (p is null) return NotFound(new { error = "Liquidacion no encontrada" });
        return Ok(p);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreatePayrollRequest r)
    {
        try { return Ok(await _service.CreateAsync(r)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdatePayrollRequest r)
    {
        try
        {
            var u = await _service.UpdateAsync(id, r);
            if (u is null) return NotFound(new { error = "Liquidacion no encontrada" });
            return Ok(u);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var ok = await _service.DeleteAsync(id);
            if (!ok) return NotFound(new { error = "Liquidacion no encontrada" });
            return Ok(new { deleted = true });
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("generate-month")]
    public async Task<IActionResult> GenerateMonth([FromBody] GeneratePayrollMonthRequest r)
    {
        try { return Ok(new { created = await _service.GenerateMonthAsync(r) }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{id}/mark-paid")]
    public async Task<IActionResult> MarkPaid(int id, [FromBody] MarkPayrollPaidRequest r)
    {
        try
        {
            var u = await _service.MarkPaidAsync(id, r);
            if (u is null) return NotFound(new { error = "Liquidacion no encontrada" });
            return Ok(u);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("{id}/unmark-paid")]
    public async Task<IActionResult> UnmarkPaid(int id)
    {
        var u = await _service.UnmarkPaidAsync(id);
        if (u is null) return NotFound(new { error = "Liquidacion no encontrada" });
        return Ok(u);
    }
}
