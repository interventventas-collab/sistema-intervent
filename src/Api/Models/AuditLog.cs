namespace Api.Models;

public class AuditLog
{
    public int Id { get; set; }
    public string EntityType { get; set; } = string.Empty;
    public string EntityId { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string? Changes { get; set; }
    public string? UserName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
