using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Maneja la (única) cuenta de EZVIZ. El appKey/appSecret se guardan en texto (la API los
/// usa para pedir el token). Nunca exponer el appSecret al frontend — solo HasCredentials.
/// Mismo molde que MpAccountService.
/// </summary>
public class EzvizAccountService
{
    private readonly AppDbContext _db;
    public EzvizAccountService(AppDbContext db) { _db = db; }

    public record EzvizAccountDto(int Id, string? Alias, bool HasCredentials, bool IsActive,
        string ApiHost, string? AreaDomain, DateTime? TokenExpiresAt,
        bool LastSyncOk, string? LastError, DateTime? LastSyncAt,
        DateTime CreatedAt, DateTime? UpdatedAt);

    public record SaveEzvizAccountRequest(string? AppKey, string? AppSecret, string? Alias,
        bool IsActive = true, string? ApiHost = null);

    private static EzvizAccountDto Map(EzvizAccount a) => new(
        a.Id, string.IsNullOrEmpty(a.Alias) ? null : a.Alias,
        !string.IsNullOrEmpty(a.AppKey) && !string.IsNullOrEmpty(a.AppSecret), a.IsActive,
        a.ApiHost, a.AreaDomain, a.TokenExpiresAt,
        a.LastSyncOk, a.LastError, a.LastSyncAt, a.CreatedAt, a.UpdatedAt);

    public async Task<EzvizAccountDto?> GetAsync()
    {
        var a = await _db.EzvizAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        return a is null ? null : Map(a);
    }

    public async Task<EzvizAccount?> GetEntityAsync()
        => await _db.EzvizAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();

    /// <summary>Guarda el token recién obtenido (+ areaDomain + expiración) en la cuenta.</summary>
    public async Task GuardarTokenAsync(string token, DateTime expiresAt, string? areaDomain)
    {
        var a = await _db.EzvizAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null) return;
        a.AccessToken = token;
        a.TokenExpiresAt = expiresAt;
        if (!string.IsNullOrWhiteSpace(areaDomain)) a.AreaDomain = areaDomain.Trim();
        await _db.SaveChangesAsync();
    }

    /// <summary>Marca el resultado del último intento de conexión (ok o error) para mostrarlo.</summary>
    public async Task MarcarResultadoAsync(bool ok, string? error)
    {
        var a = await _db.EzvizAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null) return;
        a.LastSyncOk = ok;
        a.LastError = error is null ? null : (error.Length > 500 ? error.Substring(0, 500) : error);
        a.LastSyncAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<(bool ok, string? error, EzvizAccountDto? dto)> SaveAsync(SaveEzvizAccountRequest req)
    {
        var a = await _db.EzvizAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        var host = string.IsNullOrWhiteSpace(req.ApiHost) ? null : req.ApiHost.Trim().TrimEnd('/');
        if (a is null)
        {
            if (string.IsNullOrWhiteSpace(req.AppKey) || string.IsNullOrWhiteSpace(req.AppSecret))
                return (false, "El appKey y el appSecret son obligatorios la primera vez", null);
            a = new EzvizAccount
            {
                AppKey = req.AppKey.Trim(),
                AppSecret = req.AppSecret.Trim(),
                Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim(),
                IsActive = req.IsActive,
                ApiHost = string.IsNullOrEmpty(host) ? "https://open.ezvizlife.com" : host,
                CreatedAt = DateTime.UtcNow
            };
            _db.EzvizAccounts.Add(a);
        }
        else
        {
            // Las credenciales solo se pisan si vienen nuevas (el frontend manda vacío para no cambiarlas).
            if (!string.IsNullOrWhiteSpace(req.AppKey)) a.AppKey = req.AppKey.Trim();
            if (!string.IsNullOrWhiteSpace(req.AppSecret)) a.AppSecret = req.AppSecret.Trim();
            a.Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim();
            a.IsActive = req.IsActive;
            if (!string.IsNullOrEmpty(host)) a.ApiHost = host;
            // Cambió alguna credencial/host: invalidamos el token cacheado para que se vuelva a pedir.
            if (!string.IsNullOrWhiteSpace(req.AppKey) || !string.IsNullOrWhiteSpace(req.AppSecret) || !string.IsNullOrEmpty(host))
            {
                a.AccessToken = null;
                a.TokenExpiresAt = null;
                a.AreaDomain = null;
            }
            a.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return (true, null, Map(a));
    }
}
