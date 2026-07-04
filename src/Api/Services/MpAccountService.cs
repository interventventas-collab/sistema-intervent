using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Maneja la (unica) cuenta de Mercado Pago. El Access Token se guarda en texto (la API
/// lo usa en runtime). Nunca exponer el token en un DTO al frontend — solo HasToken.
/// Mismo molde que ShellAccountService.
/// </summary>
public class MpAccountService
{
    private readonly AppDbContext _db;
    public MpAccountService(AppDbContext db) { _db = db; }

    public record MpAccountDto(int Id, string? Alias, bool HasToken, bool IsActive,
        long? MpUserId, string? Nickname, string? SiteId,
        decimal? LastSaldoDisponible, decimal? LastSaldoTotal, DateTime? LastSaldoAt,
        bool LastSyncOk, string? LastError,
        bool AutoSyncEnabled, string? AutoSyncTimes, DateTime? LastAutoSyncAt,
        DateTime CreatedAt, DateTime? UpdatedAt);

    public record SaveMpAccountRequest(string? AccessToken, string? Alias, bool IsActive,
        bool AutoSyncEnabled = false, string? AutoSyncTimes = null);

    private static MpAccountDto Map(MpAccount a) => new(
        a.Id, string.IsNullOrEmpty(a.Alias) ? null : a.Alias,
        !string.IsNullOrEmpty(a.AccessToken), a.IsActive,
        a.MpUserId, a.Nickname, a.SiteId,
        a.LastSaldoDisponible, a.LastSaldoTotal, a.LastSaldoAt,
        a.LastSyncOk, a.LastError,
        a.AutoSyncEnabled, a.AutoSyncTimes, a.LastAutoSyncAt, a.CreatedAt, a.UpdatedAt);

    public async Task<MpAccountDto?> GetAsync()
    {
        var a = await _db.MpAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        return a is null ? null : Map(a);
    }

    public async Task<string?> GetTokenAsync()
    {
        var a = await _db.MpAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        return a?.AccessToken;
    }

    public async Task<MpAccount?> GetEntityAsync()
        => await _db.MpAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();

    /// <summary>Guarda el resultado de una lectura de saldo (ok o error) + datos de la cuenta.</summary>
    public async Task GuardarSaldoAsync(decimal? disponible, decimal? total, bool ok, string? error,
        long? mpUserId = null, string? nickname = null, string? siteId = null)
    {
        var a = await _db.MpAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null) return;
        if (ok)
        {
            a.LastSaldoDisponible = disponible;
            a.LastSaldoTotal = total;
            a.LastSaldoAt = DateTime.UtcNow;
        }
        if (mpUserId.HasValue) a.MpUserId = mpUserId;
        if (!string.IsNullOrEmpty(nickname)) a.Nickname = nickname;
        if (!string.IsNullOrEmpty(siteId)) a.SiteId = siteId;
        a.LastSyncOk = ok;
        a.LastError = error is null ? null : (error.Length > 500 ? error.Substring(0, 500) : error);
        await _db.SaveChangesAsync();
    }

    public async Task MarcarAutoSyncAsync(DateTime whenUtc)
    {
        var a = await _db.MpAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null) return;
        a.LastAutoSyncAt = whenUtc;
        await _db.SaveChangesAsync();
    }

    public async Task<(bool ok, string? error, MpAccountDto? dto)> SaveAsync(SaveMpAccountRequest req)
    {
        var a = await _db.MpAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null)
        {
            if (string.IsNullOrWhiteSpace(req.AccessToken))
                return (false, "El Access Token es obligatorio la primera vez", null);
            a = new MpAccount
            {
                AccessToken = req.AccessToken.Trim(),
                Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim(),
                IsActive = req.IsActive,
                AutoSyncEnabled = req.AutoSyncEnabled,
                AutoSyncTimes = NormTimes(req.AutoSyncTimes),
                CreatedAt = DateTime.UtcNow
            };
            _db.MpAccounts.Add(a);
        }
        else
        {
            // El token solo se pisa si viene uno nuevo (el frontend manda vacio para no cambiarlo).
            if (!string.IsNullOrWhiteSpace(req.AccessToken)) a.AccessToken = req.AccessToken.Trim();
            a.Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim();
            a.IsActive = req.IsActive;
            a.AutoSyncEnabled = req.AutoSyncEnabled;
            a.AutoSyncTimes = NormTimes(req.AutoSyncTimes);
            a.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return (true, null, Map(a));
    }

    private static string? NormTimes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var valid = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => TimeSpan.TryParseExact(s, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out _))
            .Distinct().OrderBy(s => s).ToList();
        return valid.Count == 0 ? null : string.Join(",", valid);
    }

    public static List<TimeSpan> ParseTimes(string? raw)
    {
        var result = new List<TimeSpan>();
        if (string.IsNullOrWhiteSpace(raw)) return result;
        foreach (var s in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (TimeSpan.TryParseExact(s, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out var t))
                result.Add(t);
        return result;
    }
}
