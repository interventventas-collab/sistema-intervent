using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// 2026-07-17: Robot que trae el "Código de autorización del día para colectas o devoluciones" que
/// MercadoLibre manda por mail todas las mañanas. Ese código lo pide el transporte cuando viene a
/// buscar los paquetes (colecta) o a traer una devolución, y NO está expuesto en la API de MeLi
/// (es una medida de seguridad): la única vía automática es leerlo del correo.
///
/// Cada 15 minutos abre la casilla (IMAP, SOLO LECTURA — no marca leídos ni borra nada), busca el
/// mail más reciente de MercadoLibre con ese asunto, saca el código con una expresión regular y
/// guarda UNA fila por día en Meli_CodigoColecta. La primera vez que aparece el código de un día
/// nuevo, lo avisa por Telegram (categoría ALERTAS). El Dashboard muestra el más reciente.
///
/// Reutiliza la MISMA casilla ya conectada para las alertas de correo (config alertas.imap.* o la
/// integración email-smtp), así no hay que configurar nada nuevo. Mismo molde que
/// MisAlertasBackgroundService (BackgroundService + while + Task.Delay + scope por tick). Hora ARG = UTC-3.
/// </summary>
public class MeliCodigoColectaBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliCodigoColectaBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private const int ARG_OFFSET_HOURS = -3;

    // "El código es DF54B074." — tolerante a acento y a mayúsculas/minúsculas.
    private static readonly Regex RxCodigo = new(
        @"El\s+c[oó]digo\s+es\s*[:]?\s*([A-Za-z0-9]{6,12})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public MeliCodigoColectaBackgroundService(IServiceScopeFactory scopeFactory, ILogger<MeliCodigoColectaBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogWarning(ex, "[CodigoColecta] error en el ciclo (no critico)"); }
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var (host, port, user, pass) = await ResolverCredsCorreoAsync(db);
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            return; // sin casilla conectada, no hay de dónde leerlo

        // ── Buscar el mail más reciente de MeLi con ese asunto (últimos 4 días) ──
        string? codigo = null; DateTime? fechaMail = null; string? messageId = null;
        ImapClient? client = null;
        try
        {
            (client, var inbox) = await AbrirCorreoAsync(host, port, user, pass);
            if (client is null || inbox is null) return;

            // Buscamos por remitente + fecha (el asunto lleva acentos y no siempre matchea bien en IMAP);
            // el filtro fino del asunto lo hacemos en memoria. No usamos NotSeen: el mail puede estar leído.
            var desde = DateTime.UtcNow.AddDays(-4);
            var query = SearchQuery.FromContains("mercadolibre").And(SearchQuery.DeliveredAfter(desde));
            var uids = await inbox.SearchAsync(query, ct);
            if (uids.Count > 0)
            {
                var sums = await inbox.FetchAsync(uids,
                    MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.UniqueId, ct);

                // Solo los que son del código de autorización, el más nuevo primero.
                var candidatos = sums
                    .Where(s => EsMailDeCodigo(s.Envelope?.Subject))
                    .OrderByDescending(s => s.InternalDate)
                    .ToList();

                foreach (var s in candidatos)
                {
                    if (ct.IsCancellationRequested) break;
                    var full = await inbox.GetMessageAsync(s.UniqueId, ct);
                    var texto = full.TextBody;
                    if (string.IsNullOrWhiteSpace(texto) && !string.IsNullOrWhiteSpace(full.HtmlBody))
                        texto = Regex.Replace(full.HtmlBody, "<[^>]+>", " ");
                    var m = string.IsNullOrWhiteSpace(texto) ? null : RxCodigo.Match(texto);
                    if (m is { Success: true })
                    {
                        codigo = m.Groups[1].Value.ToUpperInvariant();
                        fechaMail = s.InternalDate?.UtcDateTime;
                        messageId = s.Envelope?.MessageId?.Trim().Trim('<', '>');
                        break; // ya tenemos el más reciente con código
                    }
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[CodigoColecta] no pude leer la casilla"); return; }
        finally { if (client is not null) { try { await client.DisconnectAsync(true, ct); } catch { } client.Dispose(); } }

        if (string.IsNullOrWhiteSpace(codigo)) return;

        // Día (hora ARG) al que corresponde: el del mail; si no lo tengo, hoy.
        var argDia = ((fechaMail ?? DateTime.UtcNow).AddHours(ARG_OFFSET_HOURS)).Date;

        // ── Guardar UNA fila por día (upsert) ──
        var fila = await db.MeliCodigosColecta.FirstOrDefaultAsync(x => x.FechaCodigo == argDia, ct);
        bool esNuevo = false, cambio = false;
        if (fila is null)
        {
            fila = new MeliCodigoColecta
            {
                Codigo = codigo, FechaCodigo = argDia, FechaMail = fechaMail, MessageId = messageId,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            db.MeliCodigosColecta.Add(fila);
            esNuevo = true;
        }
        else if (!string.Equals(fila.Codigo, codigo, StringComparison.OrdinalIgnoreCase))
        {
            fila.Codigo = codigo; fila.FechaMail = fechaMail; fila.MessageId = messageId;
            fila.EnviadoTelegram = false; // código corregido: vale re-avisar
            fila.UpdatedAt = DateTime.UtcNow;
            cambio = true;
        }
        if (esNuevo || cambio) await db.SaveChangesAsync(ct);

        // ── Avisar por Telegram una sola vez por día ──
        if ((esNuevo || cambio) && !fila.EnviadoTelegram)
        {
            try
            {
                var cuenta = await db.TelegramAccounts.Where(x => x.Proposito == "AVISOS").OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
                if (cuenta is not null && cuenta.IsActive && !string.IsNullOrEmpty(cuenta.BotToken)
                    && await db.TelegramChats.AnyAsync(c => c.TelegramAccountId == cuenta.Id && c.NotifAlertas, ct))
                {
                    var tg = scope.ServiceProvider.GetRequiredService<TelegramService>();
                    var texto =
                        $"🔑 Código de colecta/devolución de hoy ({argDia:dd/MM}):\n\n" +
                        $"👉 {fila.Codigo}\n\n" +
                        "Usalo cuando venga el transporte a buscar los paquetes o a traerte una devolución.";
                    var (ok, _) = await tg.SendMessageAsync(texto, categoria: "ALERTAS", ct: ct);
                    if (ok) { fila.EnviadoTelegram = true; fila.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[CodigoColecta] no pude avisar por Telegram"); }
        }
    }

    /// <summary>¿El asunto es el del código de autorización? Tolerante a acentos: comparamos por
    /// pedazos sin la primera letra acentuada ("utorizaci") o por "colectas o devoluciones".</summary>
    private static bool EsMailDeCodigo(string? asunto)
    {
        if (string.IsNullOrWhiteSpace(asunto)) return false;
        var s = asunto;
        return s.Contains("utorizaci", StringComparison.OrdinalIgnoreCase)
            && (s.Contains("colecta", StringComparison.OrdinalIgnoreCase)
                || s.Contains("devolucion", StringComparison.OrdinalIgnoreCase)
                || s.Contains("devoluci", StringComparison.OrdinalIgnoreCase));
    }

    // ─────────────────────────── Casilla de correo ───────────────────────────
    // Misma resolución que MisAlertasBackgroundService: 1) config propia alertas.imap.*; si está
    // completa la usa. 2) si no, reutiliza la casilla ya conectada (integración email-smtp).

    private static async Task<(string? host, int port, string? user, string? pass)> ResolverCredsCorreoAsync(AppDbContext db)
    {
        var cfg = await db.AppSettings
            .Where(s => s.Key.StartsWith("alertas.imap."))
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        cfg.TryGetValue("alertas.imap.user", out var user);
        cfg.TryGetValue("alertas.imap.pass", out var pass);
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
        {
            cfg.TryGetValue("alertas.imap.host", out var h);
            cfg.TryGetValue("alertas.imap.port", out var pStr);
            int.TryParse(pStr, out var pr);
            return (h, pr, user, pass);
        }

        var integ = await db.Set<Integration>().FirstOrDefaultAsync(x => x.Provider == "email-smtp");
        if (integ is null || string.IsNullOrWhiteSpace(integ.AppSecret)) return (null, 0, null, null);
        string? iUser = null, iHost = null; int iPort = 993;
        if (!string.IsNullOrWhiteSpace(integ.Settings))
        {
            try
            {
                using var doc = JsonDocument.Parse(integ.Settings);
                var root = doc.RootElement;
                if (root.TryGetProperty("username", out var u) && !string.IsNullOrWhiteSpace(u.GetString())) iUser = u.GetString();
                else if (root.TryGetProperty("fromAddress", out var f)) iUser = f.GetString();
                if (root.TryGetProperty("imapHost", out var ih)) iHost = ih.GetString();
                if (root.TryGetProperty("imapPort", out var ip) && ip.TryGetInt32(out var ipv)) iPort = ipv;
            }
            catch { }
        }
        return (iHost, iPort, iUser, integ.AppSecret);
    }

    private static async Task<(ImapClient? client, IMailFolder? inbox)> AbrirCorreoAsync(string? host, int port, string? user, string? pass)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass)) return (null, null);
        if (string.IsNullOrWhiteSpace(host)) host = "imap.gmail.com";
        if (port <= 0) port = 993;

        var client = new ImapClient();
        await client.ConnectAsync(host, port, SecureSocketOptions.SslOnConnect);
        await client.AuthenticateAsync(user, pass);
        var inbox = client.Inbox;
        await inbox.OpenAsync(FolderAccess.ReadOnly);
        return (client, inbox);
    }
}
