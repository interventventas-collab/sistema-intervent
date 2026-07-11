using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Maneja el (único) bot de Telegram. El token se guarda en texto (la API lo usa en runtime).
/// Nunca exponer el token al frontend — solo HasToken. Mismo molde que MpAccountService.
/// </summary>
public class TelegramAccountService
{
    private readonly AppDbContext _db;
    public TelegramAccountService(AppDbContext db) { _db = db; }

    public record TelegramAccountDto(int Id, string Proposito, bool HasToken, string? BotUsername, long? ChatId,
        string? VinculacionCode, bool IsActive, bool NotifVentas, bool NotifAlertas, bool NotifFichadas,
        bool LastSyncOk, string? LastError, DateTime? LastSyncAt,
        DateTime CreatedAt, DateTime? UpdatedAt);

    public record SaveTelegramAccountRequest(string? BotToken, bool IsActive = true,
        bool NotifVentas = true, bool NotifAlertas = true, bool NotifFichadas = true);

    private static TelegramAccountDto Map(TelegramAccount a) => new(
        a.Id, a.Proposito, !string.IsNullOrEmpty(a.BotToken), a.BotUsername, a.ChatId,
        a.VinculacionCode, a.IsActive, a.NotifVentas, a.NotifAlertas, a.NotifFichadas,
        a.LastSyncOk, a.LastError, a.LastSyncAt, a.CreatedAt, a.UpdatedAt);

    private static string GenerarCodigo() => Random.Shared.Next(100000, 1000000).ToString();

    private static string NormProposito(string? p)
        => string.Equals(p?.Trim(), "PREVENTAS", StringComparison.OrdinalIgnoreCase) ? "PREVENTAS" : "AVISOS";

    public async Task<TelegramAccountDto?> GetAsync(string proposito = "AVISOS")
    {
        var p = NormProposito(proposito);
        var a = await _db.TelegramAccounts.Where(x => x.Proposito == p).OrderBy(x => x.Id).FirstOrDefaultAsync();
        return a is null ? null : Map(a);
    }

    public async Task<TelegramAccount?> GetEntityAsync(string proposito = "AVISOS")
    {
        var p = NormProposito(proposito);
        return await _db.TelegramAccounts.Where(x => x.Proposito == p).OrderBy(x => x.Id).FirstOrDefaultAsync();
    }

    /// <summary>Desvincula el chat (ChatId=null) y genera un código nuevo. Después, para volver a
    /// usar el bot, hay que mandarle ese código nuevo. Útil para cambiar de celular o por seguridad.</summary>
    public async Task<TelegramAccountDto?> DesvincularAsync(string proposito = "AVISOS")
    {
        var a = await GetEntityAsync(proposito);
        if (a is null) return null;
        a.ChatId = null;
        a.LastUpdateId = null;
        a.VinculacionCode = GenerarCodigo();
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Map(a);
    }

    public async Task<(bool ok, string? error, TelegramAccountDto? dto)> SaveAsync(SaveTelegramAccountRequest req, string proposito = "AVISOS")
    {
        var p = NormProposito(proposito);
        var a = await _db.TelegramAccounts.Where(x => x.Proposito == p).OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null)
        {
            if (string.IsNullOrWhiteSpace(req.BotToken))
                return (false, "El token del bot es obligatorio la primera vez", null);
            a = new TelegramAccount
            {
                Proposito = p,
                BotToken = req.BotToken.Trim(),
                VinculacionCode = GenerarCodigo(),
                IsActive = req.IsActive,
                NotifVentas = req.NotifVentas,
                NotifAlertas = req.NotifAlertas,
                NotifFichadas = req.NotifFichadas,
                CreatedAt = DateTime.UtcNow
            };
            _db.TelegramAccounts.Add(a);
        }
        else
        {
            // El token solo se pisa si viene uno nuevo (el frontend manda vacío para no cambiarlo).
            if (!string.IsNullOrWhiteSpace(req.BotToken))
            {
                var nuevo = req.BotToken.Trim();
                if (nuevo != a.BotToken)
                {
                    a.BotToken = nuevo;
                    // Cambió el bot: reseteamos lo asociado al bot viejo.
                    a.BotUsername = null;
                    a.ChatId = null;
                    a.LastUpdateId = null;
                }
            }
            a.IsActive = req.IsActive;
            a.NotifVentas = req.NotifVentas;
            a.NotifAlertas = req.NotifAlertas;
            a.NotifFichadas = req.NotifFichadas;
            a.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return (true, null, Map(a));
    }
}
