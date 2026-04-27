namespace Api.Models;

public class Integration
{
    public int Id { get; set; }
    public string Provider { get; set; } = string.Empty;
    public string? AppId { get; set; }
    public string? AppSecret { get; set; }
    public string? RedirectUrl { get; set; }
    public string? Settings { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
