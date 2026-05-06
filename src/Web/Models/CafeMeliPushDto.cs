namespace Web.Models;

public class CafeMeliPreviewDto
{
    public CafeMeliPreviewCafe Cafe { get; set; } = new();
    public List<CafeMeliPreviewRow> Publicaciones { get; set; } = new();
}

public class CafeMeliPreviewCafe
{
    public int Id { get; set; }
    public string? Sku { get; set; }
    public string? Nombre { get; set; }
    public decimal StockGramos { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal IvaPct { get; set; }
}

public class CafeMeliPreviewRow
{
    public string MeliItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Cuenta { get; set; }
    public string Formato { get; set; } = "1KG";
    public string? LogisticType { get; set; }
    public bool EsFull { get; set; }
    public int StockMeli { get; set; }
    public int StockNuevo { get; set; }
    public int StockDelta { get; set; }
    public decimal PrecioMeli { get; set; }
    public decimal PrecioNuevo { get; set; }
    public decimal PrecioDelta { get; set; }
    public bool Cambia { get; set; }
}

public class CafeMeliPushResultDto
{
    public int Total { get; set; }
    public int Ok { get; set; }
    public List<CafeMeliPushResultRow> Results { get; set; } = new();
}

public class CafeMeliPushResultRow
{
    public string MeliItemId { get; set; } = "";
    public bool Success { get; set; }
    public string? Message { get; set; }
    public decimal? PushedPrice { get; set; }
    public int? PushedStock { get; set; }
}
