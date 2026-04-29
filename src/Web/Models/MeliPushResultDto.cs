namespace Web.Models;

public class MeliPushResultDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public decimal? PushedPrice { get; set; }
    public int? PushedStock { get; set; }
}
