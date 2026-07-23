using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>2026-07-23 (Centro de Automatizaciones): despachador multi-canal de los avisos
/// programados. Recibe el contenido ya armado y lo reparte según la configuración de esa
/// automatización (Auto_Config): 🔔 campanita (fila en Mis_Alertas_Historial), 📲 Telegram
/// (por persona, a su ChatId), 📱 WhatsApp (por persona, si su ventana de 24hs está abierta)
/// y 📧 correo (SMTP de Integraciones "email-smtp"). Actualiza LastRun* al terminar.</summary>
public class AutoAvisoSender
{
    private readonly AppDbContext _db;
    private readonly TelegramService _tg;
    private readonly WhatsAppOutboundService _wa;
    private readonly IntegrationService _integration;
    private readonly ILogger<AutoAvisoSender> _log;

    public AutoAvisoSender(AppDbContext db, TelegramService tg, WhatsAppOutboundService wa,
        IntegrationService integration, ILogger<AutoAvisoSender> log)
    {
        _db = db; _tg = tg; _wa = wa; _integration = integration; _log = log;
    }

    /// <summary>Contenido de un aviso en sus variantes por canal (mismo mensaje, distinto formato).</summary>
    public record Contenido(string Titulo, string TelegramHtml, string WhatsAppTexto, string TextoPlano, IReadOnlyList<string>? TelegramExtra = null);

    public async Task<(bool Ok, string Detalle)> EnviarAsync(string autoKey, Contenido c, CancellationToken ct = default)
    {
        var cfg = await _db.AutoConfigs.FirstOrDefaultAsync(x => x.AutoKey == autoKey, ct);
        if (cfg is null) return (false, $"No existe la automatización '{autoKey}'");

        var personaIds = await _db.AutoDestinatarios.Where(d => d.AutoKey == autoKey)
            .Select(d => d.PersonaId).ToListAsync(ct);
        var personas = await _db.AutoPersonas
            .Where(p => p.Activo && personaIds.Contains(p.Id)).ToListAsync(ct);

        var partes = new List<string>();
        var okAlguno = false;

        // 🔔 Campanita: una fila en el historial de Mis Alertas (la ven todos en el sistema)
        if (cfg.CanalCampanita)
        {
            try
            {
                _db.MisAlertasHistorial.Add(new MisAlertaHistorial
                {
                    Tipo = "AUTOMATIZACION",
                    Mensaje = c.Titulo,
                    Detalle = c.TextoPlano.Length > 1500 ? c.TextoPlano[..1500] : c.TextoPlano,
                    Alcance = "admin,oficina",
                    PorTelegram = false,
                    CreatedAt = DateTime.UtcNow
                });
                await _db.SaveChangesAsync(ct);
                okAlguno = true; partes.Add("🔔 campanita OK");
            }
            catch (Exception ex) { _log.LogWarning(ex, "[AutoAviso:{Key}] campanita falló", autoKey); partes.Add("🔔 campanita falló"); }
        }

        // 📲 Telegram por persona
        if (cfg.CanalTelegram)
        {
            int ok = 0, tot = 0;
            foreach (var p in personas.Where(x => x.TelegramChatId is > 0))
            {
                tot++;
                var (enviado, _) = await _tg.SendMessageAsync(c.TelegramHtml, chatId: p.TelegramChatId, ct: ct, parseMode: "HTML");
                if (enviado) ok++;
                foreach (var extra in c.TelegramExtra ?? Array.Empty<string>())
                    await _tg.SendMessageAsync(extra, chatId: p.TelegramChatId, ct: ct, parseMode: "HTML");
            }
            if (tot > 0) { okAlguno |= ok > 0; partes.Add($"📲 Telegram {ok}/{tot}"); }
            else partes.Add("📲 Telegram: nadie con Telegram vinculado");
        }

        // 📱 WhatsApp por persona (sale si su ventana de 24hs está abierta; sino queda en el log)
        if (cfg.CanalWhatsApp)
        {
            int ok = 0, tot = 0;
            foreach (var p in personas.Where(x => !string.IsNullOrWhiteSpace(x.WhatsAppNumero)))
            {
                tot++;
                try
                {
                    var numero = p.WhatsAppNumero!.StartsWith("whatsapp:") ? p.WhatsAppNumero : "whatsapp:" + p.WhatsAppNumero;
                    var (sid, canal) = await _wa.SendTextAsync(numero, c.WhatsAppTexto);
                    if (sid != null)
                    {
                        ok++;
                        _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
                        {
                            Direccion = "OUTGOING", Numero = numero, Cuerpo = c.WhatsAppTexto,
                            TwilioMessageSid = sid, Canal = canal, Procesado = true, CreatedAt = DateTime.UtcNow
                        });
                        await _db.SaveChangesAsync(ct);
                    }
                    else _log.LogInformation("[AutoAviso:{Key}] WhatsApp a {Persona} no salió (¿ventana cerrada?)", autoKey, p.Nombre);
                }
                catch (Exception ex) { _log.LogWarning(ex, "[AutoAviso:{Key}] WhatsApp a {Persona} falló", autoKey, p.Nombre); }
            }
            if (tot > 0) { okAlguno |= ok > 0; partes.Add($"📱 WhatsApp {ok}/{tot}"); }
            else partes.Add("📱 WhatsApp: nadie con número cargado");
        }

