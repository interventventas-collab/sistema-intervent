namespace Web.Models;

public class AuditLogDto
{
    public int Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? Changes { get; set; }
    public string? UserName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class AuditLogListResponse
{
    public List<AuditLogDto> Logs { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
