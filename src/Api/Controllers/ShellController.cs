using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Credenciales de Shell Flota + lectura del saldo disponible (robot que lee el
/// token OTP del Gmail conectado). Muestra el saldo en la tarjeta del dashboard.
/// </summary>
[ApiController]
[Route("api/shell")]
[Authorize]
public class ShellController : ControllerBase
{
    private readonly ShellAccountService _service;
    private readonly ShellSyncService _syncService;

    public ShellController(ShellAccountService service, ShellSyncService syncService)
    {
        _service = service;
        _syncService = syncService;
    }

    [HttpGet("account")]
    public async Task<IActionResult> GetAccount() => Ok(await _service.GetAsync());

    [HttpPut("account")]
    public async Task<IActionResult> SaveAccount([FromBody] ShellAccountService.SaveShellAccountRequest req)
    {
        var (ok, error, dto) = await _service.SaveAsync(req);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    public record SincronizarShellResultDto(bool Ok, string? Saldo, string? Error);

    /// <summary>Lee el saldo ahora (robot). Sincrónico (~2-3 min por el token del mail).</summary>
    [HttpPost("sincronizar")]
    public async Task<IActionResult> Sincronizar()
    {
        var r = await _syncService.SincronizarAsync();
        return Ok(new SincronizarShellResultDto(r.Ok, r.Saldo, r.Error));
    }
}

[ApiController]
[Route("api/shell/test")]
public class ShellTestController : ControllerBase
{
    private readonly ShellScrapingService _scraping;
    public ShellTestController(ShellScrapingService scraping) { _scraping = scraping; }

    [HttpGet("status")]
    [Authorize]
    public async Task<IActionResult> Status() => Ok(await _scraping.GetStatusAsync());

    [HttpGet("screenshot")]
    [AllowAnonymous]
    public async Task<IActionResult> Screenshot()
    {
        var bytes = await _scraping.GetScreenshotAsync();
        if (bytes is null) return NotFound();
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        return File(bytes, "image/png");
    }
}
