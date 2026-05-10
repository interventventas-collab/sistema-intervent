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
    private readonly ArcaScrapingService _scraping;

    public ArcaController(ArcaAccountService service, ArcaScrapingService scraping)
    {
        _service = service;
        _scraping = scraping;
    }

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

    // ============================================================
    // TEST de login + scraping (proxy al container playwright)
    // ============================================================

    /// <summary>
    /// Dispara el test para una cuenta cargada. Responde inmediato; el cliente
    /// debe pollear /api/arca/test/status para ver progreso y resultado.
    /// </summary>
    [HttpPost("{id:int}/test")]
    public async Task<IActionResult> StartTest(int id)
    {
        var dto = await _service.GetByIdAsync(id);
        if (dto is null) return NotFound(new { error = "Cuenta no encontrada" });
        if (!dto.HasPassword) return BadRequest(new { error = "Esta cuenta no tiene contraseña cargada" });

        // Necesitamos la password en claro — el service la tiene en la entidad,
        // así que pedimos un método interno para obtenerla acá.
        var password = await _service.GetPasswordAsync(id);
        if (string.IsNullOrEmpty(password)) return BadRequest(new { error = "No se pudo leer la contraseña" });

        var (ok, error) = await _scraping.StartTestAsync(dto.Cuit, dto.CuitLogin, password);
        if (!ok) return BadRequest(new { error });
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Dispara el flujo "Mis Comprobantes" para una cuenta — login + descarga
    /// de Emitidos y Recibidos según rango. Responde inmediato; el cliente
    /// pollea /api/arca/test/status para ver progreso y resultado.
    /// </summary>
    [HttpPost("{id:int}/comprobantes")]
    public async Task<IActionResult> StartComprobantes(int id, [FromBody] RangoFechasRequest rango)
    {
        var dto = await _service.GetByIdAsync(id);
        if (dto is null) return NotFound(new { error = "Cuenta no encontrada" });
        if (!dto.HasPassword) return BadRequest(new { error = "Esta cuenta no tiene contraseña cargada" });

        var password = await _service.GetPasswordAsync(id);
        if (string.IsNullOrEmpty(password)) return BadRequest(new { error = "No se pudo leer la contraseña" });

        var (ok, error) = await _scraping.StartComprobantesAsync(dto.Cuit, dto.CuitLogin, password, rango ?? new RangoFechasRequest());
        if (!ok) return BadRequest(new { error });
        return Ok(new { ok = true });
    }
}

// ============================================================
// Controller separado para los endpoints de status/screenshot del test.
// El screenshot es [AllowAnonymous] así el <img> del frontend puede pollear
// sin tener que mandar el Bearer en cada request — la cookie httpOnly viaja
// igual, pero las requests de <img> tag no incluyen Authorization headers.
// ============================================================

[ApiController]
[Route("api/arca/test")]
public class ArcaTestController : ControllerBase
{
    private readonly ArcaScrapingService _scraping;

    public ArcaTestController(ArcaScrapingService scraping) { _scraping = scraping; }

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
