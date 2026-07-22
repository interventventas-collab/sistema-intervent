using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Sync + responder preguntas de MercadoLibre.
/// Usa el endpoint /my/received_questions/search para listar y /answers para responder.
/// </summary>
public class MeliQuestionService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;

    public MeliQuestionService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService)
    {
        _db = db; _httpFactory = httpFactory; _accountService = accountService;
    }

    /// <summary>Trae preguntas UNANSWERED de todas las cuentas y las upsertea en la DB.</summary>
    public async Task<MeliQuestionSyncResult> SyncAsync()
    {
        var accounts = await _accountService.GetAllAccountEntitiesAsync();
        int totalSynced = 0, totalNew = 0, totalErrors = 0;
        var errors = new List<string>();
        foreach (var account in accounts)
        {
            try
            {
                var token = await _accountService.GetValidTokenAsync(account);
                if (token is null)
                {
                    errors.Add($"Token expirado para {account.Nickname}");
                    totalErrors++;
                    continue;
                }
                var (synced, neu) = await SyncForAccountAsync(account, token);
                totalSynced += synced;
                totalNew += neu;
            }
            catch (Exception ex)
            {
                errors.Add($"{account.Nickname}: {ex.Message}");
                totalErrors++;
            }
        }
        return new MeliQuestionSyncResult(totalSynced, totalNew, totalErrors, errors);
    }

    private async Task<(int synced, int neu)> SyncForAccountAsync(MeliAccount account, string token)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        int synced = 0, neu = 0;
        int offset = 0; int limit = 50; bool hasMore = true;

        // Track de las preguntas UNANSWERED que vimos en MeLi en este sync.
        // Si una pregunta nuestra esta UNANSWERED pero NO aparece en este conjunto,
        // significa que fue respondida (o eliminada) en MeLi por fuera de la app.
        var seenInMeli = new HashSet<long>();

        while (hasMore)
        {
            var url = $"https://api.mercadolibre.com/my/received_questions/search?status=UNANSWERED&limit={limit}&offset={offset}";
            var response = await http.GetAsync(url);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var newToken = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
                if (newToken is not null)
                {
                    token = newToken;
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    response = await http.GetAsync(url);
                }
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                throw new Exception($"MeLi API ({(int)response.StatusCode}): {body}");
            }
            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json).RootElement;
            int total = 0;
            if (doc.TryGetProperty("total", out var t)) total = t.GetInt32();
            else if (doc.TryGetProperty("paging", out var pg) && pg.TryGetProperty("total", out var pt)) total = pt.GetInt32();

            if (doc.TryGetProperty("questions", out var qs))
            {
                foreach (var q in qs.EnumerateArray())
                {
                    if (q.TryGetProperty("id", out var idEl)) seenInMeli.Add(idEl.GetInt64());
                    var (s, n) = await UpsertAsync(account.Id, q);
                    synced += s; neu += n;
                }
            }
            await _db.SaveChangesAsync();

            offset += limit;
            hasMore = offset < total && total > 0;
        }

        // Reconciliar: traer las locales UNANSWERED de esta cuenta que NO aparecieron en MeLi.
        // Para cada una, GET /questions/{id} y actualizar status + answer.
        var localUnanswered = await _db.MeliQuestions
            .Where(x => x.MeliAccountId == account.Id && x.Status == "UNANSWERED")
            .Select(x => new { x.Id, x.MeliQuestionId })
            .ToListAsync();
        var stale = localUnanswered.Where(x => !seenInMeli.Contains(x.MeliQuestionId)).ToList();
        foreach (var s in stale)
        {
            try
            {
                var url = $"https://api.mercadolibre.com/questions/{s.MeliQuestionId}";
                var resp = await http.GetAsync(url);
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Pregunta eliminada en MeLi — la marcamos como DELETED para que desaparezca
                    var loc = await _db.MeliQuestions.FindAsync(s.Id);
                    if (loc is not null) { loc.Status = "DELETED"; loc.LastSyncedAt = DateTime.UtcNow; }
                    continue;
                }
                if (!resp.IsSuccessStatusCode) continue;
                var body = await resp.Content.ReadAsStringAsync();
                var qDoc = JsonDocument.Parse(body).RootElement;
                await UpsertAsync(account.Id, qDoc);
                synced++;
            }
            catch { /* tolerar errores aislados, seguimos con el siguiente */ }
        }
        await _db.SaveChangesAsync();

        return (synced, neu);
    }

    private async Task<(int synced, int neu)> UpsertAsync(int accountId, JsonElement q)
    {
        long qid = q.GetProperty("id").GetInt64();
        var existing = await _db.MeliQuestions.FirstOrDefaultAsync(x => x.MeliQuestionId == qid);
        bool isNew = existing is null;
        existing ??= new MeliQuestion { MeliQuestionId = qid, MeliAccountId = accountId };
        existing.ItemId = q.GetProperty("item_id").GetString() ?? "";
        existing.Text = q.GetProperty("text").GetString() ?? "";
        existing.Status = q.TryGetProperty("status", out var st) ? (st.GetString() ?? "UNANSWERED") : "UNANSWERED";
        existing.DateCreated = q.TryGetProperty("date_created", out var dc) && dc.ValueKind == JsonValueKind.String
            ? DateTime.Parse(dc.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
            : DateTime.UtcNow;
        if (q.TryGetProperty("from", out var fr))
        {
            existing.FromUserId = fr.TryGetProperty("id", out var fid) ? fid.GetInt64() : 0;
            existing.FromNickname = fr.TryGetProperty("nickname", out var fn) ? fn.GetString() : null;
        }
        if (q.TryGetProperty("answer", out var ans) && ans.ValueKind == JsonValueKind.Object)
        {
            existing.AnswerText = ans.TryGetProperty("text", out var at) ? at.GetString() : null;
            existing.DateAnswered = ans.TryGetProperty("date_created", out var ad) && ad.ValueKind == JsonValueKind.String
                ? DateTime.Parse(ad.GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime()
                : null;
        }
        existing.LastSyncedAt = DateTime.UtcNow;

        // Snapshot del item (titulo + thumbnail) si lo tenemos cacheado en MeliItems
        if (string.IsNullOrEmpty(existing.ItemTitle) && !string.IsNullOrEmpty(existing.ItemId))
        {
            var item = await _db.MeliItems.FirstOrDefaultAsync(i => i.MeliItemId == existing.ItemId);
            if (item is not null)
            {
                existing.ItemTitle = item.Title;
                existing.ItemThumbnail = item.Thumbnail;
            }
        }

        if (isNew) _db.MeliQuestions.Add(existing);
        return (1, isNew ? 1 : 0);
    }

    /// <summary>Postea la respuesta a MeLi y actualiza el registro local.</summary>
    /// <param name="auto">True si la manda el robot (respondedor automático), false si la escribió una persona.</param>
    public async Task<MeliQuestion?> AnswerAsync(int questionId, string answerText, bool auto = false)
    {
        if (string.IsNullOrWhiteSpace(answerText)) throw new ArgumentException("La respuesta no puede estar vacía");
        var q = await _db.MeliQuestions.FirstOrDefaultAsync(x => x.Id == questionId);
        if (q is null) return null;
        var account = await _db.MeliAccounts.FindAsync(q.MeliAccountId);
        if (account is null) throw new InvalidOperationException("Cuenta MeLi no encontrada");
        var token = await _accountService.GetValidTokenAsync(account);
        if (token is null) throw new InvalidOperationException("No se pudo obtener token de MeLi");

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var payload = new { question_id = q.MeliQuestionId, text = answerText.Trim() };
        var json = JsonSerializer.Serialize(payload);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var resp = await http.PostAsync("https://api.mercadolibre.com/answers", content);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized || resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var newToken = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
            if (newToken is not null)
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                resp = await http.PostAsync("https://api.mercadolibre.com/answers", content);
            }
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new Exception($"MeLi rechazó la respuesta ({(int)resp.StatusCode}): {body}");
        }

        q.AnswerText = answerText.Trim();
        q.Status = "ANSWERED";
        q.AutoAnswered = auto;
        q.DateAnswered = DateTime.UtcNow;
        q.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return q;
    }

    // ===================== RESPONDEDOR AUTOMÁTICO =====================
    // Corre en cada ciclo del robot (cada 1 min). De noche (o en la franja configurada),
    // si una pregunta lleva más de X minutos sin que nadie la conteste, responde solo
    // con uno de los mensajes de la lista (elegido al azar) + la firma.
    // Config en AppSettings + tablas MeliAutoReplyMessages / MeliAutoReplySchedule.

    private const string CfgEnabled = "meli.autoreply.enabled";
    private const string CfgDelayMinutes = "meli.autoreply.delayMinutes";
    private const string CfgSignature = "meli.autoreply.signature";
    private const string CfgHolidayDate = "meli.autoreply.holidayDate";
    private const int MaxPerRun = 8;          // tope por ciclo para no mandar todo de golpe (anti-spam)
    private const int SpacingMs = 1500;       // pausita entre respuestas

    private async Task<string?> GetSettingAsync(string key)
        => (await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == key))?.Value;

    /// <summary>Hora actual de Argentina (UTC-3, sin horario de verano).</summary>
    private static DateTime NowArgentina() => DateTime.UtcNow.AddHours(-3);

    /// <summary>Arma el texto final: cuerpo del mensaje + firma configurada.</summary>
    private static string ComposeAnswer(string body, string? signature)
    {
        body = (body ?? "").Trim();
        signature = (signature ?? "").Trim();
        return string.IsNullOrEmpty(signature) ? body : $"{body} {signature}";
    }

    private static TimeSpan ParseHhmm(string? hhmm)
    {
        if (TimeSpan.TryParse(hhmm, out var ts)) return ts;
        return TimeSpan.Zero;
    }

    /// <summary>Devuelve true si en este momento (hora Argentina) el robot debe responder según la config.</summary>
    private async Task<bool> IsWithinActiveWindowAsync()
    {
        var nowArt = NowArgentina();

        // Feriado manual: "hoy responder todo el día" — si la fecha guardada es hoy, activo todo el día.
        var holiday = await GetSettingAsync(CfgHolidayDate);
        if (!string.IsNullOrWhiteSpace(holiday) && holiday == nowArt.ToString("yyyy-MM-dd"))
            return true;

        int dow = (int)nowArt.DayOfWeek; // 0=Domingo .. 6=Sábado
        var row = await _db.MeliAutoReplySchedule.FirstOrDefaultAsync(s => s.DayOfWeek == dow);
        if (row is null || !row.IsActive) return false;
        if (row.AllDay) return true;

        var start = ParseHhmm(row.StartTime);
        var end = ParseHhmm(row.EndTime);
        if (start == end) return true; // desde == hasta => todo el día (seguridad)
        var tod = nowArt.TimeOfDay;
        return start < end
            ? (tod >= start && tod < end)              // franja normal dentro del mismo día
            : (tod >= start || tod < end);             // franja que cruza la medianoche (ej. 21:00 a 06:00)
    }

    /// <summary>
    /// Corre el respondedor automático: si está activo y estamos en la franja horaria,
    /// contesta las preguntas sin responder que llevan más de X min esperando.
    /// </summary>
    public async Task<MeliAutoReplyRunResult> RunAutoReplyAsync()
    {
        var enabled = await GetSettingAsync(CfgEnabled);
        if (enabled != "1" && !string.Equals(enabled, "true", StringComparison.OrdinalIgnoreCase))
            return new MeliAutoReplyRunResult(false, "apagado", 0, 0);

        if (!await IsWithinActiveWindowAsync())
            return new MeliAutoReplyRunResult(true, "fuera de horario", 0, 0);

        // Mensajes activos
        var messages = await _db.MeliAutoReplyMessages
            .Where(m => m.IsActive)
            .Select(m => m.Body)
            .ToListAsync();
        if (messages.Count == 0)
            return new MeliAutoReplyRunResult(true, "sin mensajes configurados", 0, 0);

        var signature = await GetSettingAsync(CfgSignature);

        // Colchón: solo preguntas que llevan >= delay minutos sin responder.
        int delayMin = int.TryParse(await GetSettingAsync(CfgDelayMinutes), out var d) ? d : 30;
        var cutoff = DateTime.UtcNow.AddMinutes(-Math.Max(0, delayMin));

        var pending = await _db.MeliQuestions
            .Where(q => q.Status == "UNANSWERED" && q.AnswerText == null
                        && !q.AutoAnswered && q.DateCreated <= cutoff)
            .OrderBy(q => q.DateCreated)   // las más viejas primero (bajar reputación es lo peor)
            .Take(MaxPerRun)
            .Select(q => q.Id)
            .ToListAsync();

        int answered = 0, errors = 0;
        foreach (var qId in pending)
        {
            try
            {
                var body = messages[Random.Shared.Next(messages.Count)];
                var text = ComposeAnswer(body, signature);
                var result = await AnswerAsync(qId, text, auto: true);
                if (result is not null) answered++;
                else errors++;
            }
            catch
            {
                errors++; // toleramos el fallo puntual y seguimos; se reintenta en el próximo ciclo
            }
            if (pending.Count > 1) await Task.Delay(SpacingMs); // espaciado anti-ráfaga
        }

        return new MeliAutoReplyRunResult(true, "ok", answered, errors);
    }

    public async Task MarkAllSeenAsync()
    {
        var now = DateTime.UtcNow;
        await _db.MeliQuestions
            .Where(q => q.Status == "UNANSWERED" && q.SeenAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.SeenAt, now));
    }
}

public record MeliQuestionSyncResult(int TotalSynced, int TotalNew, int TotalErrors, List<string> Errors);

public record MeliAutoReplyRunResult(bool Enabled, string Motivo, int Answered, int Errors);
