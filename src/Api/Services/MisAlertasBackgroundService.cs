using System.Globalization;
using System.Text.Json;
using Api.Data;
using Api.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// Robot del motor de alertas configurables. Cada 2 minutos recorre las reglas activas de
/// "Mis Alertas" y evalua si se cumple la condicion de cada una. Cuando una se cumple por
/// primera vez, la marca como "disparada" (aparece en la campanita). Cuando la condicion deja
/// de cumplirse, la resetea, asi la proxima vez que se cumpla vuelve a avisar.
///
/// Incluye el tipo EMAIL_REMITENTE: vigila una casilla (IMAP, solo lectura) y dispara cuando
/// hay un correo NO leido de un remitente dado. El canal de aviso sigue siendo la campanita.
///
/// Mismo andamiaje que ShellAutoSyncBackgroundService (BackgroundService + while + Task.Delay
/// + scope por tick). Hora Argentina = UTC-3.
/// </summary>
public class MisAlertasBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MisAlertasBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private const int ARG_OFFSET_HOURS = -3;

    public MisAlertasBackgroundService(IServiceScopeFactory scopeFactory, ILogger<MisAlertasBackgroundService> logger)
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
            try { await TickAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Alertas] error en el ciclo (no critico)"); }
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Las alertas del sistema (VENTA_MELI / FICHADA) NO se evalúan acá: se disparan desde el evento
        // real (venta MeLi / fichada). Si las tocáramos, resetearíamos su campanita en cada vuelta.
        var reglas = await db.MisAlertas
            .Where(a => a.Activa && a.Tipo != "VENTA_MELI" && a.Tipo != "FICHADA")
            .ToListAsync();
        if (reglas.Count == 0) return;

        var argNow = DateTime.UtcNow.AddHours(ARG_OFFSET_HOURS);

        // Datos compartidos que se leen una sola vez por tick.
        decimal? shellSaldo = ParseMonto(
            (await db.Set<ShellAccount>().OrderByDescending(s => s.Id).FirstOrDefaultAsync())?.LastSaldo);

        decimal? bancoSaldo = (await db.CafeExtractoMovimientos
            .OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .FirstOrDefaultAsync())?.Saldo;

        // Abrir el correo una sola vez por tick, solo si hay alertas de tipo EMAIL_REMITENTE.
        ImapClient? imap = null;
        IMailFolder? inbox = null;
        if (reglas.Any(a => a.Tipo == "EMAIL_REMITENTE"))
        {
            try
            {
                var (host, port, user, pass) = await ResolverCredsCorreoAsync(db);
                (imap, inbox) = await AbrirCorreoAsync(host, port, user, pass);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[Alertas] no pude abrir la casilla de correo"); }
        }

        var changed = false;
        // Alertas que recién saltaron en este tick: se avisan por Telegram después de guardar.
        var reciecienDisparadas = new List<MisAlerta>();
        // Correos NUEVOS detectados este tick (uno por mail, con su alerta): generan aviso mail-por-mail.
        var nuevosCorreos = new List<(MisAlerta alerta, MisAlertaCorreo correo)>();
        try
        {
        foreach (var a in reglas)
        {
            var (met, detalle) = await EvaluarAsync(db, a, argNow, shellSaldo, bancoSaldo, inbox, nuevosCorreos);

            if (met && !a.EstaDisparada)
            {
                a.EstaDisparada = true;
                a.Vista = false;
                a.DisparadaAt = DateTime.UtcNow;
                a.UltimoDetalle = detalle;
                a.UpdatedAt = DateTime.UtcNow;
                changed = true;
                reciecienDisparadas.Add(a);
            }
            else if (met && a.EstaDisparada)
            {
                // Sigue disparada: solo refrescamos el detalle sin resetear "Vista".
                if (a.UltimoDetalle != detalle) { a.UltimoDetalle = detalle; a.UpdatedAt = DateTime.UtcNow; changed = true; }
            }
            else if (!met && a.EstaDisparada)
            {
                a.EstaDisparada = false;
                a.Vista = false;
                a.UltimoDetalle = null;
                a.UpdatedAt = DateTime.UtcNow;
                changed = true;
            }
        }

        // Guardo primero los cambios de estado (y los correos nuevos agregados a Mis_Alertas_Correos).
        if (changed || db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();

        // ── Historial de avisos + Telegram, de a UNO por evento ──
        // Dos fuentes de eventos:
        //   A) Cada CORREO nuevo que entró este tick (aunque la alerta ya estuviera disparada por otro mail).
        //   B) Cada alerta NO-correo que recién se disparó (transición apagada→prendida).
        // Cada evento = una fila en el historial + (si tiene Telegram) un mensaje al Telegram del dueño.
        var eventos = new List<(MisAlertaHistorial hist, string? tgTexto)>();

        // A) Correos nuevos → uno por mail.
        foreach (var (alerta, correo) in nuevosCorreos)
        {
            var rem = !string.IsNullOrWhiteSpace(correo.Remitente) ? correo.Remitente : correo.RemitenteEmail;
            var asunto = string.IsNullOrWhiteSpace(correo.Asunto) ? "(sin asunto)" : correo.Asunto;
            var detalle = string.IsNullOrWhiteSpace(rem) ? $"\"{asunto}\"" : $"De {rem} · \"{asunto}\"";
            var hist = NuevoHistorial(alerta, detalle, correo.RemitenteEmail, correo.GmailLink);
            var tgTexto = alerta.CanalTelegram
                ? $"📧 {(string.IsNullOrWhiteSpace(alerta.Mensaje) ? "Correo importante" : alerta.Mensaje)}\n{detalle}"
                : null;
            eventos.Add((hist, tgTexto));
        }

        // B) Alertas no-correo que recién saltaron.
        foreach (var a in reciecienDisparadas.Where(a => a.Tipo != "EMAIL_REMITENTE"))
        {
            var hist = NuevoHistorial(a, a.UltimoDetalle, null, null);
            var msg = string.IsNullOrWhiteSpace(a.Mensaje) ? a.Tipo : a.Mensaje;
            var tgTexto = a.CanalTelegram
                ? (string.IsNullOrWhiteSpace(a.UltimoDetalle) ? $"🔔 Alerta: {msg}" : $"🔔 Alerta: {msg}\n{a.UltimoDetalle}")
                : null;
            eventos.Add((hist, tgTexto));
        }

        if (eventos.Count > 0)
        {
            // Bot de Telegram: lo resuelvo una sola vez si hay al menos un evento que lo pida.
            TelegramService? tg = null;
            if (eventos.Any(e => e.tgTexto is not null))
            {
                try
                {
                    var cuenta = await db.TelegramAccounts.Where(x => x.Proposito == "AVISOS").OrderBy(x => x.Id).FirstOrDefaultAsync();
                    // 2026-07-16: multi-persona — hay destino si al menos una persona vinculada recibe alertas.
                    if (cuenta is not null && cuenta.IsActive && !string.IsNullOrEmpty(cuenta.BotToken)
                        && await db.TelegramChats.AnyAsync(c => c.TelegramAccountId == cuenta.Id && c.NotifAlertas))
                        tg = scope.ServiceProvider.GetRequiredService<TelegramService>();
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[Alertas] no pude resolver el bot de Telegram"); }
            }

            foreach (var (hist, tgTexto) in eventos)
            {
                db.Set<MisAlertaHistorial>().Add(hist);
                if (tgTexto is not null && tg is not null)
                {
                    try
                    {
                        var (ok, _) = await tg.SendMessageAsync(tgTexto, categoria: "ALERTAS");
                        hist.EnviadoTelegram = ok;
                    }
                    catch (Exception ex) { _logger.LogWarning(ex, "[Alertas] no pude avisar por Telegram"); }
                }
            }
            await db.SaveChangesAsync();
        }

        // Limpieza: el historial guarda los últimos 90 días (evita que crezca sin fin).
        try
        {
            var limite = DateTime.UtcNow.AddDays(-90);
            await db.Set<MisAlertaHistorial>().Where(h => h.CreatedAt < limite).ExecuteDeleteAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Alertas] no pude limpiar historial viejo"); }
        }
        finally
        {
            if (imap is not null)
            {
                try { await imap.DisconnectAsync(true); } catch { }
                imap.Dispose();
            }
        }
    }

    private async Task<(bool met, string? detalle)> EvaluarAsync(
        AppDbContext db, MisAlerta a, DateTime argNow, decimal? shellSaldo, decimal? bancoSaldo, IMailFolder? emailInbox,
        List<(MisAlerta alerta, MisAlertaCorreo correo)> nuevosCorreosSink)
    {
        switch (a.Tipo)
        {
            case "SHELL_BAJO":
            {
                if (shellSaldo is null || a.Umbral is null) return (false, null);
                if (shellSaldo.Value < a.Umbral.Value)
                    return (true, $"Shell Flota: {Money(shellSaldo.Value)}");
                return (false, null);
            }
            case "BANCO_BAJO":
            {
                if (bancoSaldo is null || a.Umbral is null) return (false, null);
                if (bancoSaldo.Value < a.Umbral.Value)
                    return (true, $"Banco Galicia: {Money(bancoSaldo.Value)}");
                return (false, null);
            }
            case "CHEQUE_VENCE":
            {
                var dias = (int)(a.Umbral ?? 0);
                if (dias < 0) dias = 0;
                var hoy = argNow.Date;
                var limite = hoy.AddDays(dias);
                var q = db.Set<CafeChequeBanco>().Where(c =>
                    c.Tipo == "EMITIDO" && c.Estado != "Pagado" &&
                    c.FechaPago != null && c.FechaPago >= hoy && c.FechaPago <= limite);
                var cant = await q.CountAsync();
                if (cant == 0) return (false, null);
                var total = await q.SumAsync(c => c.Importe);
                var prox = await q.MinAsync(c => c.FechaPago);
                var plural = cant == 1 ? "cheque" : "cheques";
                return (true, $"{cant} {plural} por {Money(total)} (el más próximo {prox:dd/MM})");
            }
            case "FECHA_MES":
            {
                var dia = (int)(a.Umbral ?? 0);
                if (dia < 1) return (false, null);
                var diasEnMes = DateTime.DaysInMonth(argNow.Year, argNow.Month);
                var diaEfectivo = Math.Min(dia, diasEnMes); // si pusiste 31 y el mes tiene 30, dispara el 30.
                if (argNow.Day == diaEfectivo)
                    return (true, $"Hoy {argNow:dd/MM}");
                return (false, null);
            }
            case "EMAIL_REMITENTE":
            {
                if (emailInbox is null || string.IsNullOrWhiteSpace(a.TextoParam)) return (false, null);
                // Uno o VARIOS remitentes (separados por coma, punto y coma o salto de línea).
                var remitentes = a.TextoParam
                    .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (remitentes.Count == 0) return (false, null);

                // De = (remitente1 OR remitente2 OR ...)
                SearchQuery deQuien = SearchQuery.FromContains(remitentes[0]);
                for (int i = 1; i < remitentes.Count; i++)
                    deQuien = deQuien.Or(SearchQuery.FromContains(remitentes[i]));

                var uids = await emailInbox.SearchAsync(SearchQuery.NotSeen.And(deQuien));

                // Solo los que LLEGARON después de crear la alerta (para no avisar de correos viejos
                // sin leer). "Disparada" mientras siga sin leer; se apaga cuando lo abrís en Gmail.
                List<IMessageSummary> nuevos = new();
                if (uids.Count > 0)
                {
                    var sums = await emailInbox.FetchAsync(uids,
                        MessageSummaryItems.Envelope | MessageSummaryItems.InternalDate | MessageSummaryItems.UniqueId);
                    nuevos = sums
                        .Where(s => (s.InternalDate?.UtcDateTime ?? DateTime.MaxValue) >= a.CreatedAt)
                        .OrderByDescending(s => s.InternalDate)
                        .ToList();
                }

                // Sincroniza la tabla de correos para la card del Dashboard (agrega nuevos, borra leídos).
                // Devuelve SOLO los mails que se agregaron ahora (no los que ya teníamos): esos generan
                // el aviso mail-por-mail (historial + Telegram).
                var agregados = await PersistirCorreosAsync(db, a, emailInbox, nuevos);
                foreach (var c in agregados) nuevosCorreosSink.Add((a, c));

                if (nuevos.Count == 0) return (false, null);

                var topEnv = nuevos[0].Envelope;
                var asunto = string.IsNullOrWhiteSpace(topEnv?.Subject) ? "(sin asunto)" : topEnv!.Subject;
                var mb = topEnv?.From?.Mailboxes?.FirstOrDefault();
                var remitente = mb is null ? "" : (!string.IsNullOrWhiteSpace(mb.Name) ? mb.Name : (mb.Address ?? ""));

                if (nuevos.Count == 1)
                    return (true, string.IsNullOrWhiteSpace(remitente) ? $"\"{asunto}\"" : $"De {remitente} · \"{asunto}\"");
                var ultimo = string.IsNullOrWhiteSpace(remitente) ? $"\"{asunto}\"" : $"{remitente}: \"{asunto}\"";
                return (true, $"{nuevos.Count} correos nuevos · último {ultimo}");
            }
            default:
                return (false, null);
        }
    }

    /// <summary>Abre la casilla de correo a vigilar (IMAP, SOLO LECTURA — no marca leídos ni borra).
    /// Credenciales guardadas en AppSettings (alertas.imap.*), cargadas por el usuario desde la
    /// pantalla Mis Alertas. Si falta usuario o clave, devuelve null y las alertas de correo no disparan.</summary>
    /// <summary>Resuelve la casilla a vigilar. 1) Si el usuario cargó una casilla propia en
    /// AppSettings (alertas.imap.*), usa esa. 2) Si no, reutiliza la casilla YA conectada del
    /// sistema (integración email-smtp) — la misma clave de app de Gmail sirve para IMAP.</summary>
    private static async Task<(string? host, int port, string? user, string? pass)> ResolverCredsCorreoAsync(AppDbContext db)
    {
        // 1) Config propia de alertas (override manual), si está completa.
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

        // 2) Reutilizar la casilla ya conectada (email-smtp).
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

    /// <summary>Mantiene en sincronía la tabla Mis_Alertas_Correos con los mails sin leer que matchean
    /// la alerta: agrega los nuevos (bajando remitente, asunto, adelanto del cuerpo y adjuntos) y borra
    /// los que ya no están (leídos en Gmail).</summary>
    private static async Task<List<MisAlertaCorreo>> PersistirCorreosAsync(AppDbContext db, MisAlerta a, IMailFolder inbox, List<IMessageSummary> nuevos)
    {
        var agregados = new List<MisAlertaCorreo>();
        static string NormId(IMessageSummary s)
        {
            var mid = s.Envelope?.MessageId;
            if (!string.IsNullOrWhiteSpace(mid)) return mid.Trim().Trim('<', '>');
            return "uid:" + s.UniqueId.Id;
        }

        var currentIds = nuevos.Select(NormId).ToHashSet();
        var existentes = await db.Set<MisAlertaCorreo>().Where(c => c.AlertaId == a.Id).ToListAsync();

        var aBorrar = existentes.Where(c => !currentIds.Contains(c.MessageId)).ToList();
        if (aBorrar.Count > 0) db.Set<MisAlertaCorreo>().RemoveRange(aBorrar);
        var yaGuardados = existentes.Select(c => c.MessageId).ToHashSet();

        foreach (var s in nuevos)
        {
            var id = NormId(s);
            if (yaGuardados.Contains(id)) continue; // ya lo tenemos: no re-bajamos el cuerpo

            var mb = s.Envelope?.From?.Mailboxes?.FirstOrDefault();
            var remNombre = mb is null ? null : (!string.IsNullOrWhiteSpace(mb.Name) ? mb.Name : mb.Address);

            string? adelanto = null; bool tieneAdj = false; string? adjNombres = null;
            try
            {
                var full = await inbox.GetMessageAsync(s.UniqueId);
                var texto = full.TextBody;
                if (string.IsNullOrWhiteSpace(texto) && !string.IsNullOrWhiteSpace(full.HtmlBody))
                    texto = System.Text.RegularExpressions.Regex.Replace(full.HtmlBody, "<[^>]+>", " ");
                adelanto = LimpiarTexto(texto, 1000);
                var adj = full.Attachments.OfType<MimePart>()
                    .Select(p => p.FileName).Where(f => !string.IsNullOrWhiteSpace(f)).ToList();
                tieneAdj = adj.Count > 0;
                if (tieneAdj) adjNombres = Recortar(string.Join(", ", adj), 500);
            }
            catch { /* si no puedo bajar el cuerpo, igual guardo el resto */ }

            string? link = !id.StartsWith("uid:")
                ? "https://mail.google.com/mail/u/0/#search/rfc822msgid:" + Uri.EscapeDataString(id)
                : (string.IsNullOrWhiteSpace(mb?.Address) ? null : "https://mail.google.com/mail/u/0/#search/from:" + Uri.EscapeDataString(mb!.Address));

            var nuevo = new MisAlertaCorreo
            {
                AlertaId = a.Id,
                MessageId = id,
                Remitente = Recortar(remNombre, 300),
                RemitenteEmail = Recortar(mb?.Address, 300),
                Asunto = Recortar(s.Envelope?.Subject, 500),
                Adelanto = adelanto,
                FechaRecibido = s.InternalDate?.UtcDateTime,
                TieneAdjuntos = tieneAdj,
                Adjuntos = adjNombres,
                GmailLink = Recortar(link, 800)
            };
            db.Set<MisAlertaCorreo>().Add(nuevo);
            agregados.Add(nuevo);
        }
        return agregados;
    }

    /// <summary>Arma una fila de historial (bitácora de avisos) con snapshot de la alerta.</summary>
    private static MisAlertaHistorial NuevoHistorial(MisAlerta a, string? detalle, string? remitenteEmail, string? gmailLink)
        => new()
        {
            AlertaId = a.Id,
            Tipo = a.Tipo,
            Mensaje = Recortar(a.Mensaje, 300) ?? a.Tipo,
            Detalle = Recortar(detalle, 500),
            Alcance = string.IsNullOrWhiteSpace(a.Alcance) ? "admin,oficina" : a.Alcance,
            RemitenteEmail = Recortar(remitenteEmail, 300),
            GmailLink = Recortar(gmailLink, 800),
            PorTelegram = a.CanalTelegram
        };

    private static string? LimpiarTexto(string? t, int max)
    {
        if (string.IsNullOrWhiteSpace(t)) return null;
        t = System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > max ? t.Substring(0, max) : t;
    }

    private static string? Recortar(string? t, int max)
        => string.IsNullOrEmpty(t) ? t : (t.Length > max ? t.Substring(0, max) : t);

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

    /// <summary>Convierte el saldo de Shell (texto scrapeado, formato argentino "$ 1.234,56")
    /// a decimal. Devuelve null si no se puede interpretar.</summary>
    private static decimal? ParseMonto(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Dejar solo digitos, coma, punto y signo menos.
        var s = new string(raw.Where(c => char.IsDigit(c) || c == ',' || c == '.' || c == '-').ToArray());
        if (string.IsNullOrEmpty(s)) return null;

        // Robusto ante formato inglés ("196,988.75") y argentino ("196.988,75"):
        // el separador que aparece MÁS a la derecha es el decimal; el otro son miles.
        int lastDot = s.LastIndexOf('.');
        int lastComma = s.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            if (lastDot > lastComma) s = s.Replace(",", "");          // inglés: 196,988.75 -> 196988.75
            else s = s.Replace(".", "").Replace(",", ".");            // argentino: 196.988,75 -> 196988.75
        }
        else if (lastComma >= 0)
        {
            // Solo coma: decimal si tiene 1-2 dígitos detrás; si no, son miles.
            var dec = s.Length - lastComma - 1;
            s = (dec >= 1 && dec <= 2) ? s.Replace(",", ".") : s.Replace(",", "");
        }
        else if (lastDot >= 0)
        {
            // Solo punto: decimal si tiene 1-2 dígitos detrás; si no, son miles.
            var dec = s.Length - lastDot - 1;
            if (!(dec >= 1 && dec <= 2)) s = s.Replace(".", "");
        }

        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string Money(decimal v)
        => "$" + v.ToString("N0", CultureInfo.GetCultureInfo("es-AR"));
}
