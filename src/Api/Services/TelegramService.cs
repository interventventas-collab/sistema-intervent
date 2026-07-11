using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Habla con la API de bots de Telegram (https://api.telegram.org/bot&lt;token&gt;/...).
/// Tres funciones:
///   1) Mandar mensajes de aviso al celu del dueño (ventas nuevas, alertas).
///   2) Probar la conexión (getMe) y vincular el chat del dueño (getUpdates).
///   3) Poll de mensajes entrantes: responder consultas simples ("ventas", "saldo", "alertas").
///
/// Sin secretos en .env: el token vive en la tabla TelegramAccounts. Llamadas HTTP con
/// IHttpClientFactory (mismo molde que MpSyncService/EzvizService).
/// </summary>
public class TelegramService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<TelegramService> _logger;

    private const string ApiBase = "https://api.telegram.org";
    private const int ARG_OFFSET_HOURS = -3;

    public TelegramService(AppDbContext db, IHttpClientFactory httpFactory, ILogger<TelegramService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private HttpClient NewClient(int timeoutSec = 30)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(timeoutSec);
        return http;
    }

    // ─────────────────────────── Mandar mensaje ───────────────────────────

    /// <summary>Manda un mensaje de texto al chat del dueño (o a un chat puntual). Devuelve (ok, error).</summary>
    public async Task<(bool ok, string? error)> SendMessageAsync(string text, long? chatId = null, CancellationToken ct = default)
    {
        var a = await _db.TelegramAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (a is null || string.IsNullOrWhiteSpace(a.BotToken)) return (false, "No hay bot de Telegram configurado");
        var target = chatId ?? a.ChatId;
        if (target is null || target == 0) return (false, "Todavía no está vinculado tu Telegram (escribile 'hola' al bot y probá de nuevo)");
        return await SendRawAsync(a.BotToken, target.Value, text, ct);
    }

    private async Task<(bool ok, string? error)> SendRawAsync(string token, long chatId, string text, CancellationToken ct)
    {
        try
        {
            var http = NewClient();
            var resp = await http.PostAsJsonAsync($"{ApiBase}/bot{token}/sendMessage",
                new { chat_id = chatId, text, disable_web_page_preview = true }, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var msg = ExtraerDescripcion(body) ?? $"Telegram respondió error {(int)resp.StatusCode}";
                return (false, msg);
            }
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Telegram] error enviando mensaje");
            return (false, "No se pudo conectar con Telegram: " + ex.Message);
        }
    }

    // ─────────────────────────── Probar / vincular ───────────────────────────

    /// <summary>Prueba el token (getMe), guarda el @usuario del bot, e intenta captar el chat del
    /// dueño (getUpdates). Si ya hay chat vinculado, manda un mensaje de prueba.</summary>
    public async Task<(bool ok, string? botUsername, long? chatId, bool testEnviado, string? error)> ProbarAsync(CancellationToken ct = default)
    {
        var a = await _db.TelegramAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (a is null || string.IsNullOrWhiteSpace(a.BotToken))
            return (false, null, null, false, "No hay token de bot cargado");

        // 1) getMe: validar token + nombre de usuario del bot.
        string? username;
        try
        {
            var http = NewClient();
            var resp = await http.GetAsync($"{ApiBase}/bot{a.BotToken}/getMe", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                var msg = ExtraerDescripcion(body) ?? "El token no parece válido. Revisalo en @BotFather.";
                await MarcarResultadoAsync(a, false, msg, ct);
                return (false, null, null, false, msg);
            }
            using var doc = JsonDocument.Parse(body);
            var result = doc.RootElement.GetProperty("result");
            username = result.TryGetProperty("username", out var u) ? u.GetString() : null;
        }
        catch (Exception ex)
        {
            var msg = "No se pudo conectar con Telegram: " + ex.Message;
            await MarcarResultadoAsync(a, false, msg, ct);
            return (false, null, null, false, msg);
        }

        a.BotUsername = username;

        // 2) Intentar captar el chat del dueño si todavía no está vinculado.
        if (a.ChatId is null)
            await IntentarCaptarChatAsync(a, ct);

        // 3) Si hay chat, mandar un mensaje de prueba.
        bool testEnviado = false;
        if (a.ChatId is not null)
        {
            var (okSend, _) = await SendRawAsync(a.BotToken, a.ChatId.Value,
                "✅ ¡Listo! Tu bot de Intervent quedó conectado. Te voy a avisar por acá las ventas nuevas y tus alertas. Escribime \"ayuda\" cuando quieras.", ct);
            testEnviado = okSend;
        }

        await MarcarResultadoAsync(a, true, null, ct);
        return (true, username, a.ChatId, testEnviado, null);
    }

    /// <summary>Vincula el chat del dueño mirando los últimos mensajes que le escribió al bot.</summary>
    public async Task<(bool ok, long? chatId, string? error)> DetectarChatAsync(CancellationToken ct = default)
    {
        var a = await _db.TelegramAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (a is null || string.IsNullOrWhiteSpace(a.BotToken))
            return (false, null, "No hay token de bot cargado");

        var captado = await IntentarCaptarChatAsync(a, ct);
        await _db.SaveChangesAsync(ct);
        if (!captado)
            return (false, null, "No encontré ningún mensaje tuyo. Abrí el bot en Telegram, escribile \"hola\" y volvé a probar.");
        return (true, a.ChatId, null);
    }

    /// <summary>Llama a getUpdates y toma el chat privado más reciente como chat del dueño. Además
    /// avanza el cursor (LastUpdateId) para no reprocesar esos mensajes en el poll. NO guarda —
    /// el llamador hace SaveChanges.</summary>
    private async Task<bool> IntentarCaptarChatAsync(TelegramAccount a, CancellationToken ct)
    {
        try
        {
            var http = NewClient();
            var resp = await http.GetAsync($"{ApiBase}/bot{a.BotToken}/getUpdates?limit=50", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return false;

            long? chat = null;
            long maxUpdateId = a.LastUpdateId ?? 0;
            foreach (var upd in result.EnumerateArray())
            {
                if (upd.TryGetProperty("update_id", out var uid) && uid.TryGetInt64(out var uidVal) && uidVal > maxUpdateId)
                    maxUpdateId = uidVal;
                if (upd.TryGetProperty("message", out var m) &&
                    m.TryGetProperty("chat", out var c) &&
                    c.TryGetProperty("type", out var t) && t.GetString() == "private" &&
                    c.TryGetProperty("id", out var cid) && cid.TryGetInt64(out var cidVal))
                {
                    chat = cidVal; // el más reciente gana (getUpdates viene en orden ascendente)
                }
            }
            if (maxUpdateId > 0) a.LastUpdateId = maxUpdateId;
            if (chat is not null) { a.ChatId = chat; return true; }
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Telegram] error en getUpdates (captar chat)");
            return false;
        }
    }

    private async Task MarcarResultadoAsync(TelegramAccount a, bool ok, string? error, CancellationToken ct)
    {
        a.LastSyncOk = ok;
        a.LastError = error is null ? null : (error.Length > 500 ? error.Substring(0, 500) : error);
        a.LastSyncAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);
    }

    // ─────────────────────────── Avisos de ventas nuevas ───────────────────────────

    /// <summary>Busca ventas nuevas de MeLi todavía no avisadas y manda un mensaje por cada una.
    /// Se apoya en la columna MeliOrders.NotifiedTelegram (0 = sin avisar). Deduplica por orden
    /// (una orden con varios items = un solo aviso). Solo mira ventas de las últimas 12h y limita
    /// a 15 por vuelta para no inundar; lo más viejo se marca como avisado sin mandar (anti-backlog).</summary>
    public async Task NotificarVentasPendientesAsync(CancellationToken ct = default)
    {
        var a = await _db.TelegramAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (a is null || string.IsNullOrWhiteSpace(a.BotToken) || !a.IsActive || !a.NotifVentas || a.ChatId is null)
            return;

        var corteViejo = DateTime.UtcNow.AddHours(-12);

        // Anti-backlog: cualquier venta sin avisar más vieja que 12h se marca como avisada y no se manda.
        var viejas = await _db.MeliOrders.Where(o => !o.NotifiedTelegram && o.DateCreated < corteViejo).ToListAsync(ct);
        if (viejas.Count > 0)
        {
            foreach (var o in viejas) o.NotifiedTelegram = true;
            await _db.SaveChangesAsync(ct);
        }

        // Ventas nuevas recientes sin avisar (paid/confirmadas), agrupadas por orden.
        var pendientes = await _db.MeliOrders
            .Where(o => !o.NotifiedTelegram && o.DateCreated >= corteViejo
                && (o.Status == "paid" || o.Status == "confirmed"))
            .OrderBy(o => o.DateCreated)
            .Include(o => o.MeliAccount)
            .ToListAsync(ct);
        if (pendientes.Count == 0) return;

        var porOrden = pendientes.GroupBy(o => o.MeliOrderId).ToList();
        int enviados = 0;
        foreach (var grupo in porOrden)
        {
            if (enviados >= 15) break; // el resto queda para la próxima vuelta
            var texto = ArmarMensajeVenta(grupo.ToList());
            var (ok, _) = await SendRawAsync(a.BotToken, a.ChatId.Value, texto, ct);
            if (!ok) break; // si falla el envío, no marcamos: reintenta la próxima
            foreach (var o in grupo) o.NotifiedTelegram = true;
            enviados++;
        }
        if (enviados > 0) await _db.SaveChangesAsync(ct);
    }

    private static string ArmarMensajeVenta(List<MeliOrder> items)
    {
        var first = items[0];
        var cuenta = first.MeliAccount?.Nickname;
        var total = items.Select(i => i.TotalAmount).FirstOrDefault();
        var comprador = string.IsNullOrWhiteSpace(first.BuyerNickname) ? "—" : first.BuyerNickname;

        var lineas = new List<string> { "🛒 ¡Venta nueva en MercadoLibre!" };
        foreach (var it in items.Take(6))
        {
            var titulo = string.IsNullOrWhiteSpace(it.ItemTitle) ? it.ItemId : it.ItemTitle;
            lineas.Add($"• {it.Quantity}x {titulo}");
        }
        if (items.Count > 6) lineas.Add($"• …y {items.Count - 6} producto(s) más");
        lineas.Add("");
        lineas.Add($"💵 Total: {Money(total)}");
        lineas.Add($"👤 Comprador: {comprador}");
        if (!string.IsNullOrWhiteSpace(cuenta)) lineas.Add($"🏷 Cuenta: {cuenta}");
        return string.Join("\n", lineas);
    }

    // ─────────────────────────── Poll de mensajes entrantes ───────────────────────────

    /// <summary>Una vuelta del poll: (1) avisa ventas pendientes, (2) hace long-poll de mensajes
    /// entrantes y responde consultas simples. Devuelve false si no hay bot activo (el caller
    /// espera más entre vueltas). Usa long-polling (timeout=25) para respuesta casi instantánea.</summary>
    public async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var a = await _db.TelegramAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (a is null || string.IsNullOrWhiteSpace(a.BotToken) || !a.IsActive)
            return false;

        // 1) Avisos de ventas pendientes (se chequea en cada vuelta ≈ cada ≤25s).
        try { await NotificarVentasPendientesAsync(ct); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Telegram] error avisando ventas"); }

        // 2) Long-poll de mensajes entrantes.
        // Si nunca poleamos (LastUpdateId null), adelantamos el cursor sin responder mensajes viejos.
        if (a.LastUpdateId is null)
        {
            await AdelantarCursorAsync(a, ct);
            await _db.SaveChangesAsync(ct);
            return true;
        }

        long offset = a.LastUpdateId.Value + 1;
        JsonDocument? doc = null;
        try
        {
            var http = NewClient(35);
            var resp = await http.GetAsync(
                $"{ApiBase}/bot{a.BotToken}/getUpdates?timeout=25&offset={offset}&allowed_updates=%5B%22message%22%5D", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return true;
            doc = JsonDocument.Parse(body);
        }
        catch (OperationCanceledException) { return true; }
        catch (Exception ex) { _logger.LogWarning(ex, "[Telegram] error en getUpdates (poll)"); return true; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return true;

            long maxUpdateId = a.LastUpdateId.Value;
            foreach (var upd in result.EnumerateArray())
            {
                if (upd.TryGetProperty("update_id", out var uid) && uid.TryGetInt64(out var uidVal) && uidVal > maxUpdateId)
                    maxUpdateId = uidVal;

                if (!upd.TryGetProperty("message", out var m)) continue;
                if (!m.TryGetProperty("chat", out var c) || !c.TryGetProperty("id", out var cid) || !cid.TryGetInt64(out var chatId))
                    continue;
                var texto = m.TryGetProperty("text", out var txt) ? txt.GetString() : null;
                if (string.IsNullOrWhiteSpace(texto)) continue;

                // Auto-vinculación: si todavía no hay dueño, el primero que escribe queda vinculado.
                if (a.ChatId is null) a.ChatId = chatId;

                // Solo respondemos al chat del dueño (seguridad: el bot no le hace caso a extraños).
                if (a.ChatId != chatId)
                {
                    await SendRawAsync(a.BotToken, chatId, "Este bot es privado del sistema Intervent. No estás autorizado.", ct);
                    continue;
                }

                var respuesta = await ResponderComandoAsync(texto, ct);
                await SendRawAsync(a.BotToken, chatId, respuesta, ct);
            }

            if (maxUpdateId > a.LastUpdateId.Value)
            {
                a.LastUpdateId = maxUpdateId;
                await _db.SaveChangesAsync(ct);
            }
            else if (_db.ChangeTracker.HasChanges())
            {
                await _db.SaveChangesAsync(ct);
            }
        }
        return true;
    }

    /// <summary>Trae el último update para fijar el cursor sin responder mensajes viejos.</summary>
    private async Task AdelantarCursorAsync(TelegramAccount a, CancellationToken ct)
    {
        try
        {
            var http = NewClient();
            var resp = await http.GetAsync($"{ApiBase}/bot{a.BotToken}/getUpdates?offset=-1&timeout=0", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) { a.LastUpdateId = 0; return; }
            using var doc = JsonDocument.Parse(body);
            long maxUpdateId = 0;
            if (doc.RootElement.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Array)
                foreach (var upd in result.EnumerateArray())
                    if (upd.TryGetProperty("update_id", out var uid) && uid.TryGetInt64(out var uidVal) && uidVal > maxUpdateId)
                        maxUpdateId = uidVal;
            a.LastUpdateId = maxUpdateId; // 0 si no hay nada pendiente
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Telegram] error adelantando cursor");
            a.LastUpdateId = 0;
        }
    }

    // ─────────────────────────── Respuestas a consultas ───────────────────────────

    private const string Ayuda =
        "🤖 Soy tu asistente de Intervent. Escribime una de estas:\n\n" +
        "• ventas — cuánto vendiste hoy en MercadoLibre\n" +
        "• saldo — la plata en Mercado Pago\n" +
        "• alertas — tus alertas que están saltando\n\n" +
        "Y te voy avisando solo las ventas nuevas y las alertas.";

    private async Task<string> ResponderComandoAsync(string textoRaw, CancellationToken ct)
    {
        var t = QuitarAcentos(textoRaw.Trim().ToLowerInvariant());

        if (t.StartsWith("/start") || t.Contains("hola") || t.Contains("ayuda") || t.Contains("menu") || t == "?")
            return Ayuda;
        if (t.Contains("venta"))
            return await ResumenVentasHoyAsync(ct);
        if (t.Contains("saldo") || t.Contains("mercado pago") || t == "mp" || t.Contains("plata") || t.Contains("dinero"))
            return await ResumenSaldoAsync(ct);
        if (t.Contains("alerta"))
            return await ResumenAlertasAsync(ct);

        return "No te entendí. 🙈\n\n" + Ayuda;
    }

    private async Task<string> ResumenVentasHoyAsync(CancellationToken ct)
    {
        var argNow = DateTime.UtcNow.AddHours(ARG_OFFSET_HOURS);
        // Medianoche de hoy en Argentina, expresada en UTC (ARG = UTC-3 → 00:00 ARG = 03:00 UTC).
        var startUtc = argNow.Date.AddHours(-ARG_OFFSET_HOURS);
        var endUtc = startUtc.AddDays(1);

        var filas = await _db.MeliOrders
            .Where(o => o.DateCreated >= startUtc && o.DateCreated < endUtc
                && (o.Status == "paid" || o.Status == "confirmed"))
            .Select(o => new { o.MeliOrderId, o.TotalAmount })
            .ToListAsync(ct);

        var porOrden = filas.GroupBy(o => o.MeliOrderId).Select(g => g.First().TotalAmount).ToList();
        var cant = porOrden.Count;
        var total = porOrden.Sum();

        if (cant == 0) return $"🛒 Hoy ({argNow:dd/MM}) todavía no hay ventas en MercadoLibre.";
        var plural = cant == 1 ? "venta" : "ventas";
        return $"🛒 Ventas de hoy ({argNow:dd/MM})\n\n{cant} {plural} · Total {Money(total)}";
    }

    private async Task<string> ResumenSaldoAsync(CancellationToken ct)
    {
        var mp = await _db.MpAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (mp is null) return "💳 Todavía no hay una cuenta de Mercado Pago conectada.";
        if (mp.LastSaldoDisponible is null && mp.LastSaldoTotal is null)
            return "💳 Mercado Pago está conectado pero todavía no leí el saldo.";

        var lineas = new List<string> { "💳 Mercado Pago" };
        if (mp.LastSaldoDisponible is not null) lineas.Add($"Disponible: {Money(mp.LastSaldoDisponible.Value)}");
        if (mp.LastSaldoTotal is not null) lineas.Add($"Total: {Money(mp.LastSaldoTotal.Value)}");
        if (mp.LastSaldoAt is not null)
            lineas.Add($"(último dato: {mp.LastSaldoAt.Value.AddHours(ARG_OFFSET_HOURS):dd/MM HH:mm})");
        return string.Join("\n", lineas);
    }

    private async Task<string> ResumenAlertasAsync(CancellationToken ct)
    {
        var disparadas = await _db.MisAlertas
            .Where(x => x.Activa && x.EstaDisparada)
            .OrderByDescending(x => x.DisparadaAt)
            .ToListAsync(ct);
        if (disparadas.Count == 0) return "🔔 No hay ninguna alerta saltando ahora mismo. Todo tranquilo. 👍";

        var lineas = new List<string> { $"🔔 Tenés {disparadas.Count} alerta(s) saltando:" };
        foreach (var al in disparadas.Take(10))
        {
            var msg = string.IsNullOrWhiteSpace(al.Mensaje) ? al.Tipo : al.Mensaje;
            lineas.Add(string.IsNullOrWhiteSpace(al.UltimoDetalle) ? $"• {msg}" : $"• {msg} — {al.UltimoDetalle}");
        }
        return string.Join("\n", lineas);
    }

    // ─────────────────────────── Helpers ───────────────────────────

    private static string? ExtraerDescripcion(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("description", out var d)) return d.GetString();
        }
        catch { }
        return null;
    }

    private static string Money(decimal v)
        => "$" + v.ToString("N0", CultureInfo.GetCultureInfo("es-AR"));

    private static string QuitarAcentos(string s)
    {
        var norm = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var ch in norm)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }
}
