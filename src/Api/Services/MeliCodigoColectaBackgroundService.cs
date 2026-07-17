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

        // ── Leer la casilla una sola vez y juntar dos cosas ──
        //   1) El código de autorización más reciente (mail "Código de autorización del día…").
        //   2) El horario de la colecta por día (sale de varios mails: "Detalle de la colecta…",
        //      "Tu colecta de X a Y está en camino", "El horario de tu colecta de mañana cambió",
        //      "No podremos recolectar… de X a Y" = cancelada).
        string? codigo = null; DateTime? fechaMail = null; string? messageId = null;
        // (dia ARG, horario "17 a 19 hs" o null si cancelada, cancelada, fecha del mail)
        var horarios = new List<(DateTime dia, string? horario, bool cancelada, DateTime mailAt)>();
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

                // 1) Código: el más nuevo con "El código es …".
                foreach (var s in sums.Where(s => EsMailDeCodigo(s.Envelope?.Subject)).OrderByDescending(s => s.InternalDate))
                {
                    if (ct.IsCancellationRequested) break;
                    var texto = await CuerpoTextoAsync(inbox, s.UniqueId, ct);
                    var m = string.IsNullOrWhiteSpace(texto) ? null : RxCodigo.Match(texto);
                    if (m is { Success: true })
                    {
                        codigo = m.Groups[1].Value.ToUpperInvariant();
                        fechaMail = s.InternalDate?.UtcDateTime;
                        messageId = s.Envelope?.MessageId?.Trim().Trim('<', '>');
                        break;
                    }
                }

                // 2) Horario: mails de colecta (no el del código). De más viejo a más nuevo, así el
                //    último (más reciente) es el que vale para cada día.
                foreach (var s in sums
                    .Where(s => EsMailDeColecta(s.Envelope?.Subject))
                    .OrderBy(s => s.InternalDate))
                {
                    if (ct.IsCancellationRequested) break;
                    var mailAt = s.InternalDate?.UtcDateTime ?? DateTime.UtcNow;
                    var asunto = s.Envelope?.Subject ?? "";
                    var esManana = ContieneManana(asunto);

                    // Intento sacar la franja del asunto; si no está, bajo el cuerpo.
                    var (found, franja, cancelada) = ExtraerHorario(asunto);
                    if (!found)
                    {
                        var body = await CuerpoTextoAsync(inbox, s.UniqueId, ct);
                        esManana = esManana || ContieneManana(body);
                        (found, franja, cancelada) = ExtraerHorario(body);
                    }
                    if (!found && !cancelada) continue;

                    var dia = mailAt.AddHours(ARG_OFFSET_HOURS).Date;
                    if (esManana) dia = dia.AddDays(1); // "…de mañana…" ⇒ es para el día siguiente
                    horarios.Add((dia, cancelada ? null : franja, cancelada, mailAt));
                }
            }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[CodigoColecta] no pude leer la casilla"); return; }
        finally { if (client is not null) { try { await client.DisconnectAsync(true, ct); } catch { } client.Dispose(); } }

        // ── Guardar el CÓDIGO (una fila por día, upsert) ──
        MeliCodigoColecta? filaCodigo = null;
        bool esNuevo = false, cambio = false;
        if (!string.IsNullOrWhiteSpace(codigo))
        {
            var argDia = ((fechaMail ?? DateTime.UtcNow).AddHours(ARG_OFFSET_HOURS)).Date;
            filaCodigo = await UpsertFilaAsync(db, argDia, ct);
            if (string.IsNullOrWhiteSpace(filaCodigo.Codigo)) esNuevo = true;
            else if (!string.Equals(filaCodigo.Codigo, codigo, StringComparison.OrdinalIgnoreCase)) cambio = true;
            if (esNuevo || cambio)
            {
                filaCodigo.Codigo = codigo; filaCodigo.FechaMail = fechaMail; filaCodigo.MessageId = messageId;
                filaCodigo.EnviadoTelegram = false; filaCodigo.UpdatedAt = DateTime.UtcNow;
            }
        }

        // ── Guardar los HORARIOS por día (el mail más reciente pisa al anterior) ──
        foreach (var h in horarios)
        {
            var fila = await UpsertFilaAsync(db, h.dia, ct);
            if (fila.HorarioMailAt is not null && fila.HorarioMailAt >= h.mailAt) continue; // ya tengo uno más nuevo
            fila.HorarioColecta = h.horario;
            fila.ColectaCancelada = h.cancelada;
            fila.HorarioMailAt = h.mailAt;
            fila.UpdatedAt = DateTime.UtcNow;
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);

        // ── Avisar por Telegram una sola vez por día (cuando llega/cambia el código) ──
        if (filaCodigo is not null && (esNuevo || cambio) && !filaCodigo.EnviadoTelegram)
        {
            try
            {
                var cuenta = await db.TelegramAccounts.Where(x => x.Proposito == "AVISOS").OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
                if (cuenta is not null && cuenta.IsActive && !string.IsNullOrEmpty(cuenta.BotToken)
                    && await db.TelegramChats.AnyAsync(c => c.TelegramAccountId == cuenta.Id && c.NotifAlertas, ct))
                {
                    var tg = scope.ServiceProvider.GetRequiredService<TelegramService>();
                    var horarioLinea = filaCodigo.ColectaCancelada
                        ? "\n🕒 Atención: la colecta de hoy figura CANCELADA."
                        : (!string.IsNullOrWhiteSpace(filaCodigo.HorarioColecta) ? $"\n🕒 Horario de hoy: {filaCodigo.HorarioColecta}." : "");
                    var texto =
                        $"🔑 Código de colecta/devolución de hoy ({filaCodigo.FechaCodigo:dd/MM}):\n\n" +
                        $"👉 {filaCodigo.Codigo}" + horarioLinea + "\n\n" +
                        "Usalo cuando venga el transporte a buscar los paquetes o a traerte una devolución.";
                    var (ok, _) = await tg.SendMessageAsync(texto, categoria: "ALERTAS", ct: ct);
                    if (ok) { filaCodigo.EnviadoTelegram = true; filaCodigo.UpdatedAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); }
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[CodigoColecta] no pude avisar por Telegram"); }
        }
    }

    /// <summary>Trae (o crea) la fila del día indicado y la deja trackeada por EF.</summary>
    private static async Task<MeliCodigoColecta> UpsertFilaAsync(AppDbContext db, DateTime dia, CancellationToken ct)
    {
        var fila = db.MeliCodigosColecta.Local.FirstOrDefault(x => x.FechaCodigo == dia)
                   ?? await db.MeliCodigosColecta.FirstOrDefaultAsync(x => x.FechaCodigo == dia, ct);
        if (fila is null)
        {
            fila = new MeliCodigoColecta { Codigo = "", FechaCodigo = dia, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.MeliCodigosColecta.Add(fila);
        }
        return fila;
    }

    /// <summary>Baja el cuerpo del mail como texto plano (si es HTML, le saca las etiquetas).</summary>
    private static async Task<string?> CuerpoTextoAsync(IMailFolder inbox, MailKit.UniqueId uid, CancellationToken ct)
    {
        try
        {
            var full = await inbox.GetMessageAsync(uid, ct);
            var texto = full.TextBody;
            if (string.IsNullOrWhiteSpace(texto) && !string.IsNullOrWhiteSpace(full.HtmlBody))
                texto = Regex.Replace(full.HtmlBody, "<[^>]+>", " ");
            return texto;
        }
        catch { return null; }
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

    /// <summary>¿Es un mail que da el horario de la colecta de UN día concreto? Lista blanca:
    /// solo los asuntos que sabemos que hablan de una colecta puntual. Así dejamos AFUERA mails
    /// como "Tu programación de colectas de la próxima semana está disponible" (que tiene varios
    /// horarios de días distintos y contaminaría el día de hoy).</summary>
    private static bool EsMailDeColecta(string? asunto)
    {
        if (string.IsNullOrWhiteSpace(asunto)) return false;
        var s = asunto;
        bool Tiene(string t) => s.Contains(t, StringComparison.OrdinalIgnoreCase);
        return (Tiene("en camino") && Tiene("colecta"))         // "Tu colecta de X a Y está en camino"
            || Tiene("detalle de la colecta")                    // "Detalle de la colecta del DD…"
            || Tiene("la colecta de hoy")                        // "La colecta de hoy de X a Y…"
            || Tiene("horario de tu colecta")                    // "El horario de tu colecta (de mañana) cambió"
            || Tiene("no podremos recolectar")                   // colecta cancelada
            || Tiene("no pudimos recolectar");
    }

    private static bool ContieneManana(string? texto)
        => !string.IsNullOrWhiteSpace(texto)
           && (texto.Contains("mañana", StringComparison.OrdinalIgnoreCase)
               || texto.Contains("manana", StringComparison.OrdinalIgnoreCase));

    // Franja horaria: "17:00 a 19:00", "12:19 a las 14:19", "11 a 13", "entre las 12:00 hs y las 14:00 hs".
    private static readonly Regex RxHorario = new(
        @"(?<h1>\d{1,2})(?::(?<m1>\d{2}))?\s*(?:hs)?\s*(?:a\s+las|a|y\s+las|y)\s+(?:las\s+)?(?<h2>\d{1,2})(?::(?<m2>\d{2}))?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Saca la franja horaria de un texto (asunto o cuerpo) y si la colecta está cancelada.
    /// Devuelve (encontróFranja, "17 a 19 hs", cancelada).</summary>
    private static (bool found, string? franja, bool cancelada) ExtraerHorario(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return (false, null, false);
        bool cancelada = texto.Contains("no podremos recolectar", StringComparison.OrdinalIgnoreCase)
                         || texto.Contains("no pudimos recolectar", StringComparison.OrdinalIgnoreCase);

        var m = RxHorario.Match(texto);
        if (!m.Success) return (false, null, cancelada);
        if (!int.TryParse(m.Groups["h1"].Value, out var h1) || !int.TryParse(m.Groups["h2"].Value, out var h2)
            || h1 > 23 || h2 > 23) return (false, null, cancelada);

        static string Fmt(int h, string min) => (string.IsNullOrEmpty(min) || min == "00") ? h.ToString() : $"{h}:{min}";
        var franja = $"{Fmt(h1, m.Groups["m1"].Value)} a {Fmt(h2, m.Groups["m2"].Value)} hs";
        return (true, franja, cancelada);
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
