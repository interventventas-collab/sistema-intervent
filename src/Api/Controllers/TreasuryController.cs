using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/treasury")]
[Authorize]
public class TreasuryController : ControllerBase
{
    private readonly TreasuryService _service;

    public TreasuryController(TreasuryService service)
    {
        _service = service;
    }

    // ===== Cuentas =====

    [HttpGet("accounts")]
    public async Task<IActionResult> GetAccounts() => Ok(await _service.GetAccountsAsync());

    [HttpGet("accounts/{id}")]
    public async Task<IActionResult> GetAccount(int id)
    {
        var a = await _service.GetAccountAsync(id);
        if (a is null) return NotFound(new { error = "Cuenta no encontrada" });
        return Ok(a);
    }

    [HttpPost("accounts")]
    public async Task<IActionResult> CreateAccount([FromBody] CreateTreasuryAccountRequest r)
    {
        try { return Ok(await _service.CreateAccountAsync(r)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("accounts/{id}")]
    public async Task<IActionResult> UpdateAccount(int id, [FromBody] UpdateTreasuryAccountRequest r)
    {
        try
        {
            var u = await _service.UpdateAccountAsync(id, r);
            if (u is null) return NotFound(new { error = "Cuenta no encontrada" });
            return Ok(u);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("accounts/{id}")]
    public async Task<IActionResult> DeleteAccount(int id)
    {
        var ok = await _service.DeleteAccountAsync(id);
        if (!ok) return NotFound(new { error = "Cuenta no encontrada" });
        return Ok(new { deleted = true });
    }

    // ===== Movimientos =====

    [HttpGet("movements")]
    public async Task<IActionResult> GetMovements([FromQuery] int? accountId = null,
        [FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
        => Ok(await _service.GetMovementsAsync(accountId, from, to));

    [HttpPost("movements")]
    public async Task<IActionResult> CreateMovement([FromBody] CreateTreasuryMovementRequest r)
    {
        try { return Ok(await _service.CreateMovementAsync(r)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("movements/{id}")]
    public async Task<IActionResult> DeleteMovement(int id)
    {
        var ok = await _service.DeleteMovementAsync(id);
        if (!ok) return NotFound(new { error = "Movimiento no encontrado" });
        return Ok(new { deleted = true });
    }
}
