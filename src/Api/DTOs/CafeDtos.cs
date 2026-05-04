namespace Api.DTOs;

// ===== Clientes =====
public record CafeClienteDto(
    int Id, string Nombre, string Tipo,
    string? Cuit, string? Telefono, string? Email,
    string? Direccion, string? Notas,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt);

public class CreateCafeClienteRequest
{
    public string Nombre { get; set; } = string.Empty;
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

// ===== Productos =====
public record CafeProductoDto(
    int Id, string Nombre, string Categoria,
    decimal Costo, decimal? PrecioPorKg,
    decimal? Pvp1, decimal? Pvp2,
    decimal StockGramos, int StockUnidades,
    string? Notas, bool IsActive,
    DateTime CreatedAt, DateTime? UpdatedAt);

public class CreateCafeProductoRequest
{
    public string Nombre { get; set; } = string.Empty;
    public string Categoria { get; set; } = "CAFE";
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
    public decimal? Costo { get; set; }
    public decimal? PrecioPorKg { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? StockGramos { get; set; }
    public int? StockUnidades { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Settings =====
public record CafeSettingDto(
    decimal CostoFraccionamiento, decimal RedondeoMultiplo,
    decimal MargenOtrosBarPct, decimal MargenOtrosNoBarPct,
    string? NegocioNombre, string? NegocioTelefono, string? NegocioWhatsappNumero,
    string? NegocioDireccion, string? NegocioCuit,
    string? WhatsappMensajeTemplate, string? WhatsappMensajeClienteTemplate,
    DateTime? UpdatedAt);

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
public record CafeVentaItemDto(
    int Id, int ProductoId, string ProductoNombre, string Categoria,
    string Formato, int Cantidad,
    decimal PrecioUnitario, decimal CostoUnitario, decimal Subtotal,
    decimal GramosDescontados,
    string? Molienda, bool EsDoyPack);

public record CafeVentaDto(
    int Id, string Numero, DateTime Fecha,
    int? ClienteId, string? ClienteNombre, string? ClienteTipo, string? ClienteTelefono,
    decimal Subtotal, decimal Descuento, decimal Total,
    decimal CostoTotal, decimal Margen,
    string? Observaciones, string Estado,
    string? WeekDays, bool IsPaid,
    DateTime CreatedAt,
    List<CafeVentaItemDto> Items);

public class CafeCotizarItemRequest
{
    public int ProductoId { get; set; }
    public string Formato { get; set; } = "1KG";  // 1KG | MEDIO | CUARTO | UNIT
    public int Cantidad { get; set; } = 1;
    public string? Molienda { get; set; }   // EN GRANOS | MOLIDO FILTRO | MOLIDO ESPRESS | null
    public bool EsDoyPack { get; set; }
}

public class CafeCotizarRequest
{
    public int? ClienteId { get; set; }
    public string? ClienteTipo { get; set; }  // override si no hay clienteId
    public List<CafeCotizarItemRequest> Items { get; set; } = new();
    public decimal Descuento { get; set; }
}

public record CafeCotizadoItemDto(
    int ProductoId, string ProductoNombre, string Categoria,
    string Formato, int Cantidad,
    decimal PrecioUnitario, decimal CostoUnitario, decimal Subtotal,
    decimal GramosNecesarios, decimal StockGramosDisponible, int StockUnidadesDisponible,
    bool StockOk, string? Aviso,
    string? Molienda, bool EsDoyPack);

public record CafeCotizadoDto(
    string ClienteTipoUsado,  // BAR | OTRO
    decimal Subtotal, decimal Descuento, decimal Total,
    decimal CostoTotal, decimal Margen,
    bool TodoOk,
    List<CafeCotizadoItemDto> Items);

public class CreateCafeVentaRequest
{
    public DateTime? Fecha { get; set; }
    public int? ClienteId { get; set; }
    public string? ClienteNombreOverride { get; set; }   // si no hay cliente cargado
    public string? ClienteTipoOverride { get; set; }     // BAR | OTRO si no hay cliente cargado
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
