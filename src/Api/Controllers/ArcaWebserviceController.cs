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
    private readonly ArcaEmisorService _emisorService;

    public ArcaWebserviceController(
        ArcaWebserviceAccountService service,
        ArcaWsService ws,
        ArcaInvoiceService invoiceService,
        ArcaInvoicePdfService pdfService,
        FileStorageService files,
        ArcaEmisorService emisorService)
    {
        _service = service;
        _ws = ws;
        _invoiceService = invoiceService;
        _pdfService = pdfService;
        _files = files;
        _emisorService = emisorService;
    }

    /// <summary>Arma el PdfEmisor consultando primero la ficha de empresa cargada (si existe).</summary>
    private async Task<PdfEmisor> BuildPdfEmisorAsync(ArcaWebserviceAccountDto account, bool isHomo)
    {
        var ficha = await _emisorService.GetEntityByCuitAsync(account.Cuit);
        var razonSocial = ficha?.RazonSocial
                          ?? (string.IsNullOrEmpty(account.Alias) ? $"CUIT {account.Cuit}" : account.Alias!);
        var condicionIva = ficha?.CondicionIva ?? "Responsable Inscripto";
        if (isHomo) condicionIva += " (HOMO)";

        return new PdfEmisor
        {
            Cuit = account.Cuit,
            RazonSocial = razonSocial,
            CondicionIva = condicionIva,
            Domicilio = ficha?.Domicilio,
            IIBBTipo = ficha?.IIBBTipo,
            IIBBNumero = ficha?.IIBBNumero,
            InicioActividades = ficha?.InicioActividades,
            LogoBytes = _emisorService.TryGetLogoBytes(ficha?.LogoPath),
            Telefono = ficha?.Telefono,
            Telefono2 = ficha?.Telefono2,
            Email = ficha?.Email,
            Web = ficha?.Web,
            Web2 = ficha?.Web2,
            BancoNombre = ficha?.BancoNombre,
            BancoCbu = ficha?.BancoCbu,
            BancoAlias = ficha?.BancoAlias,
        };
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
                // Tomar los datos del emisor desde la "Ficha de Empresa" (ArcaEmisor por CUIT).
                // Si no hay ficha cargada, cae a defaults razonables.
                var emisor = await BuildPdfEmisorAsync(account, isHomo);
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

    /// <summary>
    /// Genera y devuelve un PDF "consultativo" de un comprobante ya autorizado.
    /// Como FECompConsultar de ARCA no devuelve el detalle de items, el PDF se arma
    /// con UN solo item sintético "Operación facturada" + los totales reales + CAE + QR.
    /// El PDF sirve legalmente (tiene el CAE y QR) pero el detalle de items se reemplaza
    /// por una leyenda. Para PDFs completos con line items, hay que emitir el comprobante
    /// desde el sistema y guardar el detalle.
    /// </summary>
    [HttpPost("accounts/{id:int}/comprobante-pdf")]
    public async Task<IActionResult> ComprobantePdf(int id, [FromBody] ComprobantePdfRequest req)
    {
        if (req is null) return BadRequest(new { error = "Falta el body" });
        if (req.PtoVta <= 0 || req.CbteTipo <= 0 || req.CbteNro <= 0)
            return BadRequest(new { error = "PtoVta / CbteTipo / CbteNro inválidos" });

        var account = await _service.GetByIdAsync(id);
        if (account is null) return NotFound(new { error = "Cuenta no encontrada" });

        var data = await _ws.GetComprobanteForPdfAsync(id, req.PtoVta, req.CbteTipo, req.CbteNro);
        if (!data.Success) return BadRequest(new { error = data.Error });

        var letra = ArcaInvoicePdfService.LetraDelTipo(req.CbteTipo);
        var isHomo = string.Equals(account.Environment, "homologation", StringComparison.OrdinalIgnoreCase);

        var emisor = await BuildPdfEmisorAsync(account, isHomo);

        var comp = new PdfComprobante
        {
            CbteTipoNro = req.CbteTipo,
            CbteTipoNombre = ArcaWsService.NombreCbte(req.CbteTipo),
            PtoVta = req.PtoVta,
            CbteNro = req.CbteNro,
            Fecha = data.FechaYyyymmdd,
            Concepto = data.Concepto,
            ImpNeto = data.ImpNeto,
            ImpTotal = data.ImpTotal,
            Cae = data.Cae,
            CaeVto = data.CaeVtoYyyymmdd,
        };

        // Item sintético (FECompConsultar no devuelve los originales)
        if (letra == "A")
        {
            var alicPct = data.ImpNeto > 0 ? Math.Round(data.ImpIVA / data.ImpNeto * 100m, 2) : 21m;
            comp.Items.Add(new PdfItem
            {
                Descripcion = "Operación facturada (detalle de items no disponible en la consulta a ARCA)",
                Cantidad = 1,
                PrecioUnitario = data.ImpNeto,
                AlicPct = alicPct,
            });
            comp.IvasDesglosados.Add(new PdfIvaDesglose { Pct = alicPct, Importe = data.ImpIVA });
        }
        else
        {
            comp.Items.Add(new PdfItem
            {
                Descripcion = "Operación facturada (detalle de items no disponible en la consulta a ARCA)",
                Cantidad = 1,
                PrecioUnitario = data.ImpTotal,
                AlicPct = 0,
            });
        }

        var receptor = new PdfReceptor
        {
            DocTipo = data.DocTipo,
            DocNro = data.DocNro,
            Nombre = "—",
            CondicionIvaId = letra == "A" ? 1 : 5, // Factura A → RI; B/C → CF por default
        };

        var pdfBytes = _pdfService.GenerarPdfBytes(emisor, comp, receptor, isHomo);
        return File(pdfBytes, "application/pdf",
            $"{account.Cuit}-{ArcaWsService.NombreCbte(req.CbteTipo)}-{req.PtoVta:00000}-{req.CbteNro:00000000}.pdf");
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
    /// Consulta a ARCA si un comprobante específico (PtoVta + CbteTipo + CbteNro) existe.
    /// Devuelve JSON con los datos completos: CAE, importes, fecha, doc receptor, resultado.
    /// Si ARCA no lo conoce → Success=false + Error con el motivo.
    /// Útil para verificar comprobantes "sospechosos" sin descargar PDF.
    /// </summary>
    [HttpPost("accounts/{id:int}/consultar-comprobante")]
    public async Task<IActionResult> ConsultarComprobante(int id, [FromBody] ComprobantePdfRequest req)
    {
        if (req is null) return BadRequest(new { error = "Falta el body" });
        if (req.PtoVta <= 0 || req.CbteTipo <= 0 || req.CbteNro <= 0)
            return BadRequest(new { error = "PtoVta / CbteTipo / CbteNro inválidos" });

        var account = await _service.GetByIdAsync(id);
        if (account is null) return NotFound(new { error = "Cuenta no encontrada" });

        var data = await _ws.GetComprobanteForPdfAsync(id, req.PtoVta, req.CbteTipo, req.CbteNro);
        return Ok(new
        {
            success = data.Success,
            error = data.Error,
            cbteTipo = req.CbteTipo,
            cbteTipoNombre = ArcaWsService.NombreCbte(req.CbteTipo),
            ptoVta = req.PtoVta,
            cbteNro = req.CbteNro,
            docTipo = data.DocTipo,
            docNro = data.DocNro,
            fechaYyyymmdd = data.FechaYyyymmdd,
            cae = data.Cae,
            caeVtoYyyymmdd = data.CaeVtoYyyymmdd,
            impNeto = data.ImpNeto,
            impIVA = data.ImpIVA,
            impTotal = data.ImpTotal,
            resultado = data.Resultado,
        });
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
