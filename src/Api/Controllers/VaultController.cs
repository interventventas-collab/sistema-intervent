using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/vault")]
[Authorize]
public class VaultController : ControllerBase
{
    private readonly VaultService _vault;
    private const string TOKEN_HEADER = "X-Vault-Token";

    public VaultController(VaultService vault) { _vault = vault; }

    private string? GetToken()
    {
        if (Request.Headers.TryGetValue(TOKEN_HEADER, out var v)) return v.ToString();
        return null;
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status()
    {
        var token = GetToken();
        return Ok(await _vault.GetStatusAsync(token));
    }

    [HttpPost("setup")]
    public async Task<IActionResult> Setup([FromBody] VaultSetupRequest req)
    {
        try
        {
            var status = await _vault.SetupAsync(req.Password, req.AutoLockMinutes ?? 5);
            return Ok(status);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("unlock")]
    public async Task<IActionResult> Unlock([FromBody] VaultUnlockRequest req)
    {
        try
        {
            var resp = await _vault.UnlockAsync(req.Password);
            // OJO: no usar 401 aca — el cliente Blazor trata todo 401 como "sesion JWT expirada"
            // y te manda al login. La maestra incorrecta es un error de validacion, no de auth.
            if (resp is null) return BadRequest(new { error = "Contraseña maestra incorrecta" });
            return Ok(resp);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("lock")]
    public IActionResult Lock()
    {
        var token = GetToken();
        if (!string.IsNullOrEmpty(token)) _vault.Lock(token);
        return Ok(new { locked = true });
    }

    [HttpGet("entries")]
    public async Task<IActionResult> List()
    {
        try
        {
            var token = GetToken() ?? "";
            return Ok(await _vault.ListEntriesAsync(token));
        }
        catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Bóveda bloqueada" }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("entries")]
    public async Task<IActionResult> Create([FromBody] VaultUpsertEntryRequest req)
    {
        try
        {
            var token = GetToken() ?? "";
            return Ok(await _vault.CreateEntryAsync(token, req));
        }
        catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Bóveda bloqueada" }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("entries/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] VaultUpsertEntryRequest req)
    {
        try
        {
            var token = GetToken() ?? "";
            var updated = await _vault.UpdateEntryAsync(token, id, req);
            if (updated is null) return NotFound(new { error = "Entry no encontrada" });
            return Ok(updated);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Bóveda bloqueada" }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("entries/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var token = GetToken() ?? "";
            var ok = await _vault.DeleteEntryAsync(token, id);
            if (!ok) return NotFound(new { error = "Entry no encontrada" });
            return Ok(new { deleted = true });
        }
        catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Bóveda bloqueada" }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("change-master")]
    public async Task<IActionResult> ChangeMaster([FromBody] VaultChangeMasterRequest req)
    {
        try
        {
            var token = GetToken() ?? "";
            await _vault.ChangeMasterAsync(token, req.OldPassword, req.NewPassword);
            return Ok(new { changed = true });
        }
        catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Bóveda bloqueada" }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("settings")]
    public async Task<IActionResult> UpdateSettings([FromBody] VaultUpdateSettingsRequest req)
    {
        try
        {
            var token = GetToken() ?? "";
            if (req.AutoLockMinutes.HasValue)
                await _vault.UpdateAutoLockAsync(token, req.AutoLockMinutes.Value);
            return Ok(new { updated = true });
        }
        catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Bóveda bloqueada" }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPost("generate")]
    public IActionResult Generate([FromBody] VaultGenerateRequest req)
    {
        var pwd = VaultService.GenerateSecurePassword(
            req.Length,
            req.IncludeSymbols,
            req.IncludeNumbers,
            req.IncludeUppercase);
        return Ok(new VaultGenerateResponse(pwd));
    }

    [HttpGet("categorias")]
    public async Task<IActionResult> Categorias()
    {
        try
        {
            var token = GetToken() ?? "";
            return Ok(await _vault.ListCategoriasAsync(token));
        }
        catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Bóveda bloqueada" }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("template-excel")]
    public IActionResult TemplateExcel()
    {
        var bytes = VaultService.BuildTemplateExcel();
        return File(bytes,
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "boveda-plantilla.xlsx");
    }

    [HttpPost("import-excel")]
    [RequestSizeLimit(10_485_760)] // 10 MB
    public async Task<IActionResult> ImportExcel(IFormFile file)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "No se recibió ningún archivo." });
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Solo se acepta formato .xlsx" });
        try
        {
            var token = GetToken() ?? "";
            using var stream = file.OpenReadStream();
            var result = await _vault.ImportFromExcelAsync(token, stream);
            return Ok(result);
        }
        catch (UnauthorizedAccessException) { return Unauthorized(new { error = "Bóveda bloqueada" }); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }
}
