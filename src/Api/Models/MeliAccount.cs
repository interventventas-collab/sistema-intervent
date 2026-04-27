namespace Api.Models;

public class MeliAccount
{
    public int Id { get; set; }
    public long MeliUserId { get; set; }
    public string Nickname { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string? RefreshToken { get; set; }
    public DateTime TokenExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
