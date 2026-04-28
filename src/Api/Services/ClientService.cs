using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class ClientService
{
    private readonly AppDbContext _db;
    private const string CodePrefix = "CLI-";

    public ClientService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<ClientDto>> GetAllAsync()
    {
        return await _db.Clients
            .OrderBy(c => c.Code)
            .Select(c => new ClientDto(
                c.Id, c.Code, c.Name, c.Cuit, c.Phone, c.Email, c.Address,
                c.ContactName, c.Notes, c.IsActive, c.CreatedAt, c.UpdatedAt))
            .ToListAsync();
    }

    public async Task<ClientDto?> GetByIdAsync(int id)
    {
        return await _db.Clients
            .Where(c => c.Id == id)
            .Select(c => new ClientDto(
                c.Id, c.Code, c.Name, c.Cuit, c.Phone, c.Email, c.Address,
                c.ContactName, c.Notes, c.IsActive, c.CreatedAt, c.UpdatedAt))
            .FirstOrDefaultAsync();
    }

    public async Task<ClientDto> CreateAsync(CreateClientRequest r)
    {
        var code = string.IsNullOrWhiteSpace(r.Code) ? await GenerateNextCodeAsync() : r.Code.Trim();
        await EnsureCodeIsUniqueAsync(code, ignoreId: null);

        var c = new Client
        {
            Code = code,
            Name = r.Name.Trim(),
            Cuit = string.IsNullOrWhiteSpace(r.Cuit) ? null : r.Cuit.Trim(),
            Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim(),
            Email = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim(),
            Address = string.IsNullOrWhiteSpace(r.Address) ? null : r.Address.Trim(),
            ContactName = string.IsNullOrWhiteSpace(r.ContactName) ? null : r.ContactName.Trim(),
            Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Clients.Add(c);
        await _db.SaveChangesAsync();
        return new ClientDto(c.Id, c.Code, c.Name, c.Cuit, c.Phone, c.Email, c.Address,
            c.ContactName, c.Notes, c.IsActive, c.CreatedAt, c.UpdatedAt);
    }

    public async Task<ClientDto?> UpdateAsync(int id, UpdateClientRequest r)
    {
        var c = await _db.Clients.FindAsync(id);
        if (c is null) return null;

        if (r.Code is not null)
        {
            var newCode = r.Code.Trim();
            if (string.IsNullOrEmpty(newCode))
                throw new InvalidOperationException("El codigo no puede estar vacio.");
            if (newCode != c.Code)
            {
                await EnsureCodeIsUniqueAsync(newCode, id);
                c.Code = newCode;
            }
        }
        if (r.Name is not null) c.Name = r.Name.Trim();
        if (r.Cuit is not null) c.Cuit = string.IsNullOrWhiteSpace(r.Cuit) ? null : r.Cuit.Trim();
        if (r.Phone is not null) c.Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim();
        if (r.Email is not null) c.Email = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim();
        if (r.Address is not null) c.Address = string.IsNullOrWhiteSpace(r.Address) ? null : r.Address.Trim();
        if (r.ContactName is not null) c.ContactName = string.IsNullOrWhiteSpace(r.ContactName) ? null : r.ContactName.Trim();
        if (r.Notes is not null) c.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes;
        if (r.IsActive.HasValue) c.IsActive = r.IsActive.Value;
        c.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return new ClientDto(c.Id, c.Code, c.Name, c.Cuit, c.Phone, c.Email, c.Address,
            c.ContactName, c.Notes, c.IsActive, c.CreatedAt, c.UpdatedAt);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var c = await _db.Clients.FindAsync(id);
        if (c is null) return false;
        _db.Clients.Remove(c);
        await _db.SaveChangesAsync();
        return true;
    }

    // --- Helpers ---

    private async Task<string> GenerateNextCodeAsync()
    {
        var existing = await _db.Clients
            .Where(c => c.Code.StartsWith(CodePrefix))
            .Select(c => c.Code)
            .ToListAsync();

        int max = 0;
        foreach (var code in existing)
        {
            var numPart = code.Substring(CodePrefix.Length);
            if (int.TryParse(numPart, out var num) && num > max) max = num;
        }
        return $"{CodePrefix}{(max + 1):D3}";
    }

    private async Task EnsureCodeIsUniqueAsync(string code, int? ignoreId)
    {
        var exists = await _db.Clients.AnyAsync(c => c.Code == code && (ignoreId == null || c.Id != ignoreId.Value));
        if (exists)
            throw new InvalidOperationException($"Ya existe un cliente con el codigo '{code}'.");
    }
}
