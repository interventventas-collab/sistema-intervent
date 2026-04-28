using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class SupplierService
{
    private readonly AppDbContext _db;
    private const string CodePrefix = "PROV-";

    public SupplierService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<SupplierDto>> GetAllAsync()
    {
        return await _db.Suppliers
            .OrderBy(s => s.Code)
            .Select(s => new SupplierDto(
                s.Id, s.Code, s.Name, s.Cuit, s.Phone, s.Email, s.Address,
                s.ContactName, s.Notes, s.IsActive, s.CreatedAt, s.UpdatedAt))
            .ToListAsync();
    }

    public async Task<SupplierDto?> GetByIdAsync(int id)
    {
        return await _db.Suppliers
            .Where(s => s.Id == id)
            .Select(s => new SupplierDto(
                s.Id, s.Code, s.Name, s.Cuit, s.Phone, s.Email, s.Address,
                s.ContactName, s.Notes, s.IsActive, s.CreatedAt, s.UpdatedAt))
            .FirstOrDefaultAsync();
    }

    public async Task<SupplierDto> CreateAsync(CreateSupplierRequest r)
    {
        var code = string.IsNullOrWhiteSpace(r.Code)
            ? await GenerateNextCodeAsync()
            : r.Code.Trim();
        await EnsureCodeIsUniqueAsync(code, ignoreId: null);

        var s = new Supplier
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
        _db.Suppliers.Add(s);
        await _db.SaveChangesAsync();
        return new SupplierDto(s.Id, s.Code, s.Name, s.Cuit, s.Phone, s.Email, s.Address,
            s.ContactName, s.Notes, s.IsActive, s.CreatedAt, s.UpdatedAt);
    }

    public async Task<SupplierDto?> UpdateAsync(int id, UpdateSupplierRequest r)
    {
        var s = await _db.Suppliers.FindAsync(id);
        if (s is null) return null;

        if (r.Code is not null)
        {
            var newCode = r.Code.Trim();
            if (string.IsNullOrEmpty(newCode))
                throw new InvalidOperationException("El codigo no puede estar vacio.");
            if (newCode != s.Code)
            {
                await EnsureCodeIsUniqueAsync(newCode, id);
                s.Code = newCode;
            }
        }
        if (r.Name is not null) s.Name = r.Name.Trim();
        if (r.Cuit is not null) s.Cuit = string.IsNullOrWhiteSpace(r.Cuit) ? null : r.Cuit.Trim();
        if (r.Phone is not null) s.Phone = string.IsNullOrWhiteSpace(r.Phone) ? null : r.Phone.Trim();
        if (r.Email is not null) s.Email = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim();
        if (r.Address is not null) s.Address = string.IsNullOrWhiteSpace(r.Address) ? null : r.Address.Trim();
        if (r.ContactName is not null) s.ContactName = string.IsNullOrWhiteSpace(r.ContactName) ? null : r.ContactName.Trim();
        if (r.Notes is not null) s.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes;
        if (r.IsActive.HasValue) s.IsActive = r.IsActive.Value;
        s.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return new SupplierDto(s.Id, s.Code, s.Name, s.Cuit, s.Phone, s.Email, s.Address,
            s.ContactName, s.Notes, s.IsActive, s.CreatedAt, s.UpdatedAt);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var s = await _db.Suppliers.FindAsync(id);
        if (s is null) return false;
        _db.Suppliers.Remove(s);
        await _db.SaveChangesAsync();
        return true;
    }

    // --- Helpers ---

    private async Task<string> GenerateNextCodeAsync()
    {
        var existing = await _db.Suppliers
            .Where(s => s.Code.StartsWith(CodePrefix))
            .Select(s => s.Code)
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
        var exists = await _db.Suppliers.AnyAsync(s => s.Code == code && (ignoreId == null || s.Id != ignoreId.Value));
        if (exists)
            throw new InvalidOperationException($"Ya existe un proveedor con el codigo '{code}'.");
    }
}
