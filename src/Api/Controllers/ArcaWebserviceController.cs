using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// CRUD de certificados .pfx de ARCA Webservices. Cada certificado se asocia
/// a un CUIT y un ambiente ("production" o "homologation"). El archivo se
/// guarda en disco vía FileStorageService bajo "Certificados ARCA/&lt;CUIT&gt;/".
/// </summary>
[ApiController]
[Route("api/arca-webservice")]
[Authorize]
public class ArcaWebserviceController : ControllerBase
{
    private readonly ArcaWebserviceAccountService _service;

    public ArcaWebserviceController(ArcaWebserviceAccountService service) { _service = service; }

    [HttpGet("accounts")]
    public async Task<IActionResult> GetAll()
    {
        var list = await _service.GetAllAsync();
        return Ok(list);
    }

    [HttpGet("accounts/{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var dto = await _service.GetByIdAsync(id);
        if (dto is null) return NotFound(new { error = "Certificado no encontrado" });
        return Ok(dto);
    }

    /// <summary>
    /// Subir un .pfx nuevo. multipart/form-data con campos cuit, alias, password,
    /// environment, file. El alias y password son opcionales; environment default
    /// "production".
    /// </summary>
    [HttpPost("accounts")]
    [RequestSizeLimit(15 * 1024 * 1024)] // 15 MB
    public async Task<IActionResult> Create(
        [FromForm] string cuit,
        [FromForm] string? alias,
        [FromForm] string? password,
        [FromForm] string? environment,
        IFormFile? file)
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Falta el archivo .pfx" });

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        var (ok, error, dto) = await _service.CreateAsync(cuit, alias, password, environment, file.FileName, bytes);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    [HttpPut("accounts/{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateArcaWebserviceAccountRequest req)
    {
        var (ok, error, dto) = await _service.UpdateAsync(id, req ?? new UpdateArcaWebserviceAccountRequest());
        if (!ok)
        {
            if (error == "Certificado no encontrado") return NotFound(new { error });
            return BadRequest(new { error });
        }
        return Ok(dto);
    }

    [HttpDelete("accounts/{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var deleted = await _service.DeleteAsync(id);
        if (!deleted) return NotFound(new { error = "Certificado no encontrado" });
        return NoContent();
    }
}
