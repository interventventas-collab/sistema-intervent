namespace Web.Models;

public class CafeClienteDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "OTRO";
    public string? Cuit { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Direccion { get; set; }
    public string? Notas { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateCafeClienteRequest
{
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "OTRO";
    public string? Cuit { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Direccion { get; set; }
    public string? Notas { get; set; }
}

public class UpdateCafeClienteRequest
{
    public string? Nombre { get; set; }
    public string? Tipo { get; set; }
    public string? Cuit { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Direccion { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
}

public class CafeProductoDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string Categoria { get; set; } = "CAFE";
    public string? Marca { get; set; }
    public decimal Costo { get; set; }
    public decimal? PrecioPorKg { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal StockGramos { get; set; }
    public int StockUnidades { get; set; }
    public string? Notas { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateCafeProductoRequest
{
    public string Nombre { get; set; } = "";
    public string Categoria { get; set; } = "CAFE";
    public string? Marca { get; set; }
    public decimal Costo { get; set; }
    public decimal? PrecioPorKg { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? StockGramos { get; set; }
    public int? StockUnidades { get; set; }
    public string? Notas { get; set; }
}

public class UpdateCafeProductoRequest
{
    public string? Nombre { get; set; }
    public string? Categoria { get; set; }
    public string? Marca { get; set; }
    public decimal? Costo { get; set; }
    public decimal? PrecioPorKg { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? StockGramos { get; set; }
    public int? StockUnidades { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
}

public class CafeSettingDto
{
    public decimal CostoFraccionamiento { get; set; }
    public decimal RedondeoMultiplo { get; set; }
    public decimal MargenOtrosBarPct { get; set; }
    public decimal MargenOtrosNoBarPct { get; set; }
    public string? NegocioNombre { get; set; }
    public string? NegocioTelefono { get; set; }
    public string? NegocioWhatsappNumero { get; set; }
    public string? NegocioDireccion { get; set; }
    public string? NegocioCuit { get; set; }
    public string? WhatsappMensajeTemplate { get; set; }
    public string? WhatsappMensajeClienteTemplate { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class UpdateCafeSettingRequest
{
    public decimal? CostoFraccionamiento { get; set; }
    public decimal? RedondeoMultiplo { get; set; }
    public decimal? MargenOtrosBarPct { get; set; }
    public decimal? MargenOtrosNoBarPct { get; set; }
    public string? NegocioNombre { get; set; }
    public string? NegocioTelefono { get; set; }
    public string? NegocioWhatsappNumero { get; set; }
    public string? NegocioDireccion { get; set; }
    public string? NegocioCuit { get; set; }
    public string? WhatsappMensajeTemplate { get; set; }
    public string? WhatsappMensajeClienteTemplate { get; set; }
}

// ===== Ventas =====
public class CafeVentaItemDto
{
    public int Id { get; set; }
    public int ProductoId { get; set; }
    public string ProductoNombre { get; set; } = "";
    public string Categoria { get; set; } = "CAFE";
    public string Formato { get; set; } = "1KG";
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
    public decimal CostoUnitario { get; set; }
    public decimal Subtotal { get; set; }
    public decimal GramosDescontados { get; set; }
    public string? Molienda { get; set; }
    public bool EsDoyPack { get; set; }
}

public class CafeVentaDto
{
    public int Id { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public int? ClienteId { get; set; }
    public string? ClienteNombre { get; set; }
    public string? ClienteTipo { get; set; }
    public string? ClienteTelefono { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Descuento { get; set; }
    public decimal Total { get; set; }
    public decimal CostoTotal { get; set; }
    public decimal Margen { get; set; }
    public string? Observaciones { get; set; }
    public string Estado { get; set; } = "emitido";
    public string? WeekDays { get; set; }
    public bool IsPaid { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<CafeVentaItemDto> Items { get; set; } = new();
}

public class CafeCotizarItemRequest
{
    public int ProductoId { get; set; }
    public string Formato { get; set; } = "1KG";
    public int Cantidad { get; set; } = 1;
    public string? Molienda { get; set; }
    public bool EsDoyPack { get; set; }
}

public class CafeCotizarRequest
{
    public int? ClienteId { get; set; }
    public string? ClienteTipo { get; set; }
    public List<CafeCotizarItemRequest> Items { get; set; } = new();
    public decimal Descuento { get; set; }
}

public class CafeCotizadoItemDto
{
    public int ProductoId { get; set; }
    public string ProductoNombre { get; set; } = "";
    public string Categoria { get; set; } = "CAFE";
    public string Formato { get; set; } = "1KG";
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
    public decimal CostoUnitario { get; set; }
    public decimal Subtotal { get; set; }
    public decimal GramosNecesarios { get; set; }
    public decimal StockGramosDisponible { get; set; }
    public int StockUnidadesDisponible { get; set; }
    public bool StockOk { get; set; }
    public string? Aviso { get; set; }
    public string? Molienda { get; set; }
    public bool EsDoyPack { get; set; }
}

public class CafeCotizadoDto
{
    public string ClienteTipoUsado { get; set; } = "OTRO";
    public decimal Subtotal { get; set; }
    public decimal Descuento { get; set; }
    public decimal Total { get; set; }
    public decimal CostoTotal { get; set; }
    public decimal Margen { get; set; }
    public bool TodoOk { get; set; }
    public List<CafeCotizadoItemDto> Items { get; set; } = new();
}

public class CreateCafeVentaRequest
{
    public DateTime? Fecha { get; set; }
    public int? ClienteId { get; set; }
    public string? ClienteNombreOverride { get; set; }
    public string? ClienteTipoOverride { get; set; }
    public List<CafeCotizarItemRequest> Items { get; set; } = new();
    public decimal Descuento { get; set; }
    public string? Observaciones { get; set; }
    public string? WeekDays { get; set; }
    public bool IsPaid { get; set; }
}

public class UpdateCafeVentaFlagsRequest
{
    public string? WeekDays { get; set; }
    public bool? IsPaid { get; set; }
}
