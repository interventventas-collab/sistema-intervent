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
    private readonly ArcaInvoiceService _invoiceService;
    private readonly ArcaInvoicePdfService _pdfService;
    private readonly FileStorageService _files;

    public ArcaWebserviceController(
        ArcaWebserviceAccountService service,
        ArcaWsService ws,
        ArcaInvoiceService invoiceService,
        ArcaInvoicePdfService pdfService,
        FileStorageService files)
    {
        _service = service;
        _ws = ws;
        _invoiceService = invoiceService;
        _pdfService = pdfService;
        _files = files;
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

    /// <summary>
    /// Emite un comprobante electrónico contra ARCA y genera el PDF correspondiente.
    /// Orquesta ArcaInvoiceService (emisión) + ArcaInvoicePdfService (PDF).
    /// </summary>
    [HttpPost("accounts/{id:int}/generate-comprobante")]
    public async Task<IActionResult> GenerateComprobante(int id, [FromBody] EmitirComprobanteRequest req)
    {
        if (req is null) return BadRequest(new { error = "Falta el body" });

        // 1. Emisión contra ARCA
        var result = await _invoiceService.EmitirComprobanteAsync(id, req);
        if (!result.Success) return Ok(result); // devolver el detalle del error al frontend

        // 2. Si autorizó OK, generar el PDF
        try
        {
            var account = await _service.GetByIdAsync(id);
            if (account is not null)
            {
                var isHomo = string.Equals(account.Environment, "homologation", StringComparison.OrdinalIgnoreCase);
                // Para emisor usamos lo que tenemos (cert + cuit + alias).
                // Los datos completos del emisor (razón social, condición IVA, domicilio)
                // se podrían cargar después desde una tabla de empresas; para la prueba
                // los completamos con defaults razonables.
                var emisor = new PdfEmisor
                {
                    Cuit = account.Cuit,
                    RazonSocial = string.IsNullOrEmpty(account.Alias) ? $"CUIT {account.Cuit}" : account.Alias!,
                    CondicionIva = isHomo ? "Responsable Inscripto (HOMO)" : "Responsable Inscripto",
                    Domicilio = null,
                };
                var comp = new PdfComprobante
                {
                    CbteTipoNro = result.CbteTipo,
                    CbteTipoNombre = result.CbteTipoNombre,
                    PtoVta = result.PtoVta,
                    CbteNro = result.CbteNro,
                    Fecha = result.Fecha,
                    Concepto = req.Concepto,
                    ImpNeto = result.ImpNeto,
                    ImpTotal = result.ImpTotal,
                    Cae = result.Cae,
                    CaeVto = result.CaeVto,
                };
                foreach (var it in req.Items)
                {
                    comp.Items.Add(new PdfItem
                    {
                        Descripcion = it.Descripcion,
                        Cantidad = it.Cantidad,
                        PrecioUnitario = it.PrecioUnitario,
                        AlicPct = ArcaInvoiceService.AlicuotaPct(it.AlicIvaId),
                    });
                }
                // IVA desglosado para Factura A
                var letra = ArcaInvoicePdfService.LetraDelTipo(result.CbteTipo);
                if (letra == "A")
                {
                    var grupos = req.Items.GroupBy(i => ArcaInvoiceService.AlicuotaPct(i.AlicIvaId))
                        .Select(g => new PdfIvaDesglose
                        {
                            Pct = g.Key,
                            Importe = Math.Round(g.Sum(x => x.Cantidad * x.PrecioUnitario) * g.Key / 100m, 2, MidpointRounding.AwayFromZero),
                        });
                    comp.IvasDesglosados.AddRange(grupos);
                }
                var receptor = new PdfReceptor
                {
                    DocTipo = req.DocTipo,
                    DocNro = req.DocNro,
                    Nombre = req.ReceptorNombre,
                    Domicilio = req.ReceptorDomicilio,
                    CondicionIvaId = req.CondicionIVAReceptorId,
                };

                var pdfBytes = _pdfService.GenerarPdfBytes(emisor, comp, receptor, isHomo);

                // Guardar PDF en disco
                var folderRel = "Comprobantes de Prueba ARCA";
                var folderAbs = _files.ResolveSafe(folderRel);
                Directory.CreateDirectory(folderAbs);
                var fileName = $"{account.Cuit} - {result.CbteTipoNombre} - {result.PtoVta:00000}-{result.CbteNro:00000000}.pdf";
                fileName = FileStorageService.SanitizeName(fileName);
                var fileAbs = Path.Combine(folderAbs, fileName);
                await System.IO.File.WriteAllBytesAsync(fileAbs, pdfBytes);

                var relPath = $"{folderRel}/{fileName}";
                result.PdfPath = relPath;
                result.PdfDownloadUrl = $"/api/files/download?path={Uri.EscapeDataString(relPath)}";
            }
        }
        catch (Exception ex)
        {
            // PDF falla, pero la emisión fue exitosa — devolvemos OK con un warning
            result.Error = "Comprobante autorizado pero hubo un problema generando el PDF: " + ex.Message;
            result.PdfPath = null;
            result.PdfDownloadUrl = null;
        }

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
