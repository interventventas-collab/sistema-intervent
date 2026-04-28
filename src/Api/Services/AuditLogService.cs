using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class AuditLogService
{
    private readonly AppDbContext _db;
    private readonly IHttpContextAccessor _http;

    public AuditLogService(AppDbContext db, IHttpContextAccessor http)
    {
        _db = db;
        _http = http;
    }

    public async Task LogAsync(string entityType, string entityId, string action, string? changes = null, string? userName = null)
    {
        // Si no nos pasaron userName explicito, lo tomamos del header X-Operator-Name
        // (operador seleccionado en la UI). Si tampoco esta, queda null.
        if (string.IsNullOrWhiteSpace(userName))
        {
            var op = _http.HttpContext?.Request.Headers["X-Operator-Name"].ToString();
            if (!string.IsNullOrWhiteSpace(op)) userName = op;
        }

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
