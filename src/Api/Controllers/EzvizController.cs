using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Cámaras EZVIZ por la API oficial. Se configura desde la pantalla /integraciones/camaras:
/// se pegan el appKey/appSecret, se prueba la conexión, y se listan las cámaras + video en vivo.
/// Pedido de Osmar 2026-07-10.
/// </summary>
[ApiController]
[Route("api/ezviz")]
[Authorize]
public class EzvizController : ControllerBase
{
    private readonly EzvizAccountService _accounts;
    private readonly EzvizService _service;

    public EzvizController(EzvizAccountService accounts, EzvizService service)
    {
        _accounts = accounts;
        _service = service;
    }

    /// <summary>Devuelve la cuenta cargada (sin el appSecret), o null si no hay.</summary>
    [HttpGet("account")]
    public async Task<IActionResult> GetAccount() => Ok(await _accounts.GetAsync());

    /// <summary>Crea o actualiza appKey/appSecret/alias/host.</summary>
    [HttpPut("account")]
    public async Task<IActionResult> SaveAccount([FromBody] EzvizAccountService.SaveEzvizAccountRequest req)
    {
        var (ok, error, dto) = await _accounts.SaveAsync(req);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    public record ProbarResultDto(bool Ok, string? AreaDomain, string? Error);

    /// <summary>Prueba la conexión: pide un token a EZVIZ con las credenciales guardadas.</summary>
    [HttpPost("probar")]
    public async Task<IActionResult> Probar()
    {
        var tk = await _service.EnsureTokenAsync();
        return Ok(new ProbarResultDto(tk.Ok, tk.AreaDomain, tk.Error));
    }

    public record CamaraDto(string DeviceSerial, int ChannelNo, string? Nombre,
        bool Online, bool Encriptada, string? PicUrl);

    /// <summary>Lista las cámaras de la cuenta (nombre, online/offline, foto de vista previa).</summary>
    [HttpGet("camaras")]
    public async Task<IActionResult> Camaras()
    {
        var (ok, error, camaras) = await _service.ListarCamarasAsync();
        if (!ok) return BadRequest(new { error });
        var lista = camaras.Select(c => new CamaraDto(
            c.DeviceSerial, c.ChannelNo,
            string.IsNullOrWhiteSpace(c.ChannelName) ? c.DeviceName : c.ChannelName,
            c.Status == 1, c.IsEncrypt, c.PicUrl)).ToList();
        return Ok(lista);
    }

    public record LiveDto(bool Ok, string? Url, string? Error);

    /// <summary>Devuelve el link del video en vivo (HLS .m3u8) de una cámara sin encriptar.</summary>
    [HttpGet("live")]
    public async Task<IActionResult> Live([FromQuery] string serial, [FromQuery] int channel = 1)
    {
        var r = await _service.GetLiveAsync(serial, channel);
        return Ok(new LiveDto(r.Ok, r.Url, r.Error));
    }

    public record AutorizarRequest(string? Link);
    public record AutorizarResultDto(bool Ok, string? Error, int Camaras);

    /// <summary>Recibe el link de "Authorization Succeeded" de EZVIZ y guarda el permiso de usuario.</summary>
    [HttpPost("autorizar")]
    public async Task<IActionResult> Autorizar([FromBody] AutorizarRequest req)
    {
        var (ok, error, n) = await _service.AutorizarDesdeLinkAsync(req.Link);
        return Ok(new AutorizarResultDto(ok, error, n));
    }

    public record FotoDto(bool Ok, string? PicUrl, string? Error);

    /// <summary>Saca una foto en el momento de la cámara.</summary>
    [HttpPost("foto")]
    public async Task<IActionResult> Foto([FromQuery] string serial, [FromQuery] int channel = 1)
    {
        var (ok, error, pic) = await _service.CapturarAsync(serial, channel);
        return Ok(new FotoDto(ok, pic, error));
    }
}
