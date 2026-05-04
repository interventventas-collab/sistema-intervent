namespace Api.DTOs;

// ===== Clientes =====
public record CafeClienteDto(
    int Id, string? Codigo, string Nombre, string Tipo,
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
    int Id, string? Sku, string? Barcode,
    string Nombre, string Categoria, string? Marca,
    decimal Costo, decimal? PrecioPorKg,
    decimal? Pvp1, decimal? Pvp2,
    decimal? BarPctSobreCosto, int? UxB,
    int? OemId, string? OemCodigo,
    decimal StockGramos, int StockUnidades,
    string? Notas, bool IsActive,
    DateTime CreatedAt, DateTime? UpdatedAt);

public class CreateCafeProductoRequest
{
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string Categoria { get; set; } = "CAFE";
    public string? Marca { get; set; }
    public decimal Costo { get; set; }
    public decimal? PrecioPorKg { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? BarPctSobreCosto { get; set; }
    public int? UxB { get; set; }
    public int? OemId { get; set; }
    public decimal? StockGramos { get; set; }
    public int? StockUnidades { get; set; }
    public string? Notas { get; set; }
}

public class UpdateCafeProductoRequest
{
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string? Nombre { get; set; }
    public string? Categoria { get; set; }
    public string? Marca { get; set; }
    public decimal? Costo { get; set; }
    public decimal? PrecioPorKg { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? BarPctSobreCosto { get; set; }
    public int? UxB { get; set; }
    public int? OemId { get; set; }
    public bool ClearBarPctSobreCosto { get; set; }   // marca explicita para vaciar
    public bool ClearUxB { get; set; }
    public bool ClearOemId { get; set; }
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
    string? Molienda, bool EsDoyPack,
    decimal DescuentoPct);

public record CafeVentaDto(
    int Id, string Numero, DateTime Fecha,
    int? ClienteId, string? ClienteNombre, string? ClienteTipo, string? ClienteTelefono,
    decimal Subtotal, decimal Descuento, decimal Total,
    decimal CostoTotal, decimal Margen,
    string? Observaciones, string Estado,
    string? WeekDays, bool IsPaid,
    string TipoComprobante, string CondicionIva, string CondicionPago,
    DateTime CreatedAt,
    List<CafeVentaItemDto> Items);

public class CafeCotizarItemRequest
{
    public int ProductoId { get; set; }
    public string Formato { get; set; } = "1KG";  // 1KG | MEDIO | CUARTO | UNIT
    public int Cantidad { get; set; } = 1;
    public string? Molienda { get; set; }   // EN GRANOS | MOLIDO FILTRO | MOLIDO ESPRESS | null
    public bool EsDoyPack { get; set; }
    public decimal DescuentoPct { get; set; }   // 0-100, descuento porcentual de la linea
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
    string? Molienda, bool EsDoyPack,
    decimal DescuentoPct);

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
    public string? TipoComprobante { get; set; }
    public string? CondicionIva { get; set; }
    public string? CondicionPago { get; set; }
}

public class UpdateCafeVentaFlagsRequest
{
    public string? WeekDays { get; set; }
    public bool? IsPaid { get; set; }
}

/// <summary>Edita una venta. Si se envia Items != null, reemplaza todos los items, recalcula precios
/// y ajusta stock (devuelve el de los viejos, descuenta el de los nuevos). Si se envia Descuento, lo usa
/// para el descuento global de la venta. Solo aplica items si Estado = "emitido".</summary>
public class UpdateCafeVentaRequest
{
    public DateTime? Fecha { get; set; }
    public int? ClienteId { get; set; }
    public string? ClienteNombreOverride { get; set; }
    public string? ClienteTipoOverride { get; set; }
    public string? Observaciones { get; set; }
    public string? TipoComprobante { get; set; }
    public string? CondicionIva { get; set; }
    public string? CondicionPago { get; set; }
    public string? WeekDays { get; set; }
    public bool? IsPaid { get; set; }
    public List<CafeCotizarItemRequest>? Items { get; set; }
    public decimal? Descuento { get; set; }
}

public class DeleteCafeVentaRequest
{
    public string Password { get; set; } = string.Empty;
}

public class BulkDeleteCafeVentasRequest
{
    public List<int> Ids { get; set; } = new();
    public string Password { get; set; } = string.Empty;
}

public record DeleteCafeVentaSettingsDto(string AllowedOperator, string Hint);

// ===== Proveedores =====
public record CafeProveedorDto(
    int Id, string Nombre, string? Contacto, string? Telefono, string? Email,
    string? Notas, bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt,
    int ComprasCount, decimal TotalComprado);

public class CreateCafeProveedorRequest
{
    public string Nombre { get; set; } = string.Empty;
    public string? Contacto { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Notas { get; set; }
}

public class UpdateCafeProveedorRequest
{
    public string? Nombre { get; set; }
    public string? Contacto { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Compras =====
public record CafeCompraItemDto(
    int Id, int ProductoId, string ProductoNombre, string? ProductoSku, string Categoria,
    decimal Cantidad, decimal CostoUnitario, decimal Subtotal,
    decimal StockActualGramos, int StockActualUnidades, decimal CostoActualProducto);

public record CafeCompraDto(
    int Id, string Numero, int? ProveedorId, string? ProveedorNombre,
    DateTime Fecha, string? NumeroComprobante, string Estado, decimal Total,
    string? Observaciones,
    DateTime CreatedAt, DateTime? UpdatedAt,
    DateTime? ConfirmadaAt, DateTime? PagadaAt, DateTime? AnuladaAt,
    List<CafeCompraItemDto> Items);

public class CafeCompraItemRequest
{
    public int ProductoId { get; set; }
    public decimal Cantidad { get; set; }
    public decimal CostoUnitario { get; set; }
}

public class CreateCafeCompraRequest
{
    public int? ProveedorId { get; set; }
    public DateTime? Fecha { get; set; }
    public string? NumeroComprobante { get; set; }
    public string? Observaciones { get; set; }
    public List<CafeCompraItemRequest> Items { get; set; } = new();
}

public class UpdateCafeCompraRequest
{
    public int? ProveedorId { get; set; }
    public bool ClearProveedor { get; set; }
    public DateTime? Fecha { get; set; }
    public string? NumeroComprobante { get; set; }
    public string? Observaciones { get; set; }
    public List<CafeCompraItemRequest>? Items { get; set; }
}

/// <summary>Producto que el cliente compro mas seguido (sugerencia para el form de Nueva Venta).</summary>
public record CafeTopProductoClienteDto(
    int ProductoId, string? Sku, string Nombre, string Categoria, string? Marca,
    string Formato,                         // 1KG / MEDIO / CUARTO / UNIT
    int TimesOrdered,                        // cantidad de comprobantes que lo incluyen
    int TotalQuantity,                       // suma de cantidades
    DateTime LastPurchase,
    decimal StockGramos, int StockUnidades,
    decimal PrecioReferencia);              // precio aplicable al tipo del cliente actual

// ===== Combos =====
public record CafeComboItemDto(
    int Id, int ProductoId, string ProductoNombre, string Categoria, string? Marca,
    string? ProductoSku, decimal? ProductoPvp1, decimal? ProductoPvp2,
    string Formato, int Cantidad,
    string? Molienda, bool EsDoyPack,
    int SortOrder);

public record CafeComboDto(
    int Id, string Nombre, string? Descripcion,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt,
    int ItemsCount,
    decimal PreviewPrecioBar,    // suma de PVP1*cantidad (con costo de fraccionamiento si aplica)
    decimal PreviewPrecioOtro,   // suma de PVP2*cantidad
    List<CafeComboItemDto> Items);

public class CafeComboItemRequest
{
    public int ProductoId { get; set; }
    public string Formato { get; set; } = "1KG";
    public int Cantidad { get; set; } = 1;
    public string? Molienda { get; set; }
    public bool EsDoyPack { get; set; }
    public int SortOrder { get; set; }
}

public class CreateCafeComboRequest
{
    public string Nombre { get; set; } = string.Empty;
    public string? Descripcion { get; set; }
    public List<CafeComboItemRequest> Items { get; set; } = new();
}

public class UpdateCafeComboRequest
{
    public string? Nombre { get; set; }
    public string? Descripcion { get; set; }
    public bool? IsActive { get; set; }
    public List<CafeComboItemRequest>? Items { get; set; }
}

// ===== OEMs (lista del proveedor) =====
public record CafeOemDto(
    int Id, string Codigo, string? Descripcion, string? Marca,
    decimal Costo, decimal? PvpConIva, decimal? IvaPct,
    string? Barcode, string? Proveedor, int? UxB,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt, DateTime? LastImportAt,
    int VariantesCount);

public class CreateCafeOemRequest
{
    public string Codigo { get; set; } = string.Empty;
    public string? Descripcion { get; set; }
    public string? Marca { get; set; }
    public decimal Costo { get; set; }
    public decimal? PvpConIva { get; set; }
    public decimal? IvaPct { get; set; }
    public string? Barcode { get; set; }
    public string? Proveedor { get; set; }
    public int? UxB { get; set; }
}

public class UpdateCafeOemRequest
{
    public string? Codigo { get; set; }
    public string? Descripcion { get; set; }
    public string? Marca { get; set; }
    public decimal? Costo { get; set; }
    public decimal? PvpConIva { get; set; }
    public decimal? IvaPct { get; set; }
    public string? Barcode { get; set; }
    public string? Proveedor { get; set; }
    public int? UxB { get; set; }
    public bool ClearUxB { get; set; }
    public bool? IsActive { get; set; }
}

public record CafeOemImportResultDto(
    int Creados, int Actualizados, int Omitidos,
    string? Proveedor,
    int VariantesPropagadas,
    List<string> Errores);
