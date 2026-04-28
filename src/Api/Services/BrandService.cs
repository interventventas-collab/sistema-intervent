using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class BrandService
{
    private readonly AppDbContext _db;

    public BrandService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<BrandDto>> GetAllAsync()
    {
        return await _db.Brands
            .OrderBy(b => b.Name)
            .Select(b => new BrandDto(b.Id, b.Name, b.Description, b.IsActive, b.CreatedAt, b.UpdatedAt))
            .ToListAsync();
    }

    public async Task<BrandDto?> GetByIdAsync(int id)
    {
        return await _db.Brands
            .Where(b => b.Id == id)
            .Select(b => new BrandDto(b.Id, b.Name, b.Description, b.IsActive, b.CreatedAt, b.UpdatedAt))
            .FirstOrDefaultAsync();
    }

    public async Task<BrandDto> CreateAsync(CreateBrandRequest r)
    {
        var name = r.Name.Trim();
        if (await _db.Brands.AnyAsync(b => b.Name == name))
            throw new InvalidOperationException($"Ya existe una marca con el nombre '{name}'.");

        var b = new Brand
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Brands.Add(b);
        await _db.SaveChangesAsync();
        return new BrandDto(b.Id, b.Name, b.Description, b.IsActive, b.CreatedAt, b.UpdatedAt);
    }

    public async Task<BrandDto?> UpdateAsync(int id, UpdateBrandRequest r)
    {
        var b = await _db.Brands.FindAsync(id);
        if (b is null) return null;

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
        return new BrandDto(b.Id, b.Name, b.Description, b.IsActive, b.CreatedAt, b.UpdatedAt);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var b = await _db.Brands.FindAsync(id);
        if (b is null) return false;
        _db.Brands.Remove(b);
        await _db.SaveChangesAsync();
        return true;
    }
}
