namespace Api.DTOs;

public record AuditLogDto(
    int Id,
    string EntityType,
    string EntityId,
    string Action,
    string? Changes,
    string? UserName,
    DateTime CreatedAt
);

public record AuditLogListResponse(
    List<AuditLogDto> Logs,
    int Total,
    int Page,
    int PageSize
);
