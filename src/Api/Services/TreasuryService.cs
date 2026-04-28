using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class TreasuryService
{
    private readonly AppDbContext _db;
    private const string CodePrefix = "CTA-";
    private static readonly string[] AllowedTypes = { "caja", "banco", "billetera", "otro" };
    private static readonly string[] AllowedMovementTypes = { "ingreso", "egreso", "transferencia" };

    public TreasuryService(AppDbContext db)
    {
        _db = db;
    }

    // ===== ACCOUNTS =====

    public async Task<List<TreasuryAccountDto>> GetAccountsAsync()
    {
        var accounts = await _db.TreasuryAccounts
            .OrderBy(a => a.Code).ToListAsync();
        if (accounts.Count == 0) return new();

        var ids = accounts.Select(a => a.Id).ToList();
        var movs = await _db.TreasuryMovements
            .Where(m => ids.Contains(m.AccountId))
            .GroupBy(m => new { m.AccountId, m.MovementType })
            .Select(g => new { g.Key.AccountId, g.Key.MovementType, Sum = g.Sum(x => x.Amount) })
            .ToListAsync();

        return accounts.Select(a =>
        {
            var ingresos = movs.Where(m => m.AccountId == a.Id && m.MovementType == "ingreso").Sum(m => m.Sum);
            var egresos = movs.Where(m => m.AccountId == a.Id && m.MovementType == "egreso").Sum(m => m.Sum);
            var current = a.InitialBalance + ingresos - egresos;
            return new TreasuryAccountDto(a.Id, a.Code, a.Name, a.AccountType, a.Currency,
                a.InitialBalance, current, a.Notes, a.IsActive, a.CreatedAt, a.UpdatedAt);
        }).ToList();
    }

    public async Task<TreasuryAccountDto?> GetAccountAsync(int id)
    {
        var all = await GetAccountsAsync();
        return all.FirstOrDefault(a => a.Id == id);
    }

    public async Task<TreasuryAccountDto> CreateAccountAsync(CreateTreasuryAccountRequest r)
    {
        ValidateAccountType(r.AccountType);
        var code = string.IsNullOrWhiteSpace(r.Code) ? await GenerateNextAccountCodeAsync() : r.Code.Trim();
        if (await _db.TreasuryAccounts.AnyAsync(a => a.Code == code))
            throw new InvalidOperationException($"Ya existe una cuenta con codigo '{code}'.");

        var entity = new TreasuryAccount
        {
            Code = code,
            Name = r.Name.Trim(),
            AccountType = r.AccountType,
            Currency = string.IsNullOrWhiteSpace(r.Currency) ? "ARS" : r.Currency.Trim(),
            InitialBalance = r.InitialBalance,
            Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.TreasuryAccounts.Add(entity);
        await _db.SaveChangesAsync();
        return (await GetAccountAsync(entity.Id))!;
    }

    public async Task<TreasuryAccountDto?> UpdateAccountAsync(int id, UpdateTreasuryAccountRequest r)
    {
        var a = await _db.TreasuryAccounts.FindAsync(id);
        if (a is null) return null;
        if (r.Code is not null)
        {
            var newCode = r.Code.Trim();
            if (newCode != a.Code && await _db.TreasuryAccounts.AnyAsync(x => x.Id != id && x.Code == newCode))
                throw new InvalidOperationException($"Ya existe una cuenta con codigo '{newCode}'.");
            a.Code = newCode;
        }
        if (r.Name is not null) a.Name = r.Name.Trim();
        if (r.AccountType is not null) { ValidateAccountType(r.AccountType); a.AccountType = r.AccountType; }
        if (r.Currency is not null) a.Currency = r.Currency.Trim();
        if (r.InitialBalance.HasValue) a.InitialBalance = r.InitialBalance.Value;
        if (r.Notes is not null) a.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
        if (r.IsActive.HasValue) a.IsActive = r.IsActive.Value;
        a.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetAccountAsync(a.Id);
    }

    public async Task<bool> DeleteAccountAsync(int id)
    {
        var a = await _db.TreasuryAccounts.FindAsync(id);
        if (a is null) return false;
        _db.TreasuryAccounts.Remove(a);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== MOVEMENTS =====

    public async Task<List<TreasuryMovementDto>> GetMovementsAsync(int? accountId = null, DateTime? from = null, DateTime? to = null)
    {
        var query = _db.TreasuryMovements
            .Include(m => m.Account)
            .AsQueryable();

        if (accountId.HasValue) query = query.Where(m => m.AccountId == accountId.Value);
        if (from.HasValue) query = query.Where(m => m.Date >= from.Value);
        if (to.HasValue) query = query.Where(m => m.Date <= to.Value);

        var movs = await query.OrderByDescending(m => m.Date).ThenByDescending(m => m.Id).ToListAsync();
        var relIds = movs.Where(m => m.RelatedAccountId.HasValue).Select(m => m.RelatedAccountId!.Value).Distinct().ToList();
        var related = relIds.Count == 0 ? new Dictionary<int, string>()
            : await _db.TreasuryAccounts.Where(a => relIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Name);

        return movs.Select(m => new TreasuryMovementDto(
            m.Id, m.AccountId, m.Account?.Name ?? "?",
            m.Date, m.MovementType, m.Concept, m.Description, m.Amount,
            m.RelatedAccountId,
            m.RelatedAccountId.HasValue && related.ContainsKey(m.RelatedAccountId.Value) ? related[m.RelatedAccountId.Value] : null,
            m.CreatedAt
        )).ToList();
    }

    public async Task<List<TreasuryMovementDto>> CreateMovementAsync(CreateTreasuryMovementRequest r)
    {
        if (!AllowedMovementTypes.Contains(r.MovementType))
            throw new InvalidOperationException($"Tipo de movimiento invalido: '{r.MovementType}'.");
        if (r.Amount <= 0)
            throw new InvalidOperationException("El importe debe ser mayor a 0.");

        var date = r.Date ?? DateTime.UtcNow;
        var account = await _db.TreasuryAccounts.FindAsync(r.AccountId)
            ?? throw new InvalidOperationException("Cuenta no encontrada.");

        if (r.MovementType == "transferencia")
        {
            if (!r.RelatedAccountId.HasValue || r.RelatedAccountId.Value == r.AccountId)
                throw new InvalidOperationException("Para una transferencia tenes que indicar la cuenta destino (distinta de la origen).");
            var dest = await _db.TreasuryAccounts.FindAsync(r.RelatedAccountId.Value)
                ?? throw new InvalidOperationException("Cuenta destino no encontrada.");
            var groupId = Guid.NewGuid();
            var egreso = new TreasuryMovement
            {
                AccountId = account.Id, Date = date, MovementType = "egreso",
                Concept = r.Concept ?? "Transferencia",
                Description = r.Description, Amount = r.Amount,
                RelatedAccountId = dest.Id, TransferGroupId = groupId,
                CreatedAt = DateTime.UtcNow
            };
            var ingreso = new TreasuryMovement
            {
                AccountId = dest.Id, Date = date, MovementType = "ingreso",
                Concept = r.Concept ?? "Transferencia",
                Description = r.Description, Amount = r.Amount,
                RelatedAccountId = account.Id, TransferGroupId = groupId,
                CreatedAt = DateTime.UtcNow
            };
            _db.TreasuryMovements.AddRange(egreso, ingreso);
            await _db.SaveChangesAsync();
            return await GetMovementsByGroupAsync(groupId);
        }

        var mov = new TreasuryMovement
        {
            AccountId = account.Id, Date = date, MovementType = r.MovementType,
            Concept = r.Concept, Description = r.Description, Amount = r.Amount,
            CreatedAt = DateTime.UtcNow
        };
        _db.TreasuryMovements.Add(mov);
        await _db.SaveChangesAsync();
        return new List<TreasuryMovementDto> { (await GetMovementByIdAsync(mov.Id))! };
    }

    public async Task<bool> DeleteMovementAsync(int id)
    {
        var m = await _db.TreasuryMovements.FindAsync(id);
        if (m is null) return false;
        // Si es parte de una transferencia, eliminar las dos partes
        if (m.TransferGroupId.HasValue)
        {
            var pair = await _db.TreasuryMovements.Where(x => x.TransferGroupId == m.TransferGroupId).ToListAsync();
            _db.TreasuryMovements.RemoveRange(pair);
        }
        else
        {
            _db.TreasuryMovements.Remove(m);
        }
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== HELPERS =====

    private static void ValidateAccountType(string type)
    {
        if (!AllowedTypes.Contains(type))
            throw new InvalidOperationException($"Tipo de cuenta invalido: '{type}'. Aceptados: {string.Join(", ", AllowedTypes)}.");
    }

    private async Task<string> GenerateNextAccountCodeAsync()
    {
        var existing = await _db.TreasuryAccounts
            .Where(a => a.Code.StartsWith(CodePrefix))
            .Select(a => a.Code).ToListAsync();
        int max = 0;
        foreach (var code in existing)
        {
            var num = code.Substring(CodePrefix.Length);
            if (int.TryParse(num, out var n) && n > max) max = n;
        }
        return $"{CodePrefix}{(max + 1):D3}";
    }

    private async Task<TreasuryMovementDto?> GetMovementByIdAsync(int id)
    {
        var movs = await GetMovementsAsync();
        return movs.FirstOrDefault(m => m.Id == id);
    }

    private async Task<List<TreasuryMovementDto>> GetMovementsByGroupAsync(Guid groupId)
    {
        var ids = await _db.TreasuryMovements
            .Where(m => m.TransferGroupId == groupId)
            .Select(m => m.Id).ToListAsync();
        var all = await GetMovementsAsync();
        return all.Where(m => ids.Contains(m.Id)).ToList();
    }

    // Util para que PayrollService registre el pago como egreso.
    public async Task<TreasuryMovement> RegisterEgresoAsync(int accountId, decimal amount, string concept, string description, int? relatedEmployeeId = null)
    {
        var account = await _db.TreasuryAccounts.FindAsync(accountId)
            ?? throw new InvalidOperationException("Cuenta de pago no encontrada.");
        var mov = new TreasuryMovement
        {
            AccountId = account.Id, Date = DateTime.UtcNow, MovementType = "egreso",
            Concept = concept, Description = description, Amount = amount,
            RelatedEmployeeId = relatedEmployeeId,
            CreatedAt = DateTime.UtcNow
        };
        _db.TreasuryMovements.Add(mov);
        await _db.SaveChangesAsync();
        return mov;
    }
}
