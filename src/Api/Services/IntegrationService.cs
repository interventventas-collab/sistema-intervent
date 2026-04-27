using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class IntegrationService
{
    private readonly AppDbContext _db;

    public IntegrationService(AppDbContext db) => _db = db;

    public async Task<List<IntegrationDto>> GetAllAsync()
    {
        return await _db.Integrations
            .OrderBy(i => i.Provider)
            .Select(i => new IntegrationDto(
                i.Id, i.Provider, i.AppId,
                !string.IsNullOrEmpty(i.AppSecret),
                i.RedirectUrl, i.Settings, i.IsActive,
                i.CreatedAt, i.UpdatedAt))
            .ToListAsync();
    }

    public async Task<IntegrationDto?> GetByProviderAsync(string provider)
    {
        var i = await _db.Integrations
            .FirstOrDefaultAsync(x => x.Provider == provider);

        if (i is null) return null;

        return new IntegrationDto(
            i.Id, i.Provider, i.AppId,
            !string.IsNullOrEmpty(i.AppSecret),
            i.RedirectUrl, i.Settings, i.IsActive,
            i.CreatedAt, i.UpdatedAt);
    }

    public async Task<string?> GetSecretAsync(string provider)
    {
        var integration = await _db.Integrations
            .FirstOrDefaultAsync(x => x.Provider == provider);
        return integration?.AppSecret;
    }

    public async Task<IntegrationDto> SaveAsync(SaveIntegrationRequest request)
    {
        var existing = await _db.Integrations
            .FirstOrDefaultAsync(x => x.Provider == request.Provider);

        if (existing is not null)
        {
            existing.AppId = request.AppId;
            if (!string.IsNullOrEmpty(request.AppSecret))
                existing.AppSecret = request.AppSecret;
            existing.RedirectUrl = request.RedirectUrl;
            existing.Settings = request.Settings;
            existing.IsActive = request.IsActive;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            existing = new Integration
            {
                Provider = request.Provider,
                AppId = request.AppId,
                AppSecret = request.AppSecret,
                RedirectUrl = request.RedirectUrl,
                Settings = request.Settings,
                IsActive = request.IsActive
            };
            _db.Integrations.Add(existing);
        }

        await _db.SaveChangesAsync();

        return new IntegrationDto(
            existing.Id, existing.Provider, existing.AppId,
            !string.IsNullOrEmpty(existing.AppSecret),
            existing.RedirectUrl, existing.Settings, existing.IsActive,
            existing.CreatedAt, existing.UpdatedAt);
    }

    public async Task<bool> DeleteAsync(string provider)
    {
        var integration = await _db.Integrations
            .FirstOrDefaultAsync(x => x.Provider == provider);

        if (integration is null) return false;

        _db.Integrations.Remove(integration);
        await _db.SaveChangesAsync();
        return true;
    }
}
