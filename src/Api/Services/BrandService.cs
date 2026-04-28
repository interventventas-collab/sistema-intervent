using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class BrandService
{
    private readonly AppDbContext _db;
    private const string CodePrefix = "MAR-";

    public BrandService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<BrandDto>> GetAllAsync()
    {
        return await _db.Brands
            .OrderBy(b => b.Code)
            .Select(b => new BrandDto(b.Id, b.Code, b.Name, b.Description, b.IsActive, b.CreatedAt, b.UpdatedAt))
            .ToListAsync();
    }

    public async Task<BrandDto?> GetByIdAsync(int id)
    {
        return await _db.Brands
            .Where(b => b.Id == id)
            .Select(b => new BrandDto(b.Id, b.Code, b.Name, b.Description, b.IsActive, b.CreatedAt, b.UpdatedAt))
            .FirstOrDefaultAsync();
    }

    public async Task<BrandDto> CreateAsync(CreateBrandRequest r)
    {
        var name = r.Name.Trim();
        if (await _db.Brands.AnyAsync(b => b.Name == name))
            throw new InvalidOperationException($"Ya existe una marca con el nombre '{name}'.");

        var code = string.IsNullOrWhiteSpace(r.Code) ? await GenerateNextCodeAsync() : r.Code.Trim();
        await EnsureCodeIsUniqueAsync(code, ignoreId: null);

        var b = new Brand
        {
            Code = code,
            Name = name,
            Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Brands.Add(b);
        await _db.SaveChangesAsync();
        return new BrandDto(b.Id, b.Code, b.Name, b.Description, b.IsActive, b.CreatedAt, b.UpdatedAt);
    }

    public async Task<BrandDto?> UpdateAsync(int id, UpdateBrandRequest r)
    {
        var b = await _db.Brands.FindAsync(id);
        if (b is null) return null;

        if (r.Code is not null)
        {
            var newCode = r.Code.Trim();
            if (string.IsNullOrEmpty(newCode))
                throw new InvalidOperationException("El codigo no puede estar vacio.");
            if (newCode != b.Code)
            {
                await EnsureCodeIsUniqueAsync(newCode, id);
                b.Code = newCode;
            }
        }
        if (r.Name is not null)
        {
            var newName = r.Name.Trim();
            if (newName != b.Name && await _db.Brands.AnyAsync(x => x.Id != id && x.Name == newName))
                throw new InvalidOperationException($"Ya existe una marca con el nombre '{newName}'.");
            b.Name = newName;
        }
        if (r.Description is not null) b.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        if (r.IsActive.HasValue) b.IsActive = r.IsActive.Value;
        b.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return new BrandDto(b.Id, b.Code, b.Name, b.Description, b.IsActive, b.CreatedAt, b.UpdatedAt);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var b = await _db.Brands.FindAsync(id);
        if (b is null) return false;
        _db.Brands.Remove(b);
        await _db.SaveChangesAsync();
        return true;
    }

    // --- Helpers ---

    private async Task<string> GenerateNextCodeAsync()
    {
        var existing = await _db.Brands
            .Where(b => b.Code.StartsWith(CodePrefix))
            .Select(b => b.Code)
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
        var exists = await _db.Brands.AnyAsync(b => b.Code == code && (ignoreId == null || b.Id != ignoreId.Value));
        if (exists)
            throw new InvalidOperationException($"Ya existe una marca con el codigo '{code}'.");
    }
}
