using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Maneja la (única) cuenta de Shell Flota. La clave se guarda en texto (el scraper
/// la usa). Nunca exponer la clave en un DTO al frontend.
/// </summary>
public class ShellAccountService
{
    private readonly AppDbContext _db;
    public ShellAccountService(AppDbContext db) { _db = db; }

    public record ShellAccountDto(int Id, string Usuario, string? Alias, bool HasPassword, bool IsActive,
        string? LastSaldo, DateTime? LastSaldoAt, bool LastSyncOk, string? LastError,
        bool AutoSyncEnabled, string? AutoSyncTimes, DateTime? LastAutoSyncAt,
        DateTime CreatedAt, DateTime? UpdatedAt);

    public record SaveShellAccountRequest(string Usuario, string? Password, string? Alias, bool IsActive,
        bool AutoSyncEnabled = false, string? AutoSyncTimes = null);

    private static ShellAccountDto Map(ShellAccount a) => new(
        a.Id, a.Usuario, string.IsNullOrEmpty(a.Alias) ? null : a.Alias,
        !string.IsNullOrEmpty(a.Password), a.IsActive,
        a.LastSaldo, a.LastSaldoAt, a.LastSyncOk, a.LastError,
        a.AutoSyncEnabled, a.AutoSyncTimes, a.LastAutoSyncAt, a.CreatedAt, a.UpdatedAt);

    public async Task<ShellAccountDto?> GetAsync()
    {
        var a = await _db.ShellAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        return a is null ? null : Map(a);
    }

    public async Task<string?> GetPasswordAsync()
    {
        var a = await _db.ShellAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        return a?.Password;
    }

    public async Task<ShellAccount?> GetEntityAsync()
        => await _db.ShellAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();

    public async Task GuardarSaldoAsync(string? saldo, bool ok, string? error)
    {
        var a = await _db.ShellAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null) return;
        if (ok && !string.IsNullOrEmpty(saldo)) { a.LastSaldo = saldo; a.LastSaldoAt = DateTime.UtcNow; }
        a.LastSyncOk = ok;
        a.LastError = error is null ? null : (error.Length > 500 ? error.Substring(0, 500) : error);
        await _db.SaveChangesAsync();
    }

    public async Task MarcarAutoSyncAsync(DateTime whenUtc)
    {
        var a = await _db.ShellAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null) return;
        a.LastAutoSyncAt = whenUtc;
        await _db.SaveChangesAsync();
    }

    public async Task<(bool ok, string? error, ShellAccountDto? dto)> SaveAsync(SaveShellAccountRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Usuario))
            return (false, "El usuario es obligatorio", null);

        var a = await _db.ShellAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null)
        {
            if (string.IsNullOrWhiteSpace(req.Password))
                return (false, "La clave es obligatoria la primera vez", null);
            a = new ShellAccount
            {
                Usuario = req.Usuario.Trim(),
                Password = req.Password,
                Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim(),
                IsActive = req.IsActive,
                AutoSyncEnabled = req.AutoSyncEnabled,
                AutoSyncTimes = NormTimes(req.AutoSyncTimes),
                CreatedAt = DateTime.UtcNow
            };
            _db.ShellAccounts.Add(a);
        }
        else
        {
            a.Usuario = req.Usuario.Trim();
            if (!string.IsNullOrWhiteSpace(req.Password)) a.Password = req.Password;
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
