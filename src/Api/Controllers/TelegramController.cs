using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Bot de Telegram para avisos al celu. Se configura desde /integraciones/telegram: se pega el
/// token de @BotFather, se vincula el chat del dueño (le escribe "hola" al bot), y se elige qué
/// avisos recibir. Pedido de Osmar 2026-07-10 (reemplazo de WhatsApp por Telegram).
/// </summary>
[ApiController]
[Route("api/telegram")]
[Authorize]
public class TelegramController : ControllerBase
{
    private readonly TelegramAccountService _accounts;
    private readonly TelegramService _service;

    public TelegramController(TelegramAccountService accounts, TelegramService service)
    {
        _accounts = accounts;
        _service = service;
    }

    /// <summary>Devuelve la config del bot (sin el token), o null si no hay.</summary>
    [HttpGet("account")]
    public async Task<IActionResult> GetAccount() => Ok(await _accounts.GetAsync());

    /// <summary>Crea o actualiza el token + los tildes de qué avisos mandar.</summary>
    [HttpPut("account")]
    public async Task<IActionResult> SaveAccount([FromBody] TelegramAccountService.SaveTelegramAccountRequest req)
    {
        var (ok, error, dto) = await _accounts.SaveAsync(req);
        if (!ok) return BadRequest(new { error });
        return Ok(dto);
    }

    public record ProbarResultDto(bool Ok, string? BotUsername, long? ChatId, bool TestEnviado, string? Error);

    /// <summary>Prueba el token (getMe), vincula el chat si puede, y manda un mensaje de prueba.</summary>
    [HttpPost("probar")]
    public async Task<IActionResult> Probar()
    {
        var (ok, username, chatId, testEnviado, error) = await _service.ProbarAsync();
        return Ok(new ProbarResultDto(ok, username, chatId, testEnviado, error));
    }

    public record VincularResultDto(bool Ok, long? ChatId, string? Error);

    /// <summary>Vincula el chat del dueño mirando los mensajes que le escribió al bot (getUpdates).</summary>
    [HttpPost("vincular")]
    public async Task<IActionResult> Vincular()
    {
        var (ok, chatId, error) = await _service.DetectarChatAsync();
        return Ok(new VincularResultDto(ok, chatId, error));
    }

    public record TestMsgResultDto(bool Ok, string? Error);

    /// <summary>Manda un mensaje de prueba al chat vinculado.</summary>
    [HttpPost("test-mensaje")]
    public async Task<IActionResult> TestMensaje()
    {
        var (ok, error) = await _service.SendMessageAsync(
            "🔔 Mensaje de prueba de Intervent. Si lo estás viendo, ¡funciona perfecto! 🚀");
        return Ok(new TestMsgResultDto(ok, error));
    }
}
