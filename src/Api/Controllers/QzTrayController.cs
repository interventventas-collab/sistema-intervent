using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-09-24: QZ Tray (programa gratuito instalado en la PC de la impresora térmica HPRT, que
/// entiende ZPL como la Zebra) deja que la página mande la etiqueta DIRECTO a la impresora. Para
/// no preguntar "¿Permitir?" en cada impresión, QZ Tray tiene que confiar en nosotros: le cargan
/// UNA vez nuestro certificado (GET certificado?descargar=1, en QZ Tray → Site Manager) y cada
/// impresión va firmada con la llave privada (POST firmar).
///
/// El par certificado/llave se genera solo la primera vez y se guarda en AppSettings (NO en
/// /data/files, que se puede navegar desde Documentación). Dev y prod tienen cada uno el suyo:
/// el certificado que se carga en la PC tiene que salir del sistema REAL.
/// </summary>
[ApiController]
[Route("api/qz")]
[Authorize]
public class QzTrayController : ControllerBase
{
    private const string ClaveCert = "qz.certificado_pem";
    private const string ClaveLlave = "qz.llave_privada_pem";
    private static readonly SemaphoreSlim Candado = new(1, 1);

    private readonly AppDbContext _db;
    public QzTrayController(AppDbContext db) { _db = db; }

    /// <summary>Certificado público (texto PEM). Con ?descargar=1 se baja como archivo .crt.</summary>
    [HttpGet("certificado")]
    public async Task<IActionResult> Certificado([FromQuery] bool descargar = false)
    {
        var (cert, _) = await ObtenerParAsync();
        if (descargar)
        {
            Response.Headers["Content-Disposition"] = "attachment; filename=\"palanica-qz-tray.crt\"";
            return File(Encoding.ASCII.GetBytes(cert), "application/x-x509-ca-cert");
        }
        return Content(cert, "text/plain");
    }

    /// <summary>Firma (RSA SHA-512, base64) el pedido que arma qz-tray.js antes de imprimir.</summary>
    [HttpPost("firmar")]
    public async Task<IActionResult> Firmar()
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var aFirmar = await reader.ReadToEndAsync();
        if (string.IsNullOrEmpty(aFirmar) || aFirmar.Length > 20000)
            return BadRequest("Nada para firmar");

        var (_, llave) = await ObtenerParAsync();
        using var rsa = RSA.Create();
        rsa.ImportFromPem(llave);
        var firma = rsa.SignData(Encoding.UTF8.GetBytes(aFirmar), HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
        return Content(Convert.ToBase64String(firma), "text/plain");
    }

    private async Task<(string Cert, string Llave)> ObtenerParAsync()
    {
        var cert = await LeerAsync(ClaveCert);
        var llave = await LeerAsync(ClaveLlave);
        if (cert is not null && llave is not null) return (cert, llave);

        await Candado.WaitAsync();
        try
        {
            // otro pedido pudo haberlo creado mientras esperábamos
            cert = await LeerAsync(ClaveCert);
            llave = await LeerAsync(ClaveLlave);
            if (cert is not null && llave is not null) return (cert, llave);

            using var rsa = RSA.Create(2048);
            var pedido = new CertificateRequest("CN=app.palanica.com.ar, O=INTER VENT, C=AR", rsa,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var x = pedido.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
            cert = x.ExportCertificatePem();
            llave = rsa.ExportPkcs8PrivateKeyPem();

            await GuardarAsync(ClaveCert, cert);
            await GuardarAsync(ClaveLlave, llave);
            await _db.SaveChangesAsync();
            return (cert, llave);
        }
        finally { Candado.Release(); }
    }

    private async Task<string?> LeerAsync(string clave)
        => (await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(a => a.Key == clave))?.Value;

    private async Task GuardarAsync(string clave, string valor)
    {
        var s = await _db.AppSettings.FirstOrDefaultAsync(a => a.Key == clave);
        if (s is null) _db.AppSettings.Add(new AppSetting { Key = clave, Value = valor, UpdatedAt = DateTime.UtcNow });
        else { s.Value = valor; s.UpdatedAt = DateTime.UtcNow; }
    }
}
