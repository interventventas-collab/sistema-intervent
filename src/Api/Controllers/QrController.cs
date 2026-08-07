using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QRCoder;

namespace Api.Controllers;

/// <summary>2026-08-06: genera un código QR (PNG) para una URL cualquiera, para "llevarse"
/// una pantalla al celular escaneándola. Reusa la librería QRCoder que ya usa el proyecto.
/// Solo usuarios logueados (el token viaja en la cookie httpOnly).</summary>
[ApiController]
[Route("api/qr")]
[Authorize]
public class QrController : ControllerBase
{
    [HttpGet]
    public IActionResult Get([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return BadRequest(new { error = "Falta la URL" });
        if (url.Length > 2000) return BadRequest(new { error = "URL demasiado larga" });

        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data).GetGraphic(8);
        return File(png, "image/png");
    }
}
