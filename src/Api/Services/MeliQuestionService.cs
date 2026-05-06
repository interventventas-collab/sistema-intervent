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
        while (hasMore)
        {
            // Solo UNANSWERED — las respondidas no nos interesan para la campanita
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
                    var (s, n) = await UpsertAsync(account.Id, q);
                    synced += s; neu += n;
                }
            }
            await _db.SaveChangesAsync();

            offset += limit;
            hasMore = offset < total && total > 0;
        }
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
    public async Task<MeliQuestion?> AnswerAsync(int questionId, string answerText)
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
        q.DateAnswered = DateTime.UtcNow;
        q.LastSyncedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return q;
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
