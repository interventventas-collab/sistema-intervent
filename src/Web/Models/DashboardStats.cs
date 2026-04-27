namespace Web.Models;

public class DashboardStats
{
    public int TotalItems { get; set; }
    public int TotalProducts { get; set; }
    public int ItemsSinProducto { get; set; }
    public int ProductosSinItems { get; set; }
    public List<AccountStatsRow> AccountStats { get; set; } = new();
}

public class AccountStatsRow
{
    public int AccountId { get; set; }
    public string Nickname { get; set; } = "";
    public int TotalItems { get; set; }
    public int ItemsConProducto { get; set; }
    public int ItemsSinProducto { get; set; }
    public int ProductosVinculados { get; set; }
}
