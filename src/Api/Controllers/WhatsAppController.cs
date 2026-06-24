using System.Text.Json;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/whatsapp")]
[Authorize]
public class WhatsAppController : ControllerBase
{
    private readonly WhatsAppService _wa;
    private readonly AuditLogService _audit;

    public WhatsAppController(WhatsAppService wa, AuditLogService audit)
    {
        _wa = wa;
        _audit = audit;
    }

    private string? CurrentUser => User?.Identity?.Name;

    private static string Preview(string? text, int max = 300)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text.Substring(0, max) + "...";
    }

    private async Task AuditSendAsync(string phone, string? name, string message, WhatsAppSendResult result)
    {
        var payload = JsonSerializer.Serialize(new
        {
            phone,
            name,
            message = Preview(message),
            success = result.Success,
            resultMessage = result.Message
        });
        var action = result.Success ? "WHATSAPP_SEND_OK" : "WHATSAPP_SEND_FAIL";
        await _audit.LogAsync("WhatsApp", phone ?? "", action, payload, CurrentUser);
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var status = await _wa.GetStatusAsync();
        return Ok(status);
    }

    /// <summary>2026-06-23: Lista los chats del sidebar del WhatsApp Web vinculado.
    /// limit (query) por defecto 50. Hace scraping en el container Playwright en cada llamada.</summary>
    [HttpGet("chats")]
    public async Task<IActionResult> GetChats([FromQuery] int limit = 50)
    {
        var chats = await _wa.ListChatsAsync(limit);
        return Ok(new { chats });
    }

    /// <summary>2026-06-23: Abre un chat por nombre (click en el sidebar) y devuelve mensajes.</summary>
    [HttpPost("chats/open")]
    public async Task<IActionResult> OpenChat([FromBody] OpenChatRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Name)) return BadRequest(new { error = "name requerido" });
        var dto = await _wa.OpenChatByNameAsync(req.Name);
        return Ok(dto);
    }

    /// <summary>2026-06-23: Abre un chat por INDICE del sidebar (la posicion que devolvio el ultimo
    /// /chats/list). Mucho mas robusto que open-by-name — no depende del matching de texto.
    /// Si el sidebar se reordeno (entro mensaje nuevo), devuelve 409 y el frontend refresca.</summary>
    [HttpPost("chats/open-by-index")]
    public async Task<IActionResult> OpenChatByIndex([FromBody] OpenChatByIndexRequest req)
    {
        if (req is null || req.Index < 0) return BadRequest(new { error = "index requerido (>=0)" });
        var dto = await _wa.OpenChatByIndexAsync(req.Index, req.Name ?? "");
        return Ok(dto);
    }

    /// <summary>2026-06-23: Manda un mensaje al chat actualmente abierto en el WA Web.</summary>
    [HttpPost("chats/send")]
    public async Task<IActionResult> SendToOpenChat([FromBody] SendToChatRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Text)) return BadRequest(new { error = "text requerido" });
        var ok = await _wa.SendToCurrentChatAsync(req.Text);
        return ok ? Ok(new { ok = true }) : StatusCode(500, new { error = "No se pudo mandar" });
    }

    public class OpenChatRequest { public string Name { get; set; } = ""; }
    public class OpenChatByIndexRequest { public int Index { get; set; } public string? Name { get; set; } }
    public class SendToChatRequest { public string Text { get; set; } = ""; }

    [HttpPost("link")]
    public async Task<IActionResult> StartLinking()
    {
        await _wa.StartLinkingAsync();
        return Ok(new { ok = true });
    }

    // QR es [AllowAnonymous] para que el <img> pueda pollear sin Bearer
    [HttpGet("qr")]
    [AllowAnonymous]
    public async Task<IActionResult> GetQr()
    {
        var bytes = await _wa.GetQrScreenshotAsync();
        if (bytes is null) return NotFound();
        Response.Headers["Cache-Control"] = "no-cache, no-store, must-revalidate";
        Response.Headers["Pragma"] = "no-cache";
        Response.Headers["Expires"] = "0";
        return File(bytes, "image/png");
    }

    [HttpGet("check-linked")]
    public async Task<IActionResult> CheckLinked()
    {
        var linked = await _wa.CheckLinkedAsync();
        return Ok(new { linked });
    }

    [HttpPost("unlink")]
    public async Task<IActionResult> Unlink()
    {
        await _wa.UnlinkAsync();
        return Ok(new { ok = true });
    }

    [HttpPost("cancel-link")]
    public async Task<IActionResult> CancelLink()
    {
        await _wa.CancelLinkAsync();
        return Ok(new { ok = true });
    }

    public class SendRequest
    {
        public string Phone { get; set; } = "";
        public string Message { get; set; } = "";
    }

    [HttpPost("send")]
    public async Task<IActionResult> Send([FromBody] SendRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Phone) || string.IsNullOrWhiteSpace(req.Message))
            return BadRequest(new { error = "phone y message son requeridos" });
        WhatsAppSendResult result;
        try
        {
            result = await _wa.SendMessageAsync(req.Phone, req.Message);
        }
        catch (Exception ex)
        {
            result = new WhatsAppSendResult { Phone = req.Phone, Success = false, Message = ex.Message };
            await AuditSendAsync(req.Phone, null, req.Message, result);
            throw;
        }
        await AuditSendAsync(req.Phone, null, req.Message, result);
        return Ok(result);
    }

    public class SendBulkRequest
    {
        public List<WhatsAppRecipient> Recipients { get; set; } = new();
        public string Message { get; set; } = "";
    }

    [HttpPost("send-bulk")]
    public async Task<IActionResult> SendBulk([FromBody] SendBulkRequest req)
    {
        if (req is null || req.Recipients is null || req.Recipients.Count == 0)
            return BadRequest(new { error = "recipients vacio" });
        var message = req.Message ?? "";
        List<WhatsAppSendResult> results;
        try
        {
            results = await _wa.SendBulkAsync(req.Recipients, message);
        }
        catch (Exception ex)
        {
            foreach (var r in req.Recipients)
            {
                var failed = new WhatsAppSendResult { Phone = r.Phone, Name = r.Name, Success = false, Message = ex.Message };
                await AuditSendAsync(r.Phone, r.Name, r.Message ?? message, failed);
            }
            throw;
        }
        foreach (var r in results)
        {
            var recipientMessage = req.Recipients.FirstOrDefault(x => x.Phone == r.Phone)?.Message ?? message;
            await AuditSendAsync(r.Phone, r.Name, recipientMessage, r);
        }
        return Ok(results);
    }
}
