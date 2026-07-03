using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Credenciales del Office Banking de Galicia + prueba de login (proxy al robot
/// Playwright). Paso 1 del scraping de Galicia: verificar que el robot puede
/// entrar desde el servidor. La descarga de movimientos vendrá en un paso siguiente.
/// </summary>
[ApiController]
[Route("api/galicia")]
[Authorize]
public class GaliciaController : ControllerBase
{
    private readonly GaliciaAccountService _service;
    private readonly GaliciaScrapingService _scraping;
    private readonly GaliciaSyncService _syncService;

    public GaliciaController(GaliciaAccountService service, GaliciaScrapingService scraping,
        GaliciaSyncService syncService)
    {
        _service = service;
        _scraping = scraping;
        _syncService = syncService;
    }

    /// <summary>Devuelve la cuenta cargada (sin la clave), o null si no hay.</summary>
    [HttpGet("account")]
    public async Task<IActionResult> GetAccount()
    {
        var dto = await _service.GetAsync();
        return Ok(dto); // puede ser null → el frontend muestra el form vacío
    }

    /// <summary>Crea o actualiza usuario/clave/alias.</summary>
    [HttpPut("account")]
    public async Task<IActionResult> SaveAccount([FromBody] GaliciaAccountService.SaveGaliciaAccountRequest req)
    {
        var (ok, error, dto) = await _service.SaveAsync(req);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    public record TestLoginRequest(bool Submit);

    /// <summary>
    /// Dispara la prueba de login. body { submit }: submit=false solo abre el
    /// formulario y saca foto SIN enviar; submit=true aprieta "Ingresar".
    /// Responde inmediato; el cliente pollea /api/galicia/test/status.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> StartTest([FromBody] TestLoginRequest req)
    {
        var dto = await _service.GetAsync();
        if (dto is null) return BadRequest(new { error = "Cargá primero el usuario y la clave" });

        string? password = null;
        if (req.Submit)
        {
            if (!dto.HasPassword) return BadRequest(new { error = "No hay clave cargada" });
            password = await _service.GetPasswordAsync();
            if (string.IsNullOrEmpty(password)) return BadRequest(new { error = "No se pudo leer la clave" });
        }

        var (ok, error) = await _scraping.StartLoginTestAsync(dto.Usuario, password, req.Submit);
        if (!ok) return BadRequest(new { error });
        return Ok(new { ok = true });
    }

    public record SincronizarResultDto(bool Ok, int Nuevos, int SinCambios, string? Error, List<string>? Detalles);

    /// <summary>
    /// Sincroniza los movimientos: el robot entra, baja el CSV y lo importa al Extracto
    /// de Banco (dedup por hash). Es sincrónico (espera al robot ~hasta 2 min) porque
    /// se dispara con un botón manual. La tarjeta "Saldo Banco Galicia" del dashboard
    /// se alimenta del mismo Extracto, así que se actualiza sola.
    /// </summary>
    [HttpPost("sincronizar")]
    public async Task<IActionResult> Sincronizar()
    {
        var r = await _syncService.SincronizarAsync();
        return Ok(new SincronizarResultDto(r.Ok, r.Nuevos, r.SinCambios, r.Error, r.Detalles));
    }
}

/// <summary>
/// Status/screenshot de la prueba. El screenshot es [AllowAnonymous] para que el
/// &lt;img&gt; del frontend pueda pollear sin header Authorization (misma razón que ARCA).
/// </summary>
[ApiController]
[Route("api/galicia/test")]
public class GaliciaTestController : ControllerBase
{
    private readonly GaliciaScrapingService _scraping;

    public GaliciaTestController(GaliciaScrapingService scraping) { _scraping = scraping; }

    [HttpGet("status")]
    [Authorize]
    public async Task<IActionResult> Status()
    {
        var status = await _scraping.GetStatusAsync();
        return Ok(status);
    }

    [HttpGet("screenshot")]
    [AllowAnonymous]
    public async Task<IActionResult> Screenshot()
    {
        var bytes = await _scraping.GetScreenshotAsync();
        if (bytes is null) return NotFound();
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
        return File(bytes, "image/png");
    }
}
