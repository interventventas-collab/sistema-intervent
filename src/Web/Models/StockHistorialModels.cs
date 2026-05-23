namespace Web.Models;

// Items del historial de movimientos de stock (/api/stock/admin/movimientos)
public class StockHistorialItem
{
    public int Id { get; set; }
    public DateTime Fecha { get; set; }
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string ProductoNombre { get; set; } = "";
    public string Operador { get; set; } = "";
    public string TipoMov { get; set; } = "";
    public int Cantidad { get; set; }
    public int StockAntes { get; set; }
    public int StockDespues { get; set; }
    public string? Comentario { get; set; }
}

public class StockHistorialStatsItem
{
    public string TipoMov { get; set; } = "";
    public int Count { get; set; }
}

public class StockHistorialStats
{
    public int Total { get; set; }
    public List<StockHistorialStatsItem> PorTipo { get; set; } = new();
    public DateTime? Ultimo { get; set; }
}
