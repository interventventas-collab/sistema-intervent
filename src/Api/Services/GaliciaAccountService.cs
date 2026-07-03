using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Maneja la (única) cuenta del Office Banking de Galicia para login automatizado.
/// La clave se guarda en texto en la DB (el scraper la necesita en runtime; mismo
/// criterio que ArcaAccountService). Nunca exponer la clave en un DTO al frontend.
/// </summary>
public class GaliciaAccountService
{
    private readonly AppDbContext _db;

    public GaliciaAccountService(AppDbContext db) { _db = db; }

    public record GaliciaAccountDto(int Id, string Usuario, string? Alias, bool HasPassword,
        bool IsActive, bool AutoSyncEnabled, string? AutoSyncTimes, DateTime? LastAutoSyncAt,
        DateTime CreatedAt, DateTime? UpdatedAt);

    public record SaveGaliciaAccountRequest(string Usuario, string? Password, string? Alias, bool IsActive,
        bool AutoSyncEnabled = false, string? AutoSyncTimes = null);

    private static GaliciaAccountDto Map(GaliciaAccount a) => new(
        a.Id, a.Usuario, string.IsNullOrEmpty(a.Alias) ? null : a.Alias,
        !string.IsNullOrEmpty(a.Password), a.IsActive, a.AutoSyncEnabled, a.AutoSyncTimes, a.LastAutoSyncAt,
        a.CreatedAt, a.UpdatedAt);

    /// <summary>Devuelve la cuenta principal (la primera), o null si no hay ninguna cargada.</summary>
    public async Task<GaliciaAccountDto?> GetAsync()
    {
        var a = await _db.GaliciaAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        return a is null ? null : Map(a);
    }

    /// <summary>Clave en claro. SOLO para el scraper — nunca al frontend.</summary>
    public async Task<string?> GetPasswordAsync()
    {
        var a = await _db.GaliciaAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        return a?.Password;
    }

    /// <summary>Entidad cruda (para el job de auto-sync). Incluye clave — no exponer al frontend.</summary>
    public async Task<GaliciaAccount?> GetEntityAsync()
        => await _db.GaliciaAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();

    /// <summary>Marca la última corrida automática (UTC).</summary>
    public async Task MarcarAutoSyncAsync(DateTime whenUtc)
    {
        var a = await _db.GaliciaAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null) return;
        a.LastAutoSyncAt = whenUtc;
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Crea o actualiza la cuenta principal. Si Password viene vacío en una
    /// actualización, se mantiene la clave existente.
    /// </summary>
    public async Task<(bool ok, string? error, GaliciaAccountDto? dto)> SaveAsync(SaveGaliciaAccountRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Usuario))
            return (false, "El usuario es obligatorio", null);

        var a = await _db.GaliciaAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null)
        {
            if (string.IsNullOrWhiteSpace(req.Password))
                return (false, "La clave es obligatoria la primera vez", null);
            a = new GaliciaAccount
            {
                Usuario = req.Usuario.Trim(),
                Password = req.Password,
                Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim(),
                IsActive = req.IsActive,
                AutoSyncEnabled = req.AutoSyncEnabled,
                AutoSyncTimes = NormTimes(req.AutoSyncTimes),
                CreatedAt = DateTime.UtcNow
            };
            _db.GaliciaAccounts.Add(a);
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

    /// <summary>Normaliza "HH:mm,HH:mm,...": valida cada hora, deja las válidas ordenadas y sin repetir.</summary>
    private static string? NormTimes(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var valid = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => TimeSpan.TryParseExact(s, "hh\\:mm", System.Globalization.CultureInfo.InvariantCulture, out _))
            .Distinct()
            .OrderBy(s => s)
            .ToList();
        return valid.Count == 0 ? null : string.Join(",", valid);
    }

    /// <summary>Parsea "HH:mm,..." a lista de TimeSpan (para el job). Ignora inválidos.</summary>
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
