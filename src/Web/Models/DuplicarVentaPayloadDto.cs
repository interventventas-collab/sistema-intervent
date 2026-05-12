namespace Web.Models;

/// <summary>Payload pre-armado del endpoint Duplicar — datos para abrir el modal
/// de Nueva venta con todo cargado del comprobante original.</summary>
public class DuplicarVentaPayloadDto
{
    public int? ClienteId { get; set; }
    public string? ClienteNombre { get; set; }
    public string ClienteTipo { get; set; } = "OTRO";
    public string TipoComprobante { get; set; } = "X";
    public string CondicionIva { get; set; } = "CF";
    public string CondicionPago { get; set; } = "EFECTIVO";
    public string? WeekDays { get; set; }
    public string? Observaciones { get; set; }
    public List<CafeCotizarItemRequest> Items { get; set; } = new();
    public string? OrigenNumero { get; set; }
}