        // 📧 Correo por persona
        if (cfg.CanalEmail)
        {
            int ok = 0, tot = 0;
            foreach (var p in personas.Where(x => !string.IsNullOrWhiteSpace(x.Email)))
            {
                tot++;
                if (await EnviarEmailAsync(p.Email!, c.Titulo, c.TextoPlano, ct)) ok++;
            }
            if (tot > 0) { okAlguno |= ok > 0; partes.Add($"📧 correo {ok}/{tot}"); }
            else partes.Add("📧 correo: nadie con mail cargado");
        }

        var detalle = partes.Count > 0 ? string.Join(" · ", partes) : "sin canales activos";

        cfg.LastRunAt = DateTime.UtcNow;
        cfg.LastRunOk = okAlguno;
        cfg.LastRunDetalle = detalle.Length > 300 ? detalle[..300] : detalle;
        await _db.SaveChangesAsync(ct);

        return (okAlguno, detalle);
    }

    /// <summary>Mail simple de texto (misma config SMTP de Integraciones que usan los comprobantes).</summary>
    private async Task<bool> EnviarEmailAsync(string to, string subject, string body, CancellationToken ct)
    {
        try
        {
            var integration = await _integration.GetByProviderAsync("email-smtp");
            var secret = await _integration.GetSecretAsync("email-smtp");
            if (integration is null || string.IsNullOrEmpty(secret)) { _log.LogInformation("[AutoAviso] sin SMTP configurado"); return false; }

            string smtpHost = "smtp.gmail.com"; int smtpPort = 587; bool smtpTls = true;
            string fromAddress = "", fromName = "", username = "";
            if (!string.IsNullOrEmpty(integration.Settings))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(integration.Settings);
                var root = doc.RootElement;
                if (root.TryGetProperty("smtpHost", out var h)) smtpHost = h.GetString() ?? smtpHost;
                if (root.TryGetProperty("smtpPort", out var pr)) smtpPort = pr.GetInt32();
                if (root.TryGetProperty("smtpTls", out var t)) smtpTls = t.GetBoolean();
                if (root.TryGetProperty("fromAddress", out var f)) fromAddress = f.GetString() ?? "";
                if (root.TryGetProperty("fromName", out var n)) fromName = n.GetString() ?? "";
                if (root.TryGetProperty("username", out var u)) username = u.GetString() ?? "";
            }
            if (string.IsNullOrEmpty(fromAddress)) return false;

            using var client = new System.Net.Mail.SmtpClient(smtpHost, smtpPort)
            {
                EnableSsl = smtpTls,
                Credentials = new System.Net.NetworkCredential(string.IsNullOrEmpty(username) ? fromAddress : username, secret)
            };
            using var message = new System.Net.Mail.MailMessage
            {
                From = new System.Net.Mail.MailAddress(fromAddress, string.IsNullOrEmpty(fromName) ? fromAddress : fromName),
                Subject = subject,
                Body = body
            };
            message.To.Add(to);
            await client.SendMailAsync(message, ct);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[AutoAviso] mail a {To} falló", to);
            return false;
        }
    }
}
