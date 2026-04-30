namespace Web.Models;

/// <summary>
/// Resumen financiero del dashboard:
/// - Ventas del mes en curso
/// - Saldos pendientes de cobro a clientes
/// </summary>
public class SalesSummaryDto
{
    public decimal MonthlySalesTotal { get; set; }
    public int MonthlySalesCount { get; set; }
    public decimal ClientBalanceTotal { get; set; }
    public int ClientBalanceCount { get; set; }
    public int ClientsWithBalance { get; set; }
    public DateTime PeriodStart { get; set; }
    public DateTime PeriodEnd { get; set; }
    public DateTime GeneratedAt { get; set; }
}
