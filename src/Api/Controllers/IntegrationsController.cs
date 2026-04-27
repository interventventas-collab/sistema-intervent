using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class IntegrationsController : ControllerBase
{
    private readonly IntegrationService _service;
    private readonly IHttpClientFactory _httpFactory;

    public IntegrationsController(IntegrationService service, IHttpClientFactory httpFactory)
    {
        _service = service;
        _httpFactory = httpFactory;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var integrations = await _service.GetAllAsync();
        return Ok(integrations);
    }

    [HttpGet("{provider}")]
    public async Task<IActionResult> GetByProvider(string provider)
    {
        var integration = await _service.GetByProviderAsync(provider);
        if (integration is null) return NotFound();
        return Ok(integration);
    }

    [HttpPost]
    public async Task<IActionResult> Save([FromBody] SaveIntegrationRequest request)
    {
        if (!ModelState.IsValid) return BadRequest(ModelState);
        var result = await _service.SaveAsync(request);
        return Ok(result);
    }

    [HttpGet("openai/models")]
    public async Task<IActionResult> GetOpenAiModels()
    {
        var integration = await _service.GetByProviderAsync("openai");
        if (integration is null || !integration.HasSecret)
            return BadRequest(new { error = "No hay API Key de OpenAI configurada" });

        // Get the actual secret from DB (not exposed via DTO)
        var secret = await _service.GetSecretAsync("openai");
        if (string.IsNullOrEmpty(secret))
            return BadRequest(new { error = "No hay API Key de OpenAI configurada" });

        try
        {
            var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", secret);
            var response = await http.GetAsync("https://api.openai.com/v1/models");

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                return BadRequest(new { error = $"Error de OpenAI ({response.StatusCode}): API Key invalida o sin permisos" });
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var models = new List<object>();

            foreach (var model in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = model.GetProperty("id").GetString() ?? "";
                // Filter only chat/completion models
                if (id.StartsWith("gpt-") || id.StartsWith("o1") || id.StartsWith("o3") || id.StartsWith("o4") || id.StartsWith("chatgpt"))
                {
                    models.Add(new { id });
                }
            }

            models = models.OrderBy(m => ((dynamic)m).id).ToList<object>();
            return Ok(models);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = "Error al conectar con OpenAI: " + ex.Message });
        }
    }

    [HttpGet("claude/models")]
    public async Task<IActionResult> GetClaudeModels()
    {
        var integration = await _service.GetByProviderAsync("claude");
        if (integration is null || !integration.HasSecret)
            return BadRequest(new { error = "No hay API Key de Claude configurada" });

        var secret = await _service.GetSecretAsync("claude");
        if (string.IsNullOrEmpty(secret))
            return BadRequest(new { error = "No hay API Key de Claude configurada" });

        try
        {
            var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Add("x-api-key", secret);
            http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
            var response = await http.GetAsync("https://api.anthropic.com/v1/models?limit=100");

            if (!response.IsSuccessStatusCode)
            {
                return BadRequest(new { error = $"Error de Anthropic ({response.StatusCode}): API Key invalida o sin permisos" });
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var models = new List<object>();

            foreach (var model in doc.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = model.GetProperty("id").GetString() ?? "";
                var displayName = model.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? id : id;
                models.Add(new { id, displayName });
            }

            models = models.OrderBy(m => ((dynamic)m).displayName).ToList<object>();
            return Ok(models);
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = "Error al conectar con Anthropic: " + ex.Message });
        }
    }

    [HttpPost("email-smtp/test")]
    public async Task<IActionResult> TestEmailSmtp()
    {
        var integration = await _service.GetByProviderAsync("email-smtp");
        if (integration is null)
            return BadRequest(new { error = "No hay configuracion de email" });

        var secret = await _service.GetSecretAsync("email-smtp");
        if (string.IsNullOrEmpty(secret))
            return BadRequest(new { error = "No hay contraseña configurada" });

        string smtpHost = "smtp.gmail.com";
        int smtpPort = 587;
        bool smtpTls = true;
        string fromAddress = "";
        string fromName = "";
        string username = "";

        if (!string.IsNullOrEmpty(integration.Settings))
        {
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(integration.Settings);
                var root = doc.RootElement;
                if (root.TryGetProperty("smtpHost", out var h)) smtpHost = h.GetString() ?? "smtp.gmail.com";
                if (root.TryGetProperty("smtpPort", out var p)) smtpPort = p.GetInt32();
                if (root.TryGetProperty("smtpTls", out var t)) smtpTls = t.GetBoolean();
                if (root.TryGetProperty("fromAddress", out var f)) fromAddress = f.GetString() ?? "";
                if (root.TryGetProperty("fromName", out var n)) fromName = n.GetString() ?? "";
                if (root.TryGetProperty("username", out var u)) username = u.GetString() ?? "";
            }
            catch { }
        }

        if (string.IsNullOrEmpty(fromAddress))
            return BadRequest(new { error = "No hay email de remitente configurado" });

        try
        {
            using var client = new System.Net.Mail.SmtpClient(smtpHost, smtpPort)
            {
                Credentials = new System.Net.NetworkCredential(
                    string.IsNullOrEmpty(username) ? fromAddress : username,
                    secret),
                EnableSsl = smtpTls,
                Timeout = 15000
            };

            var message = new System.Net.Mail.MailMessage
            {
                From = new System.Net.Mail.MailAddress(fromAddress, string.IsNullOrEmpty(fromName) ? fromAddress : fromName),
                Subject = "Email de prueba - Tu Marca",
                Body = "Este es un email de prueba enviado desde Tu Marca.\n\nSi recibiste este mensaje, la configuracion SMTP esta funcionando correctamente.",
                IsBodyHtml = false
            };
            message.To.Add(fromAddress);

            await client.SendMailAsync(message);
            return Ok(new { message = "Email de prueba enviado correctamente a " + fromAddress });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = "Error al enviar email: " + ex.Message });
        }
    }

    [HttpDelete("{provider}")]
    public async Task<IActionResult> Delete(string provider)
    {
        var deleted = await _service.DeleteAsync(provider);
        if (!deleted) return NotFound();
        return NoContent();
    }
}
