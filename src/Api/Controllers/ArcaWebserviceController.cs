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
    private readonly ArcaWsService _ws;

    public ArcaWebserviceController(ArcaWebserviceAccountService service, ArcaWsService ws)
    {
        _service = service;
        _ws = ws;
    }

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

    // ============================================================
    // Wizard: generar CSR → bajar .csr → subir .crt → finalizar .pfx
    // ============================================================

    /// <summary>Paso 1: genera la clave privada + el CSR y guarda el pedido temporal.</summary>
    [HttpPost("csr")]
    public async Task<IActionResult> GenerateCsr([FromBody] GenerateCsrRequest req)
    {
        var (ok, error, dto) = await _service.GenerateCsrAsync(req?.Cuit ?? "", req?.Alias);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    /// <summary>Paso 2: descarga el .csr generado para subirlo a ARCA.</summary>
    [HttpGet("csr/{id:int}/download")]
    public async Task<IActionResult> DownloadCsr(int id)
    {
        var result = await _service.GetCsrDownloadAsync(id);
        if (result is null) return NotFound(new { error = "Pedido no encontrado" });
        return File(result.Value.bytes, "application/x-pem-file", result.Value.fileName);
    }

    // ============================================================
    // Probar certificado contra WSAA + WSFEv1
    // ============================================================

    /// <summary>
    /// Autentica el .pfx contra WSAA y trae los puntos de venta (producción)
    /// o devuelve IsHomologation=true para que la UI muestre el form manual.
    /// </summary>
    [HttpPost("accounts/{id:int}/test-certificate")]
    public async Task<IActionResult> TestCertificate(int id)
    {
        var result = await _ws.TestCertificateAsync(id);
        return Ok(result);
    }

    /// <summary>Trae los últimos N comprobantes de un PtoVta + CbteTipo.</summary>
    [HttpPost("accounts/{id:int}/last-comprobantes")]
    public async Task<IActionResult> LastComprobantes(int id, [FromBody] UltimosComprobantesRequest req)
    {
        if (req is null) return BadRequest(new { error = "Falta el body" });
        if (req.PtoVta <= 0) return BadRequest(new { error = "PtoVta inválido" });
        if (req.CbteTipo <= 0) return BadRequest(new { error = "CbteTipo inválido" });
        var result = await _ws.GetUltimosComprobantesAsync(id, req.PtoVta, req.CbteTipo, req.UltimoNro, req.Cantidad);
        return Ok(result);
    }

    /// <summary>
    /// Paso 3: combina la clave privada del pedido con el .crt recibido de ARCA,
    /// genera el .pfx final, lo guarda en disco y crea el registro definitivo
    /// en ArcaWebserviceAccounts. Elimina el pedido temporal.
    /// </summary>
    [HttpPost("csr/{id:int}/finalize")]
    [RequestSizeLimit(15 * 1024 * 1024)] // 15 MB
    public async Task<IActionResult> FinalizeCsr(
        int id,
        [FromForm] IFormFile? crt,
        [FromForm] string? password,
        [FromForm] string? environment,
        [FromForm] string? alias)
    {
        if (crt is null || crt.Length == 0)
            return BadRequest(new { error = "Falta el archivo .crt" });

        byte[] bytes;
        using (var ms = new MemoryStream())
        {
            await crt.CopyToAsync(ms);
            bytes = ms.ToArray();
        }

        var (ok, error, dto) = await _service.FinalizeCsrAsync(id, bytes, password, environment, alias);
        if (!ok)
        {
            if (error?.StartsWith("Pedido de CSR no encontrado") == true)
                return NotFound(new { error });
            return BadRequest(new { error });
        }
        return Ok(dto);
    }
}
