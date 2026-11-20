using Microsoft.EntityFrameworkCore;
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

    /// <summary>18/11/2026: cargas de combustible del período (pantalla /combustible). Solo admin.</summary>
    [HttpGet("cargas")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Cargas([FromServices] ShellCargasService cargas, [FromQuery] DateTime? desde, [FromQuery] DateTime? hasta)
    {
        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        var h = (hasta ?? hoy).Date;
        var d = (desde ?? h.AddDays(-29)).Date;
        if (d > h) (d, h) = (h, d);
        return Ok(await cargas.ConsultarAsync(d, h));
    }

    // ─── 20/11/2026: vehículos (foto de cada uno + autos propios de repartidores). Solo admin. ───
    public record PropioRequest(string Placa, string Nombre, int RepartidorId);

    [HttpGet("flota")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Flota([FromServices] FlotaService flota, [FromServices] Api.Data.AppDbContext db)
    {
        var reps = await db.CafeRepartidores.Where(r => r.IsActive).OrderBy(r => r.Nombre)
            .Select(r => new { r.Id, r.Nombre }).ToListAsync();
        return Ok(new { Coches = await flota.CochesAsync(null, todosLosPropios: true), Repartidores = reps, Hoy = await flota.CochesHoyAsync() });
    }

    [HttpGet("flota/{placa}/foto")]
    public async Task<IActionResult> FotoFlota(string placa, [FromServices] FlotaService flota)
    {
        var foto = await flota.FotoAsync(placa);
        if (foto is null) return NotFound();
        Response.Headers.CacheControl = "private, max-age=604800";
        return File(foto, "image/jpeg");
    }

    [HttpPost("flota/{placa}/foto")]
    [Authorize(Roles = "admin")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> SubirFoto(string placa, IFormFile? file, [FromServices] FlotaService flota)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Elegí una foto." });
        using var st = file.OpenReadStream();
        using var ms = new MemoryStream();
        await st.CopyToAsync(ms); ms.Position = 0;
        var (ok, error) = await flota.GuardarFotoAsync(placa, ms);
        return ok ? Ok() : BadRequest(new { error });
    }

    [HttpDelete("flota/{placa}/foto")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> QuitarFoto(string placa, [FromServices] FlotaService flota)
    { await flota.QuitarFotoAsync(placa); return Ok(); }

    [HttpPost("flota/propio")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> GuardarPropio([FromBody] PropioRequest req, [FromServices] FlotaService flota)
    {
        var (ok, error) = await flota.GuardarPropioAsync(req.Placa, req.Nombre, req.RepartidorId);
        return ok ? Ok() : BadRequest(new { error });
    }

    [HttpDelete("flota/propio/{placa}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> BorrarPropio(string placa, [FromServices] FlotaService flota)
    { await flota.BorrarPropioAsync(placa); return Ok(); }
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
