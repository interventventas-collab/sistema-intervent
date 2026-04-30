namespace Web.Models;

/// <summary>
/// Sumatoria de kg de cafe FRIKAF vendidos en el mes actual.
/// Se calcula como SUM(SaleItem.Quantity * Product.Fraction) sobre items
/// de productos cuya marca contenga 'FRIKAF', en ventas no anuladas, dentro del mes.
/// </summary>
public class CoffeeMonthlyKgDto
{
    public decimal KgTotal { get; set; }
    public int Items { get; set; }
    public int Sales { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime GeneratedAt { get; set; }
}
