namespace Web.Models;

/// <summary>
/// Stock total de cafe disponible (en kg). Suma el Stock de todos los productos
/// kg-mode (StockUnit='kg'), que son los padres de cada variedad de cafe.
/// </summary>
public class CoffeeStockKgDto
{
    public decimal KgTotal { get; set; }
    public int Variedades { get; set; }
    public int VariedadesConStock { get; set; }
    public DateTime GeneratedAt { get; set; }
}
