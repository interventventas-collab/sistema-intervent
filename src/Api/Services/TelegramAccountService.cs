using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Maneja los bots de Telegram (una fila por propósito: AVISOS / FUNCIONES / PREVENTAS) y las
/// personas vinculadas a cada uno (TelegramChats). El token se guarda en texto (la API lo usa en
/// runtime). Nunca exponer el token al frontend — solo HasToken. Mismo molde que MpAccountService.
/// </summary>
public class TelegramAccountService
{
    private readonly AppDbContext _db;
    public TelegramAccountService(AppDbContext db) { _db = db; }

    public record TelegramAccountDto(int Id, string Proposito, bool HasToken, string? BotUsername, long? ChatId,
        string? VinculacionCode, bool IsActive, bool NotifVentas, bool NotifAlertas, bool NotifFichadas,
        bool LastSyncOk, string? LastError, DateTime? LastSyncAt,
        DateTime CreatedAt, DateTime? UpdatedAt, int PersonasVinculadas);

    public record SaveTelegramAccountRequest(string? BotToken, bool IsActive = true,
        bool NotifVentas = true, bool NotifAlertas = true, bool NotifFichadas = true);

    public record TelegramChatDto(int Id, long ChatId, string? Nombre,
        bool NotifVentas, bool NotifAlertas, bool NotifFichadas, DateTime CreatedAt);

    public record UpdateTelegramChatRequest(string? Nombre, bool NotifVentas, bool NotifAlertas, bool NotifFichadas);

    private async Task<TelegramAccountDto> MapAsync(TelegramAccount a)
    {
        var personas = await _db.TelegramChats.CountAsync(c => c.TelegramAccountId == a.Id);
        return new(
            a.Id, a.Proposito, !string.IsNullOrEmpty(a.BotToken), a.BotUsername, a.ChatId,
            a.VinculacionCode, a.IsActive, a.NotifVentas, a.NotifAlertas, a.NotifFichadas,
            a.LastSyncOk, a.LastError, a.LastSyncAt, a.CreatedAt, a.UpdatedAt, personas);
    }

    private static TelegramChatDto MapChat(TelegramChat c)
        => new(c.Id, c.ChatId, c.Nombre, c.NotifVentas, c.NotifAlertas, c.NotifFichadas, c.CreatedAt);

    private static string GenerarCodigo() => Random.Shared.Next(100000, 1000000).ToString();

    private static string NormProposito(string? p)
    {
        var t = p?.Trim().ToUpperInvariant();
        return t is "PREVENTAS" or "FUNCIONES" ? t : "AVISOS";
    }

    public async Task<TelegramAccountDto?> GetAsync(string proposito = "AVISOS")
    {
        var a = await GetEntityAsync(proposito);
        return a is null ? null : await MapAsync(a);
    }

    public async Task<TelegramAccount?> GetEntityAsync(string proposito = "AVISOS")
    {
        var p = NormProposito(proposito);
        return await _db.TelegramAccounts.Where(x => x.Proposito == p).OrderBy(x => x.Id).FirstOrDefaultAsync();
    }

    // ─────────── Personas vinculadas (TelegramChats) ───────────

    public async Task<List<TelegramChatDto>> ListChatsAsync(string proposito = "AVISOS")
    {
        var a = await GetEntityAsync(proposito);
        if (a is null) return new();
        return await _db.TelegramChats.Where(c => c.TelegramAccountId == a.Id)
            .OrderBy(c => c.Id)
            .Select(c => new TelegramChatDto(c.Id, c.ChatId, c.Nombre, c.NotifVentas, c.NotifAlertas, c.NotifFichadas, c.CreatedAt))
            .ToListAsync();
    }

    /// <summary>Edita a una persona vinculada: nombre y qué avisos le llegan.</summary>
    public async Task<TelegramChatDto?> UpdateChatAsync(int id, UpdateTelegramChatRequest req)
    {
        var c = await _db.TelegramChats.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return null;
        c.Nombre = string.IsNullOrWhiteSpace(req.Nombre) ? null : req.Nombre.Trim();
        c.NotifVentas = req.NotifVentas;
        c.NotifAlertas = req.NotifAlertas;
        c.NotifFichadas = req.NotifFichadas;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return MapChat(c);
    }

    /// <summary>Quita a una persona: deja de recibir avisos y el bot no le contesta más.</summary>
    public async Task<bool> DeleteChatAsync(int id)
    {
        var c = await _db.TelegramChats.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return false;
        _db.TelegramChats.Remove(c);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Genera un código de seguridad nuevo (el viejo deja de servir para vincularse).</summary>
    public async Task<TelegramAccountDto?> RegenerarCodigoAsync(string proposito = "AVISOS")
    {
        var a = await GetEntityAsync(proposito);
        if (a is null) return null;
        a.VinculacionCode = GenerarCodigo();
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await MapAsync(a);
    }

    /// <summary>Desvincula el bot. En PREVENTAS: borra el chat del dueño (ChatId=null). En
    /// AVISOS/FUNCIONES: quita a TODAS las personas vinculadas. Siempre genera código nuevo.</summary>
    public async Task<TelegramAccountDto?> DesvincularAsync(string proposito = "AVISOS")
    {
        var a = await GetEntityAsync(proposito);
        if (a is null) return null;
        a.ChatId = null;
        a.LastUpdateId = null;
        a.VinculacionCode = GenerarCodigo();
        a.UpdatedAt = DateTime.UtcNow;
        var chats = await _db.TelegramChats.Where(c => c.TelegramAccountId == a.Id).ToListAsync();
        if (chats.Count > 0) _db.TelegramChats.RemoveRange(chats);
        await _db.SaveChangesAsync();
        return await MapAsync(a);
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
                    // Cambió el bot: reseteamos lo asociado al bot viejo (incluidas las personas,
                    // que estaban vinculadas al bot anterior).
                    a.BotUsername = null;
                    a.ChatId = null;
                    a.LastUpdateId = null;
                    var viejos = await _db.TelegramChats.Where(c => c.TelegramAccountId == a.Id).ToListAsync();
                    if (viejos.Count > 0) _db.TelegramChats.RemoveRange(viejos);
                }
            }
            a.IsActive = req.IsActive;
            a.NotifVentas = req.NotifVentas;
            a.NotifAlertas = req.NotifAlertas;
            a.NotifFichadas = req.NotifFichadas;
            a.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return (true, null, await MapAsync(a));
    }
}
