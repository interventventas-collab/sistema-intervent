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

    /// <summary>Manda un mensaje de texto por el bot de AVISOS al chat del dueño. Devuelve (ok, error).</summary>
    public async Task<(bool ok, string? error)> SendMessageAsync(string text, long? chatId = null, CancellationToken ct = default)
    {
        var a = await GetBotAsync("AVISOS", ct);
        if (a is null || string.IsNullOrWhiteSpace(a.BotToken)) return (false, "No hay bot de Telegram configurado");
        var target = chatId ?? a.ChatId;
        if (target is null || target == 0) return (false, "Todavía no está vinculado tu Telegram (escribile 'hola' al bot y probá de nuevo)");
        return await SendRawAsync(a.BotToken, target.Value, text, ct);
    }

    /// <summary>Devuelve el bot de un propósito ("AVISOS" o "PREVENTAS"), o null si no existe.</summary>
    private async Task<TelegramAccount?> GetBotAsync(string proposito, CancellationToken ct)
        => await _db.TelegramAccounts.Where(x => x.Proposito == proposito).OrderBy(x => x.Id).FirstOrDefaultAsync(ct);

    // Teclados de Telegram (botones) para la conversación de preventa.
    private static object TecladoBotones(IEnumerable<string> opciones)
        => new { keyboard = opciones.Select(o => new[] { o }).ToArray(), resize_keyboard = true, one_time_keyboard = true };
    private static readonly object QuitarTeclado = new { remove_keyboard = true };

    // Botones "inline" (pegados al mensaje) con un dato oculto (callback_data). Los usamos para
    // elegir el cliente: cada botón lleva el id del cliente, así no hay ambigüedad al tocar.
    private static object TecladoInline(IEnumerable<(string text, string data)> items)
        => new { inline_keyboard = items.Select(i => new object[] { new { text = i.text, callback_data = i.data } }).ToArray() };

    private async Task AnswerCallbackAsync(string token, string? callbackId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(callbackId)) return;
        try { var http = NewClient(); await http.PostAsJsonAsync($"{ApiBase}/bot{token}/answerCallbackQuery", new { callback_query_id = callbackId }, ct); }
        catch { }
    }

    private async Task<(bool ok, string? error)> SendRawAsync(string token, long chatId, string text, CancellationToken ct, object? replyMarkup = null)
    {
        try
        {
            var http = NewClient();
            object payload = replyMarkup is null
                ? new { chat_id = chatId, text, disable_web_page_preview = true }
                : new { chat_id = chatId, text, disable_web_page_preview = true, reply_markup = replyMarkup };
            var resp = await http.PostAsJsonAsync($"{ApiBase}/bot{token}/sendMessage", payload, ct);
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
    public async Task<(bool ok, string? botUsername, long? chatId, bool testEnviado, string? error)> ProbarAsync(string proposito = "AVISOS", CancellationToken ct = default)
    {
        var a = await GetBotAsync(proposito, ct);
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
            var mensajePrueba = a.Proposito == "PREVENTAS"
                ? "✅ ¡Listo! Este es tu bot de Preventas. Acá cargás las preventas sin que se mezclen con los avisos. Escribime \"nueva\" para arrancar una. 🧾"
                : "✅ ¡Listo! Tu bot de Intervent quedó conectado. Te voy a avisar por acá las ventas nuevas y tus alertas. Escribime \"ayuda\" cuando quieras.";
            var (okSend, _) = await SendRawAsync(a.BotToken, a.ChatId.Value, mensajePrueba, ct);
            testEnviado = okSend;
        }

        await MarcarResultadoAsync(a, true, null, ct);
        return (true, username, a.ChatId, testEnviado, null);
    }

    /// <summary>Vincula el chat del dueño mirando los últimos mensajes que le escribió al bot.</summary>
    public async Task<(bool ok, long? chatId, string? error)> DetectarChatAsync(string proposito = "AVISOS", CancellationToken ct = default)
    {
        var a = await GetBotAsync(proposito, ct);
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
        // 2026-07-11: el aviso de ventas ahora se configura desde "Mis Alertas" (alerta del sistema
        // VENTA_MELI), con elección de canal: campanita 🔔 y/o Telegram 📲.
        var alerta = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Tipo == "VENTA_MELI", ct);
        if (alerta is null || !alerta.Activa) return;
        bool quiereTelegram = alerta.CanalTelegram;
        bool quiereCampanita = alerta.CanalCampanita;
        if (!quiereTelegram && !quiereCampanita) return;

        var a = await GetBotAsync("AVISOS", ct);
        bool telegramListo = a is not null && !string.IsNullOrWhiteSpace(a.BotToken) && a.IsActive && a.ChatId is not null;
        // Si SOLO quiere Telegram y todavía no está vinculado, no consumimos las ventas: esperamos a que se vincule.
        if (quiereTelegram && !quiereCampanita && !telegramListo) return;

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
        int procesados = 0;
        string? ultimoDetalle = null;
        foreach (var grupo in porOrden)
        {
            if (procesados >= 15) break; // el resto queda para la próxima vuelta
            var lista = grupo.ToList();
            bool enviado = false;
            if (quiereTelegram && telegramListo)
            {
                var texto = ArmarMensajeVenta(lista);
                var (ok, _) = await SendRawAsync(a!.BotToken, a.ChatId!.Value, texto, ct);
                if (!ok) break; // si falla el envío, no marcamos: reintenta la próxima
                enviado = true;
            }
            foreach (var o in lista) o.NotifiedTelegram = true;
            ultimoDetalle = ResumenCortoVenta(lista);
            // Historial de avisos: una fila por venta (bitácora, aparece de a una).
            _db.MisAlertasHistorial.Add(new MisAlertaHistorial
            {
                AlertaId = alerta.Id,
                Tipo = "VENTA_MELI",
                Mensaje = string.IsNullOrWhiteSpace(alerta.Mensaje) ? "Venta nueva en MercadoLibre" : alerta.Mensaje,
                Detalle = ultimoDetalle,
                Alcance = string.IsNullOrWhiteSpace(alerta.Alcance) ? "admin,oficina" : alerta.Alcance,
                PorTelegram = alerta.CanalTelegram,
                EnviadoTelegram = enviado
            });
            procesados++;
        }

        if (procesados > 0)
        {
            // Campanita: mostramos la última venta (queda encendida hasta que la mirás en la campanita).
            if (quiereCampanita && ultimoDetalle is not null)
            {
                alerta.EstaDisparada = true;
                alerta.Vista = false;
                alerta.DisparadaAt = DateTime.UtcNow;
                alerta.UltimoDetalle = ultimoDetalle;
                alerta.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>2026-07-16: avisa publicaciones MeLi que hay que REVISAR (incidente cápsulas KDOR):
    /// - PAUSADA_CON_STOCK: el push le calculó stock a una publi pausada pero NO la despertó.
    /// - STATUS_ACTIVE: una publi pasó de pausada→activa (reactivación detectada por el sync).
    /// Un aviso por publicación (Telegram y/o campanita según la alerta del sistema PUBLI_MELI)
    /// + fila en el historial. Marca NotifiedAt para no repetir. Mismo andamiaje que ventas.</summary>
    public async Task NotificarPublicacionesMeliAsync(CancellationToken ct = default)
    {
        var alerta = await _db.MisAlertas.FirstOrDefaultAsync(x => x.Tipo == "PUBLI_MELI", ct);
        if (alerta is null || !alerta.Activa) return;
        bool quiereTelegram = alerta.CanalTelegram;
        bool quiereCampanita = alerta.CanalCampanita;
        if (!quiereTelegram && !quiereCampanita) return;

        var a = await GetBotAsync("AVISOS", ct);
        bool telegramListo = a is not null && !string.IsNullOrWhiteSpace(a.BotToken) && a.IsActive && a.ChatId is not null;
        if (quiereTelegram && !quiereCampanita && !telegramListo) return;

        var tipos = new[] { "PAUSADA_CON_STOCK", "STATUS_ACTIVE" };
        var corteViejo = DateTime.UtcNow.AddHours(-12);

        // Anti-backlog: eventos sin avisar más viejos que 12h se marcan y no se mandan.
        var viejos = await _db.MeliCambiosDetectados
            .Where(c => c.NotifiedAt == null && tipos.Contains(c.Tipo) && c.DetectedAt < corteViejo)
            .ToListAsync(ct);
        if (viejos.Count > 0)
        {
            foreach (var c in viejos) c.NotifiedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        var pendientes = await _db.MeliCambiosDetectados
            .Where(c => c.NotifiedAt == null && tipos.Contains(c.Tipo) && c.DetectedAt >= corteViejo)
            .OrderBy(c => c.DetectedAt)
            .Take(15) // el resto queda para la próxima vuelta
            .ToListAsync(ct);
        if (pendientes.Count == 0) return;

        int procesados = 0;
        string? ultimoDetalle = null;
        foreach (var ev in pendientes)
        {
            bool enviado = false;
            var texto = ArmarMensajePubliRevisar(ev);
            if (quiereTelegram && telegramListo)
            {
                var (ok, _) = await SendRawAsync(a!.BotToken, a.ChatId!.Value, texto, ct);
                if (!ok) break; // si falla el envío, no marcamos: reintenta la próxima
                enviado = true;
            }
            ev.NotifiedAt = DateTime.UtcNow;
            ultimoDetalle = ResumenCortoPubli(ev);
            _db.MisAlertasHistorial.Add(new MisAlertaHistorial
            {
                AlertaId = alerta.Id,
                Tipo = "PUBLI_MELI",
                Mensaje = string.IsNullOrWhiteSpace(alerta.Mensaje) ? "Publicación MeLi para revisar" : alerta.Mensaje,
                Detalle = ultimoDetalle,
                Alcance = string.IsNullOrWhiteSpace(alerta.Alcance) ? "admin,oficina" : alerta.Alcance,
                PorTelegram = alerta.CanalTelegram,
                EnviadoTelegram = enviado
            });
            procesados++;
        }

        if (procesados > 0)
        {
            if (quiereCampanita && ultimoDetalle is not null)
            {
                alerta.EstaDisparada = true;
                alerta.Vista = false;
                alerta.DisparadaAt = DateTime.UtcNow;
                alerta.UltimoDetalle = ultimoDetalle;
                alerta.UpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
        }
    }

    private static string ResumenCortoPubli(MeliCambioDetectado ev)
    {
        var quePaso = ev.Tipo == "PAUSADA_CON_STOCK" ? "pausada con stock" : "reactivada";
        var nombre = string.IsNullOrWhiteSpace(ev.Title) ? ev.MeliItemId : ev.Title;
        return $"Publi {quePaso}: {Recortar(nombre, 60)}";
    }

    private static string ArmarMensajePubliRevisar(MeliCambioDetectado ev)
    {
        var lineas = new List<string>();
        if (ev.Tipo == "PAUSADA_CON_STOCK")
        {
            lineas.Add("⚠️ Publicación PAUSADA con stock — ¡revisá el precio antes de activarla!");
            lineas.Add($"📦 {ev.Title ?? ev.MeliItemId}");
            var datos = new List<string>();
            if (!string.IsNullOrWhiteSpace(ev.Sku)) datos.Add($"SKU {ev.Sku}");
            if (decimal.TryParse(ev.ValorNuevo, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var precio))
                datos.Add($"Precio actual en MeLi: {Money(precio)}");
            if (ev.Delta.HasValue) datos.Add($"Stock disponible: {ev.Delta.Value:0}");
            if (datos.Count > 0) lineas.Add(string.Join(" · ", datos));
            lineas.Add("");
            lineas.Add("El robot NO la activó (política nueva). Revisá el precio y activala desde el sistema: Cambios MeLi.");
        }
        else // STATUS_ACTIVE
        {
            lineas.Add("📢 Publicación REACTIVADA en MercadoLibre — revisá el precio.");
            lineas.Add($"📦 {ev.Title ?? ev.MeliItemId}");
            if (!string.IsNullOrWhiteSpace(ev.Sku)) lineas.Add($"SKU {ev.Sku}");
        }
        lineas.Add($"🔗 https://articulo.mercadolibre.com.ar/{ev.MeliItemId.Insert(3, "-")}");
        return string.Join("\n", lineas);
    }

    private static string Recortar(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Resumen corto de una venta para la campanita (una línea).</summary>
    private static string ResumenCortoVenta(List<MeliOrder> items)
    {
        var total = items.Select(i => i.TotalAmount).FirstOrDefault();
        var comprador = string.IsNullOrWhiteSpace(items[0].BuyerNickname) ? "—" : items[0].BuyerNickname;
        return $"Última venta: {Money(total)} · {comprador}";
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

    /// <summary>Una vuelta del poll: recorre TODOS los bots activos (Avisos + Preventas). Para el
    /// bot de Avisos, además, avisa las ventas pendientes. Devuelve false si no hay ningún bot
    /// activo (el caller espera más entre vueltas).</summary>
    public async Task<bool> PollOnceAsync(CancellationToken ct)
    {
        var bots = (await _db.TelegramAccounts.Where(x => x.IsActive).OrderBy(x => x.Id).ToListAsync(ct))
            .Where(b => !string.IsNullOrWhiteSpace(b.BotToken)).ToList();
        if (bots.Count == 0) return false;

        foreach (var bot in bots)
        {
            if (ct.IsCancellationRequested) break;
            try { await PollBotAsync(bot, ct); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _logger.LogWarning(ex, "[Telegram] error poleando bot {P}", bot.Proposito); }
        }
        return true;
    }

    /// <summary>Poll de un bot puntual: si es AVISOS avisa ventas; en ambos hace long-poll de mensajes
    /// y responde según el propósito (consultas para AVISOS, carga de preventa para PREVENTAS).</summary>
    private async Task PollBotAsync(TelegramAccount bot, CancellationToken ct)
    {
        if (bot.Proposito == "AVISOS")
        {
            try { await NotificarVentasPendientesAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Telegram] error avisando ventas"); }
            // 2026-07-16: publicaciones MeLi a revisar (pausadas con stock / reactivadas)
            try { await NotificarPublicacionesMeliAsync(ct); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Telegram] error avisando publicaciones a revisar"); }
        }

        // Si nunca poleamos este bot, adelantamos el cursor sin responder mensajes viejos.
        if (bot.LastUpdateId is null)
        {
            await AdelantarCursorAsync(bot, ct);
            await _db.SaveChangesAsync(ct);
            return;
        }

        long offset = bot.LastUpdateId.Value + 1;
        JsonDocument? doc = null;
        try
        {
            var http = NewClient(25);
            var resp = await http.GetAsync(
                $"{ApiBase}/bot{bot.BotToken}/getUpdates?timeout=15&offset={offset}&allowed_updates=%5B%22message%22%2C%22callback_query%22%5D", ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode) return;
            doc = JsonDocument.Parse(body);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _logger.LogWarning(ex, "[Telegram] error en getUpdates (poll)"); return; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
                return;

            long maxUpdateId = bot.LastUpdateId.Value;
            foreach (var upd in result.EnumerateArray())
            {
                if (upd.TryGetProperty("update_id", out var uid) && uid.TryGetInt64(out var uidVal) && uidVal > maxUpdateId)
                    maxUpdateId = uidVal;

                // Tocó un botón inline (ej. eligió un cliente de la lista).
                if (upd.TryGetProperty("callback_query", out var cq))
                {
                    await ProcesarCallbackAsync(bot, cq, ct);
                    continue;
                }

                if (!upd.TryGetProperty("message", out var m)) continue;
                if (!m.TryGetProperty("chat", out var c) || !c.TryGetProperty("id", out var cid) || !cid.TryGetInt64(out var chatId))
                    continue;
                var texto = m.TryGetProperty("text", out var txt) ? txt.GetString() : null;
                if (string.IsNullOrWhiteSpace(texto)) continue;

                // Vinculación con código: mientras no haya dueño, el bot NO le hace caso a nadie hasta
                // que le manden el código de seguridad. Así un extraño que descubra el bot no puede
                // adueñárselo.
                if (bot.ChatId is null)
                {
                    if (!string.IsNullOrEmpty(bot.VinculacionCode))
                    {
                        if (texto.Trim() == bot.VinculacionCode)
                        {
                            bot.ChatId = chatId;
                            await _db.SaveChangesAsync(ct);
                            await SendRawAsync(bot.BotToken, chatId, "✅ ¡Listo! Quedaste vinculado. Ya podés usar el bot.", ct);
                        }
                        else
                        {
                            await SendRawAsync(bot.BotToken, chatId,
                                "🔒 Para activar este bot mandame el código de seguridad (lo ves en el sistema, en Integraciones → Telegram).", ct);
                        }
                        continue;
                    }
                    // Sin código configurado (caso viejo): auto-vincular el primero que escribe.
                    bot.ChatId = chatId;
                }

                // Solo respondemos al chat del dueño (seguridad).
                if (bot.ChatId != chatId)
                {
                    await SendRawAsync(bot.BotToken, chatId, "Este bot es privado del sistema Intervent. No estás autorizado.", ct);
                    continue;
                }

                if (bot.Proposito == "PREVENTAS")
                    await ProcesarPreventaAsync(bot, chatId, texto, ct);
                else
                {
                    var respuesta = await ResponderComandoAsync(texto, ct);
                    await SendRawAsync(bot.BotToken, chatId, respuesta, ct);
                }
            }

            if (maxUpdateId > bot.LastUpdateId.Value) bot.LastUpdateId = maxUpdateId;
            if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync(ct);
        }
    }

    // ─────────────────────────── Bot de PREVENTAS (carga guiada) ───────────────────────────

    private const string PreventaBienvenida =
        "🧾 Este es tu bot de Preventas. Tocá el botón para cargar una preventa nueva.";

    private static void LimpiarConv(TelegramAccount bot)
    {
        bot.ConvEstado = null;
        bot.ConvClienteId = null;
        bot.ConvClienteNombre = null;
        bot.ConvItemsJson = null;
        bot.ConvPendProductoId = null;
        bot.ConvPendProductoNombre = null;
        bot.ConvPendProductoSku = null;
    }

    private class PreventaItem
    {
        public int ProductoId { get; set; }
        public string? Sku { get; set; }
        public string Nombre { get; set; } = "";
        public int Cantidad { get; set; }
    }

    private static List<PreventaItem> LeerItems(TelegramAccount bot)
    {
        if (string.IsNullOrWhiteSpace(bot.ConvItemsJson)) return new();
        try { return JsonSerializer.Deserialize<List<PreventaItem>>(bot.ConvItemsJson) ?? new(); } catch { return new(); }
    }

    private static void GuardarItems(TelegramAccount bot, List<PreventaItem> items)
        => bot.ConvItemsJson = JsonSerializer.Serialize(items);

    /// <summary>Maneja un mensaje entrante del bot de PREVENTAS (máquina de estados de la conversación).</summary>
    private async Task ProcesarPreventaAsync(TelegramAccount bot, long chatId, string textoRaw, CancellationToken ct)
    {
        var t = QuitarAcentos(textoRaw.Trim().ToLowerInvariant());

        if (t is "cancelar" or "cancela" or "/cancel" or "salir")
        {
            LimpiarConv(bot);
            await SendRawAsync(bot.BotToken, chatId, "Listo, cancelé. 👍", ct, TecladoBotones(new[] { "🧾 Nueva preventa" }));
            return;
        }

        if (bot.ConvEstado == "CLIENTE") { await PreventaClienteAsync(bot, chatId, textoRaw, ct); return; }
        if (bot.ConvEstado == "CANT") { await PreventaCantidadTextoAsync(bot, chatId, textoRaw, ct); return; }
        if (bot.ConvEstado == "LIBRE") { await PreventaLibreAsync(bot, chatId, textoRaw, ct); return; }
        // En el menú de productos, escribir texto = buscar un producto por nombre.
        if (bot.ConvEstado == "MENU") { await PreventaBuscarProductoAsync(bot, chatId, textoRaw, ct); return; }

        // Sin conversación en curso: ¿arranca una preventa?
        if (t.Contains("nueva") || t.Contains("preventa") || t.Contains("cargar") || t.Contains("pedido"))
        {
            bot.ConvEstado = "CLIENTE";
            bot.ConvClienteId = null;
            bot.ConvClienteNombre = null;
            await SendRawAsync(bot.BotToken, chatId,
                "🧾 Nueva preventa.\n¿Para qué cliente es? Escribime parte del nombre.\n(o poné \"suelta\" para venta sin cliente, o \"cancelar\")",
                ct, QuitarTeclado);
            return;
        }

        await SendRawAsync(bot.BotToken, chatId, PreventaBienvenida, ct, TecladoBotones(new[] { "🧾 Nueva preventa" }));
    }

    private async Task PreventaClienteAsync(TelegramAccount bot, long chatId, string textoRaw, CancellationToken ct)
    {
        var q = textoRaw.Trim();
        var qn = QuitarAcentos(q.ToLowerInvariant());
        if (qn is "suelta" or "venta suelta" or "sin cliente")
        {
            bot.ConvClienteId = null;
            bot.ConvClienteNombre = null;
            await IniciarMenuProductosAsync(bot, chatId, "Dale, venta suelta (sin cliente).", ct);
            return;
        }
        if (q.Length < 2)
        {
            await SendRawAsync(bot.BotToken, chatId, "Escribime al menos 2 letras del nombre del cliente. 🙂", ct);
            return;
        }

        // Búsqueda parcial por nombre o razón social. SIEMPRE mostramos las coincidencias como
        // botones para que el dueño elija (aunque una coincida exacto): así nunca elige por él.
        var like = $"%{q}%";
        var matches = await _db.CafeClientes.AsNoTracking()
            .Where(c => c.IsActive && (EF.Functions.Like(c.Nombre, like) ||
                        (c.RazonSocial != null && EF.Functions.Like(c.RazonSocial, like))))
            .OrderBy(c => c.Nombre)
            .Select(c => new { c.Id, c.Nombre }).Take(30).ToListAsync(ct);

        if (matches.Count == 0)
        {
            await SendRawAsync(bot.BotToken, chatId,
                $"No encontré ningún cliente con \"{q}\". Probá con otra parte del nombre, o escribí \"suelta\".", ct);
            return;
        }

        var opciones = matches.Take(10).Select(m => (text: m.Nombre, data: $"pvc:{m.Id}")).ToList();
        opciones.Add(("🛒 Venta suelta (sin cliente)", "pvc:0"));
        opciones.Add(("✖ Cancelar", "pvcx"));
        var cab = matches.Count == 1
            ? "Encontré este. Tocalo para confirmar 👇"
            : (matches.Count > 10
                ? $"Encontré {matches.Count}. Te muestro los primeros 10 — si no está, escribí más letras del nombre 👇"
                : "¿Cuál es? Tocá el cliente 👇");
        await SendRawAsync(bot.BotToken, chatId, cab, ct, TecladoInline(opciones));
    }

    private async Task PreventaFijarClienteAsync(TelegramAccount bot, long chatId, int clienteId, string clienteNombre, CancellationToken ct)
    {
        bot.ConvClienteId = clienteId;
        bot.ConvClienteNombre = clienteNombre;
        await IniciarMenuProductosAsync(bot, chatId, $"Dale, para {clienteNombre}.", ct);
    }

    /// <summary>Procesa el toque de un botón inline (callback_query). Confirma el toque (para sacar
    /// el "reloj") y, si es del bot de Preventas y del dueño, resuelve la elección del cliente.</summary>
    private async Task ProcesarCallbackAsync(TelegramAccount bot, JsonElement cq, CancellationToken ct)
    {
        var cbId = cq.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        await AnswerCallbackAsync(bot.BotToken, cbId, ct);

        long? chatId = null;
        if (cq.TryGetProperty("message", out var msg) && msg.TryGetProperty("chat", out var ch) &&
            ch.TryGetProperty("id", out var chId) && chId.TryGetInt64(out var cidv)) chatId = cidv;
        if (chatId is null && cq.TryGetProperty("from", out var fr) && fr.TryGetProperty("id", out var frId) && frId.TryGetInt64(out var frv))
            chatId = frv;
        if (chatId is null) return;

        // Los botones inline solo se le mandan al dueño ya vinculado; si no coincide, ignoramos.
        if (bot.ChatId is null || bot.ChatId != chatId || bot.Proposito != "PREVENTAS") return;

        var data = cq.TryGetProperty("data", out var dEl) ? dEl.GetString() : null;
        if (string.IsNullOrEmpty(data)) return;
        await ProcesarPreventaCallbackAsync(bot, chatId.Value, data, ct);
    }

    private async Task ProcesarPreventaCallbackAsync(TelegramAccount bot, long chatId, string data, CancellationToken ct)
    {
        // Cancelar corta siempre.
        if (data == "pvcx")
        {
            LimpiarConv(bot);
            await SendRawAsync(bot.BotToken, chatId, "Listo, cancelé. 👍", ct, TecladoBotones(new[] { "🧾 Nueva preventa" }));
            return;
        }
        if (bot.ConvEstado is null)
        {
            await SendRawAsync(bot.BotToken, chatId, "Esa preventa ya se cerró. Escribí \"nueva\" para cargar otra.", ct,
                TecladoBotones(new[] { "🧾 Nueva preventa" }));
            return;
        }

        // --- Elección de cliente ---
        if (data == "pvc:0")
        {
            bot.ConvClienteId = null;
            bot.ConvClienteNombre = null;
            await IniciarMenuProductosAsync(bot, chatId, "Dale, venta suelta (sin cliente).", ct);
            return;
        }
        if (data.StartsWith("pvc:") && int.TryParse(data.AsSpan(4), out var cid))
        {
            var cli = await _db.CafeClientes.AsNoTracking()
                .Where(c => c.Id == cid).Select(c => new { c.Id, c.Nombre }).FirstOrDefaultAsync(ct);
            if (cli is null) { await SendRawAsync(bot.BotToken, chatId, "No encontré ese cliente. Escribí parte del nombre de nuevo.", ct); return; }
            await PreventaFijarClienteAsync(bot, chatId, cli.Id, cli.Nombre, ct);
            return;
        }

        // --- Menú de productos ---
        if (data == "pvm:menu") { await MostrarMenuProductosAsync(bot, chatId, "¿Qué le cargamos? 👇", ct); return; }
        if (data == "pvm:mas")
        {
            if (bot.ConvClienteId is null) { await MostrarMenuProductosAsync(bot, chatId, "Para 'más comprados' necesito un cliente. Probá otra opción 👇", ct); return; }
            var prods = await ProductosMasCompradosAsync(bot.ConvClienteId.Value, ct);
            await MostrarProductosAsync(bot, chatId, prods,
                prods.Count == 0 ? "Este cliente todavía no tiene compras. Probá 'buscar' o 'más vendidos' 👇" : "⭐ Más comprados por el cliente. Tocá uno 👇", ct);
            return;
        }
        if (data == "pvm:top")
        {
            var prods = await ProductosMasVendidosAsync(ct);
            await MostrarProductosAsync(bot, chatId, prods, "🔝 Los más vendidos. Tocá uno 👇", ct);
            return;
        }
        if (data == "pvm:buscar") { bot.ConvEstado = "MENU"; await SendRawAsync(bot.BotToken, chatId, "🔎 Escribime parte del nombre del producto 👇", ct); return; }
        if (data == "pvm:libre") { bot.ConvEstado = "LIBRE"; await SendRawAsync(bot.BotToken, chatId, "✍️ Escribime el pedido con tus palabras 👇", ct, QuitarTeclado); return; }
        if (data == "pvfin") { await PreventaFinalizarAsync(bot, chatId, ct); return; }

        // --- Elegir producto → pedir cantidad ---
        if (data.StartsWith("pvp:") && int.TryParse(data.AsSpan(4), out var pid))
        {
            var p = await _db.CafeProductos.AsNoTracking()
                .Where(x => x.Id == pid && x.IsActive).Select(x => new { x.Id, x.Sku, x.Nombre }).FirstOrDefaultAsync(ct);
            if (p is null) { await MostrarMenuProductosAsync(bot, chatId, "No encontré ese producto. Probá de nuevo 👇", ct); return; }
            bot.ConvPendProductoId = p.Id;
            bot.ConvPendProductoNombre = p.Nombre;
            bot.ConvPendProductoSku = p.Sku;
            bot.ConvEstado = "CANT";
            var botones = new List<(string, string)> { ("1", "pvq:1"), ("2", "pvq:2"), ("3", "pvq:3"), ("5", "pvq:5"), ("10", "pvq:10"), ("↩️ Volver", "pvm:menu") };
            await SendRawAsync(bot.BotToken, chatId, $"¿Cuántos de {p.Nombre}?\nTocá o escribí el número 👇", ct, TecladoInline(botones));
            return;
        }
        if (data.StartsWith("pvq:") && int.TryParse(data.AsSpan(4), out var qn))
        {
            await PreventaAgregarConCantidadAsync(bot, chatId, qn, ct);
            return;
        }
    }

    // ── Menú de productos y carrito ──

    private async Task IniciarMenuProductosAsync(TelegramAccount bot, long chatId, string prefacio, CancellationToken ct)
    {
        bot.ConvItemsJson = null;
        bot.ConvPendProductoId = null; bot.ConvPendProductoNombre = null; bot.ConvPendProductoSku = null;
        await MostrarMenuProductosAsync(bot, chatId, prefacio + "\n¿Qué le cargamos? 👇", ct);
    }

    private async Task MostrarMenuProductosAsync(TelegramAccount bot, long chatId, string encabezado, CancellationToken ct)
    {
        bot.ConvEstado = "MENU";
        bot.ConvPendProductoId = null; bot.ConvPendProductoNombre = null; bot.ConvPendProductoSku = null;
        var items = LeerItems(bot);
        var lineas = new List<string> { encabezado };
        if (items.Count > 0)
        {
            lineas.Add("");
            lineas.Add("🧾 Va cargado:");
            foreach (var it in items) lineas.Add($"• {it.Cantidad}× {it.Nombre}");
        }
        var botones = new List<(string, string)>();
        if (bot.ConvClienteId is not null) botones.Add(("⭐ Más comprados", "pvm:mas"));
        botones.Add(("🔝 Más vendidos", "pvm:top"));
        botones.Add(("🔎 Buscar producto", "pvm:buscar"));
        botones.Add(("✍️ Escribir a mano", "pvm:libre"));
        if (items.Count > 0) botones.Add(("✅ Terminar preventa", "pvfin"));
        botones.Add(("✖ Cancelar", "pvcx"));
        await SendRawAsync(bot.BotToken, chatId, string.Join("\n", lineas), ct, TecladoInline(botones));
    }

    private async Task MostrarProductosAsync(TelegramAccount bot, long chatId, List<(int Id, string? Sku, string Nombre)> prods, string titulo, CancellationToken ct)
    {
        if (prods.Count == 0) { await MostrarMenuProductosAsync(bot, chatId, titulo, ct); return; }
        var botones = prods.Take(10).Select(p => (p.Nombre.Length > 45 ? p.Nombre.Substring(0, 45) : p.Nombre, $"pvp:{p.Id}")).ToList();
        botones.Add(("↩️ Volver", "pvm:menu"));
        await SendRawAsync(bot.BotToken, chatId, titulo, ct, TecladoInline(botones));
    }

    private async Task PreventaBuscarProductoAsync(TelegramAccount bot, long chatId, string textoRaw, CancellationToken ct)
    {
        var q = textoRaw.Trim();
        if (q.Length < 2) { await SendRawAsync(bot.BotToken, chatId, "Escribime al menos 2 letras del producto 🙂", ct); return; }
        var prods = await BuscarProductosAsync(q, ct);
        await MostrarProductosAsync(bot, chatId, prods,
            prods.Count == 0 ? $"No encontré productos con \"{q}\". Probá otra palabra 👇" : "¿Cuál? Tocá el producto 👇", ct);
    }

    private async Task PreventaCantidadTextoAsync(TelegramAccount bot, long chatId, string texto, CancellationToken ct)
    {
        var s = new string(texto.Where(char.IsDigit).ToArray());
        if (!int.TryParse(s, out var n) || n < 1) { await SendRawAsync(bot.BotToken, chatId, "Decime un número (ej: 2), o tocá un botón. 🙂", ct); return; }
        if (n > 9999) n = 9999;
        await PreventaAgregarConCantidadAsync(bot, chatId, n, ct);
    }

    private async Task PreventaAgregarConCantidadAsync(TelegramAccount bot, long chatId, int cantidad, CancellationToken ct)
    {
        if (bot.ConvPendProductoId is null) { await MostrarMenuProductosAsync(bot, chatId, "Elegí un producto primero 👇", ct); return; }
        if (cantidad < 1) cantidad = 1;
        var items = LeerItems(bot);
        var ex = items.FirstOrDefault(i => i.ProductoId == bot.ConvPendProductoId.Value);
        if (ex is not null) ex.Cantidad += cantidad;
        else items.Add(new PreventaItem { ProductoId = bot.ConvPendProductoId.Value, Sku = bot.ConvPendProductoSku, Nombre = bot.ConvPendProductoNombre ?? "producto", Cantidad = cantidad });
        GuardarItems(bot, items);
        var nom = bot.ConvPendProductoNombre;
        await MostrarMenuProductosAsync(bot, chatId, $"✅ Agregué {cantidad}× {nom}. ¿Algo más? 👇", ct);
    }

    private async Task PreventaLibreAsync(TelegramAccount bot, long chatId, string texto, CancellationToken ct)
    {
        var d = texto.Trim();
        if (string.IsNullOrWhiteSpace(d)) { await SendRawAsync(bot.BotToken, chatId, "Escribime el pedido, por favor 🙏", ct); return; }
        await GuardarPreventaAsync(bot, chatId, d, null, ct);
    }

    private async Task PreventaFinalizarAsync(TelegramAccount bot, long chatId, CancellationToken ct)
    {
        var items = LeerItems(bot);
        if (items.Count == 0) { await MostrarMenuProductosAsync(bot, chatId, "Todavía no agregaste nada. Elegí al menos un producto 👇", ct); return; }
        var texto = string.Join("\n", items.Select(i => $"{i.Cantidad}x {i.Nombre}"));
        var json = JsonSerializer.Serialize(items);
        await GuardarPreventaAsync(bot, chatId, texto, json, ct);
    }

    private async Task GuardarPreventaAsync(TelegramAccount bot, long chatId, string textoCrudo, string? productosJson, CancellationToken ct)
    {
        var clienteNombre = bot.ConvClienteNombre;
        _db.WhatsAppPedidosRecibidos.Add(new WhatsAppPedidoRecibido
        {
            Telefono = "telegram",
            TextoCrudo = textoCrudo,
            ClienteId = bot.ConvClienteId,
            ClienteNombre = clienteNombre,
            ProductosParseados = productosJson,
            Estado = "NUEVO",
            Source = "telegram",
            RecibidoAt = DateTime.UtcNow
        });
        LimpiarConv(bot);
        await _db.SaveChangesAsync(ct);

        var clienteTxt = string.IsNullOrWhiteSpace(clienteNombre) ? "venta suelta" : clienteNombre;
        await SendRawAsync(bot.BotToken, chatId,
            $"✅ ¡Preventa guardada! ({clienteTxt})\nYa te aparece en Pedidos para convertirla en venta. 🧾\n\nTocá el botón para cargar otra.",
            ct, TecladoBotones(new[] { "🧾 Nueva preventa" }));
    }

    // ── Consultas de productos ──

    private async Task<List<(int Id, string? Sku, string Nombre)>> ProductosMasCompradosAsync(int clienteId, CancellationToken ct)
    {
        var grouped = await _db.CafeVentaItems
            .Where(i => i.VentaNav != null && i.VentaNav.ClienteId == clienteId && i.VentaNav.Estado != "anulado" && i.ProductoId != null)
            .GroupBy(i => i.ProductoId!.Value)
            .Select(g => new { ProductoId = g.Key, Veces = g.Select(x => x.VentaId).Distinct().Count() })
            .OrderByDescending(x => x.Veces).Take(10).ToListAsync(ct);
        return await ResolverProductosAsync(grouped.Select(g => g.ProductoId).ToList(), ct);
    }

    private async Task<List<(int Id, string? Sku, string Nombre)>> ProductosMasVendidosAsync(CancellationToken ct)
    {
        var grouped = await _db.CafeVentaItems
            .Where(i => i.VentaNav != null && i.VentaNav.Estado != "anulado" && i.ProductoId != null)
            .GroupBy(i => i.ProductoId!.Value)
            .Select(g => new { ProductoId = g.Key, Qty = g.Sum(x => x.Cantidad) })
            .OrderByDescending(x => x.Qty).Take(10).ToListAsync(ct);
        return await ResolverProductosAsync(grouped.Select(g => g.ProductoId).ToList(), ct);
    }

    /// <summary>Resuelve id/sku/nombre de una lista de productoIds, respetando el orden recibido.</summary>
    private async Task<List<(int Id, string? Sku, string Nombre)>> ResolverProductosAsync(List<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        var prods = await _db.CafeProductos.AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.IsActive)
            .Select(p => new { p.Id, p.Sku, p.Nombre }).ToListAsync(ct);
        var dict = prods.ToDictionary(p => p.Id);
        var res = new List<(int, string?, string)>();
        foreach (var id in ids) if (dict.TryGetValue(id, out var p)) res.Add((p.Id, p.Sku, p.Nombre));
        return res;
    }

    private async Task<List<(int Id, string? Sku, string Nombre)>> BuscarProductosAsync(string q, CancellationToken ct)
    {
        var up = q.ToUpperInvariant();
        var prods = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive && p.IsVisibleEnVentas &&
                        (p.Nombre.ToUpper().Contains(up) || (p.Sku != null && p.Sku.ToUpper().Contains(up))))
            .OrderBy(p => p.Nombre).Take(10)
            .Select(p => new { p.Id, p.Sku, p.Nombre }).ToListAsync(ct);
        return prods.Select(p => (p.Id, p.Sku, p.Nombre)).ToList();
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
        "• shell — el saldo de la Shell Flota\n" +
        "• alertas — tus alertas que están saltando\n" +
        "• precio C3622AZ — costo y sugerido de un producto\n\n" +
        "Y te voy avisando solo las ventas nuevas y las alertas.";

    private async Task<string> ResponderComandoAsync(string textoRaw, CancellationToken ct)
    {
        var t = QuitarAcentos(textoRaw.Trim().ToLowerInvariant());

        if (t.StartsWith("/start") || t.Contains("hola") || t.Contains("ayuda") || t.Contains("menu") || t == "?")
            return Ayuda;
        // "precio C3622AZ" / "costo F30" — se busca el código en el texto ORIGINAL (sin bajar a
        // minúsculas) para no romper SKUs. Va antes que el resto para que gane el comando.
        if (t.StartsWith("precio") || t.StartsWith("costo") || t.StartsWith("/precio") || t.StartsWith("/costo"))
        {
            var raw = textoRaw.Trim();
            var sp = raw.IndexOf(' ');
            var codigo = sp >= 0 ? raw.Substring(sp + 1).Trim() : "";
            if (string.IsNullOrWhiteSpace(codigo))
                return "🔎 Escribime el código así:\nprecio C3622AZ";
            return await ResumenPrecioProductoAsync(codigo, ct);
        }
        if (t.Contains("venta"))
            return await ResumenVentasHoyAsync(ct);
        if (t.Contains("saldo") || t.Contains("mercado pago") || t == "mp" || t.Contains("plata") || t.Contains("dinero"))
            return await ResumenSaldoAsync(ct);
        if (t.Contains("shell") || t.Contains("nafta") || t.Contains("combustible"))
            return await ResumenShellAsync(ct);
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

    /// <summary>Busca un producto por código (SKU) o nombre y devuelve costo sin IVA + sugerido.
    /// Misma lógica que el buscador de la web (CafeConsultasController.BuscarPrecio): si tiene OEM,
    /// el sugerido sale del OEM (con IVA → sin IVA); si no, muestra las listas Bar/Comercial.</summary>
    private async Task<string> ResumenPrecioProductoAsync(string codigo, CancellationToken ct)
    {
        codigo = codigo.Trim();
        var up = codigo.ToUpperInvariant();
        var p = await _db.CafeProductos
            .Include(x => x.OemNav)
            .FirstOrDefaultAsync(x => x.Sku == codigo || x.Sku == up, ct);
        if (p is null)
        {
            p = await _db.CafeProductos
                .Include(x => x.OemNav)
                .Where(x => (x.Sku != null && x.Sku.Contains(codigo)) || x.Nombre.Contains(codigo))
                .OrderBy(x => x.Nombre)
                .FirstOrDefaultAsync(ct);
        }
        if (p is null)
            return $"🔎 No encontré ningún producto con «{codigo}».";

        var iva = p.IvaPct > 0m ? p.IvaPct : 21m;
        var lineas = new List<string>
        {
            $"🔎 {p.Nombre}",
            $"SKU {p.Sku ?? "—"} · {p.Categoria}",
            $"Costo sin IVA: {Money(p.Costo)}"
        };

        if (p.OemId.HasValue && p.OemNav?.PvpConIva is decimal pvpOem && pvpOem > 0m)
        {
            var mult = p.MultiplicadorOem is decimal m && m > 0m ? m : 1m;
            var ivaOem = p.OemNav.IvaPct is decimal io && io > 0m ? io : 21m;
            var conIva = Math.Round(pvpOem * mult, 2);
            var sinIva = Math.Round(conIva / (1m + ivaOem / 100m), 2);
            lineas.Add($"Sugerido: {Money(sinIva)}  (con IVA {Money(conIva)})");
        }
        else
        {
            var suf = p.Categoria == "CAFE" ? " (por kg)" : "";
            var bar = p.PrecioBar ?? p.Pvp1 ?? p.PrecioPorKg;
            var com = p.PrecioOtro ?? p.Pvp2 ?? p.PrecioPorKg;
            if (bar.HasValue)
                lineas.Add($"Precio Bar{suf}: {Money(bar.Value)}  (con IVA {Money(Math.Round(bar.Value * (1m + iva / 100m), 2))})");
            if (com.HasValue)
                lineas.Add($"Precio Comercial{suf}: {Money(com.Value)}  (con IVA {Money(Math.Round(com.Value * (1m + iva / 100m), 2))})");
        }
        return string.Join("\n", lineas);
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

    private async Task<string> ResumenShellAsync(CancellationToken ct)
    {
        var sh = await _db.ShellAccounts.OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        if (sh is null || string.IsNullOrWhiteSpace(sh.LastSaldo))
            return "⛽ Todavía no tengo el saldo de la Shell Flota. (se lee solo cada un rato)";
        var lineas = new List<string> { "⛽ Shell Flota", $"Saldo: {sh.LastSaldo}" };
        if (sh.LastSaldoAt is not null)
            lineas.Add($"(último dato: {sh.LastSaldoAt.Value.AddHours(ARG_OFFSET_HOURS):dd/MM HH:mm})");
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
