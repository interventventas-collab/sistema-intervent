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

    public record TelegramAccountDto(int Id, bool HasToken, string? BotUsername, long? ChatId,
        bool IsActive, bool NotifVentas, bool NotifAlertas,
        bool LastSyncOk, string? LastError, DateTime? LastSyncAt,
        DateTime CreatedAt, DateTime? UpdatedAt);

    public record SaveTelegramAccountRequest(string? BotToken, bool IsActive = true,
        bool NotifVentas = true, bool NotifAlertas = true);

    private static TelegramAccountDto Map(TelegramAccount a) => new(
        a.Id, !string.IsNullOrEmpty(a.BotToken), a.BotUsername, a.ChatId,
        a.IsActive, a.NotifVentas, a.NotifAlertas,
        a.LastSyncOk, a.LastError, a.LastSyncAt, a.CreatedAt, a.UpdatedAt);

    public async Task<TelegramAccountDto?> GetAsync()
    {
        var a = await _db.TelegramAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        return a is null ? null : Map(a);
    }

    public async Task<TelegramAccount?> GetEntityAsync()
        => await _db.TelegramAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();

    public async Task<(bool ok, string? error, TelegramAccountDto? dto)> SaveAsync(SaveTelegramAccountRequest req)
    {
        var a = await _db.TelegramAccounts.OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (a is null)
        {
            if (string.IsNullOrWhiteSpace(req.BotToken))
                return (false, "El token del bot es obligatorio la primera vez", null);
            a = new TelegramAccount
            {
                BotToken = req.BotToken.Trim(),
                IsActive = req.IsActive,
                NotifVentas = req.NotifVentas,
                NotifAlertas = req.NotifAlertas,
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
            a.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return (true, null, Map(a));
    }
}
