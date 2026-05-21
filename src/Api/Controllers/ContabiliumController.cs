using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/contabilium")]
[Authorize]
public class ContabiliumController : ControllerBase
{
    private readonly ContabiliumService _svc;

    public ContabiliumController(ContabiliumService svc) { _svc = svc; }

    public record StatusDto(bool Connected, string? Email, DateTime? LastSyncAt, int? LastSyncCount, string? LastSyncError);

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var acc = await _svc.GetAccountAsync();
        if (acc is null) return Ok(new StatusDto(false, null, null, null, null));
        return Ok(new StatusDto(true, acc.Email, acc.LastSyncAt, acc.LastSyncCount, acc.LastSyncError));
    }

    public class ConnectRequest
    {
        public string Email { get; set; } = "";
        public string ApiKey { get; set; } = "";
    }

    /// <summary>Guarda email + apikey y valida la conexion contra Contabilium.</summary>
    [HttpPost("connect")]
    public async Task<IActionResult> Connect([FromBody] ConnectRequest req)
    {
        var (ok, err) = await _svc.ConnectAsync(req.Email, req.ApiKey);
        if (!ok) return BadRequest(new { error = err });
        return Ok(new { ok = true });
    }

    /// <summary>Test simple: pide los primeros 2 conceptos para confirmar que sigue conectado.</summary>
    [HttpGet("ping")]
    public async Task<IActionResult> Ping()
    {
        var page = await _svc.ListConceptosAsync(page: 1, pageSize: 2);
        if (page is null) return BadRequest(new { error = "No se pudo conectar." });
        return Ok(new { ok = true, total = page.TotalItems, sample = page.Items.Take(2) });
    }
}
