using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// 2026-11-26: telefono dentro del sistema. Le da al navegador los datos para
/// conectarse a la central telefonica (Asterisk, carpeta telefonia/) como interno 103.
/// Solo admin: la clave del interno no tiene que llegar a otros usuarios.
/// </summary>
[ApiController]
[Route("api/telefono")]
[Authorize(Roles = "admin")]
public class TelefonoController : ControllerBase
{
    public record InternoDto(string Nombre, string Numero);
    public record TelefonoConfigDto(bool Habilitado, string WsUrl, string Dominio, string Usuario, string Clave, List<InternoDto> Internos);

    [HttpGet("config")]
    public ActionResult<TelefonoConfigDto> GetConfig()
    {
        var clave = Environment.GetEnvironmentVariable("TEL_103_PASS") ?? "";
        var ws = Environment.GetEnvironmentVariable("TELEFONO_WS_URL") ?? "";
        var internos = new List<InternoDto> { new("Oficina", "101"), new("Notebook", "102") };
        if (string.IsNullOrWhiteSpace(clave) || string.IsNullOrWhiteSpace(ws))
            return new TelefonoConfigDto(false, "", "", "", "", internos);
        return new TelefonoConfigDto(true, ws, "179.43.123.134", "103", clave, internos);
    }
}
