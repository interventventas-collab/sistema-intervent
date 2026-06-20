using Twilio;
using Twilio.Rest.Api.V2010.Account;
using Twilio.Types;

namespace Api.Services;

/// <summary>
/// Envío de mensajes WhatsApp via Twilio (Sandbox o productivo).
/// Lee credenciales del .env: TWILIO_ACCOUNT_SID, TWILIO_AUTH_TOKEN, TWILIO_WHATSAPP_FROM.
/// </summary>
public class TwilioWhatsAppService
{
    private readonly IConfiguration _config;
    private readonly ILogger<TwilioWhatsAppService> _logger;
    private bool _initialized = false;

    public TwilioWhatsAppService(IConfiguration config, ILogger<TwilioWhatsAppService> logger)
    {
        _config = config;
        _logger = logger;
    }

    private string AccountSid => _config["TWILIO_ACCOUNT_SID"] ?? Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID") ?? "";
    private string AuthToken => _config["TWILIO_AUTH_TOKEN"] ?? Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN") ?? "";
    private string FromNumber => _config["TWILIO_WHATSAPP_FROM"] ?? Environment.GetEnvironmentVariable("TWILIO_WHATSAPP_FROM") ?? "whatsapp:+14155238886";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(AccountSid) && !string.IsNullOrWhiteSpace(AuthToken);

    private void EnsureInit()
    {
        if (_initialized) return;
        if (!IsConfigured) throw new InvalidOperationException("Twilio no configurado: faltan TWILIO_ACCOUNT_SID / TWILIO_AUTH_TOKEN en .env");
        TwilioClient.Init(AccountSid, AuthToken);
        _initialized = true;
    }

    /// <summary>Envía un mensaje de texto a un numero WhatsApp. Numero formato "whatsapp:+5491111..." o solo "+5491111..."</summary>
    public async Task<string> SendTextAsync(string to, string body)
    {
        EnsureInit();
        var toWa = to.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase) ? to : $"whatsapp:{to}";
        var msg = await MessageResource.CreateAsync(
            body: body,
            from: new PhoneNumber(FromNumber),
            to: new PhoneNumber(toWa));
        _logger.LogInformation("Twilio WhatsApp enviado a {To}: SID={Sid}", toWa, msg.Sid);
        return msg.Sid;
    }

    /// <summary>Envía un mensaje con un adjunto (PDF/imagen/etc) via Twilio.
    /// mediaUrl debe ser URL HTTPS publica (Twilio la descarga para enviar). Body es opcional (caption).</summary>
    public async Task<string> SendMediaAsync(string to, string mediaUrl, string? body = null)
    {
        EnsureInit();
        var toWa = to.StartsWith("whatsapp:", StringComparison.OrdinalIgnoreCase) ? to : $"whatsapp:{to}";
        var msg = await MessageResource.CreateAsync(
            body: body ?? "",
            from: new PhoneNumber(FromNumber),
            to: new PhoneNumber(toWa),
            mediaUrl: new List<Uri> { new Uri(mediaUrl) });
        _logger.LogInformation("Twilio WhatsApp con media enviado a {To}: SID={Sid}", toWa, msg.Sid);
        return msg.Sid;
    }
}
