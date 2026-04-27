using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class AuditLogService
{
    private readonly AppDbContext _db;

    public AuditLogService(AppDbContext db)
    {
        _db = db;
    }

    public async Task LogAsync(string entityType, string entityId, string action, string? changes = null, string? userName = null)
    {
        _db.AuditLogs.Add(new AuditLog
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            Changes = changes,
            UserName = userName,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    public async Task<AuditLogListResponse> GetLogsAsync(DateTime from, DateTime to, string? entityType = null, int page = 1, int pageSize = 50)
    {
        var query = _db.AuditLogs.AsQueryable();

        query = query.Where(a => a.CreatedAt >= from && a.CreatedAt <= to);

        if (!string.IsNullOrEmpty(entityType))
            query = query.Where(a => a.EntityType == entityType);

        var total = await query.CountAsync();

        var logs = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AuditLogDto(
                a.Id, a.EntityType, a.EntityId, a.Action,
                a.Changes, a.UserName, a.CreatedAt))
            .ToListAsync();

        return new AuditLogListResponse(logs, total, page, pageSize);
    }
}
