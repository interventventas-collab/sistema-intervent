namespace Web.Models;

public class MeliAccountStatsDto
{
    public int AccountId { get; set; }
    public string Nickname { get; set; } = "";
    public int OrderCount { get; set; }
    public int ItemCount { get; set; }
}
