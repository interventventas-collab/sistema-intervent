using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/arca-emisor")]
[Authorize]
public class ArcaEmisorController : ControllerBase
{
    private readonly ArcaEmisorService _service;

    public ArcaEmisorController(ArcaEmisorService service) { _service = service; }

    [HttpGet]
    public async Task<IActionResult> GetAll()
        => Ok(await _service.GetAllAsync());

    [HttpGet("{cuit}")]
    public async Task<IActionResult> GetByCuit(string cuit)
    {
        var dto = await _service.GetByCuitAsync(cuit);
        if (dto is null) return NotFound(new { error = "No hay ficha cargada para ese CUIT" });
        return Ok(dto);
    }

    [HttpPost]
    public async Task<IActionResult> Upsert([FromBody] UpsertArcaEmisorRequest req)
    {
        var (ok, error, dto) = await _service.UpsertAsync(req);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    [HttpPost("{cuit}/logo")]
    [RequestSizeLimit(8 * 1024 * 1024)] // 8 MB
    public async Task<IActionResult> UploadLogo(string cuit, IFormFile? file)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Falta el archivo" });
        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await file.CopyToAsync(ms);
            bytes = ms.ToArray();
        }
        var (ok, error, dto) = await _service.UploadLogoAsync(cuit, bytes, file.FileName);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    [HttpDelete("{cuit}/logo")]
    public async Task<IActionResult> DeleteLogo(string cuit)
    {
        var ok = await _service.DeleteLogoAsync(cuit);
        if (!ok) return NotFound(new { error = "No había logo cargado" });
        return NoContent();
    }
}
