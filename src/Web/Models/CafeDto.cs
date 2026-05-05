namespace Web.Models;

public class CafeClienteDto
{
    public int Id { get; set; }
    public string? Codigo { get; set; }
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
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string Nombre { get; set; } = "";
    public string Categoria { get; set; } = "CAFE";
    public string? Marca { get; set; }
    public int? MarcaId { get; set; }
    public string? MarcaNombre { get; set; }
    public decimal Costo { get; set; }
    public decimal? PrecioPorKg { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? BarPctSobreCosto { get; set; }
    public int? UxB { get; set; }
    public int? OemId { get; set; }
    public string? OemCodigo { get; set; }
    public decimal StockGramos { get; set; }
    public int StockUnidades { get; set; }
    public string? Notas { get; set; }
    public bool IsActive { get; set; }
    public decimal IvaPct { get; set; } = 21m;
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Calculados — Pvp1/Pvp2 se guardan SIN IVA. Multiplicar por (1 + IvaPct/100) da el con IVA.
    public decimal? Pvp1ConIva => Pvp1.HasValue ? Math.Round(Pvp1.Value * (1 + IvaPct / 100m), 2) : null;
    public decimal? Pvp2ConIva => Pvp2.HasValue ? Math.Round(Pvp2.Value * (1 + IvaPct / 100m), 2) : null;
}

public class CreateCafeProductoRequest
{
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string Nombre { get; set; } = "";
    public string Categoria { get; set; } = "CAFE";
    public string? Marca { get; set; }
    public int? MarcaId { get; set; }
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
    public decimal? IvaPct { get; set; }
}

public class UpdateCafeProductoRequest
{
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string? Nombre { get; set; }
    public string? Categoria { get; set; }
    public string? Marca { get; set; }
    public int? MarcaId { get; set; }
    public bool ClearMarcaId { get; set; }
    public decimal? Costo { get; set; }
    public decimal? PrecioPorKg { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? BarPctSobreCosto { get; set; }
    public int? UxB { get; set; }
    public int? OemId { get; set; }
    public bool ClearBarPctSobreCosto { get; set; }
    public bool ClearUxB { get; set; }
    public bool ClearOemId { get; set; }
    public decimal? StockGramos { get; set; }
    public int? StockUnidades { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
    public decimal? IvaPct { get; set; }
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
    public string? NegocioEmail { get; set; }
    public string? NegocioWeb { get; set; }
    public string? NegocioLogoUrl { get; set; }
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
    public string? NegocioEmail { get; set; }
    public string? NegocioWeb { get; set; }
    public string? NegocioLogoUrl { get; set; }
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
    public decimal DescuentoPct { get; set; }
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
    public string TipoComprobante { get; set; } = "X";
    public string CondicionIva { get; set; } = "CF";
    public string CondicionPago { get; set; } = "EFECTIVO";
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
    public decimal DescuentoPct { get; set; }
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
    public decimal DescuentoPct { get; set; }
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
    public string? TipoComprobante { get; set; }
    public string? CondicionIva { get; set; }
    public string? CondicionPago { get; set; }
}

public class UpdateCafeVentaFlagsRequest
{
    public string? WeekDays { get; set; }
    public bool? IsPaid { get; set; }
}

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

public class DeleteCafeVentaSettingsDto
{
    public string AllowedOperator { get; set; } = "OSMAR";
    public string Hint { get; set; } = string.Empty;
}

// ===== Proveedores =====
public class CafeProveedorDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Contacto { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Notas { get; set; }
    public string? Cuit { get; set; }
    public string? CategoriaImpositiva { get; set; }
    public string? Direccion { get; set; }
    public string? CodigoPostal { get; set; }
    public string? Provincia { get; set; }
    public string? Ciudad { get; set; }
    public string? Web { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int ComprasCount { get; set; }
    public decimal TotalComprado { get; set; }
}

public class CreateCafeProveedorRequest
{
    public string Nombre { get; set; } = "";
    public string? Contacto { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Notas { get; set; }
    public string? Cuit { get; set; }
    public string? CategoriaImpositiva { get; set; }
    public string? Direccion { get; set; }
    public string? CodigoPostal { get; set; }
    public string? Provincia { get; set; }
    public string? Ciudad { get; set; }
    public string? Web { get; set; }
}

public class UpdateCafeProveedorRequest
{
    public string? Nombre { get; set; }
    public string? Contacto { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Notas { get; set; }
    public string? Cuit { get; set; }
    public string? CategoriaImpositiva { get; set; }
    public string? Direccion { get; set; }
    public string? CodigoPostal { get; set; }
    public string? Provincia { get; set; }
    public string? Ciudad { get; set; }
    public string? Web { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Compras =====
public class CafeCompraItemDto
{
    public int Id { get; set; }
    public int ProductoId { get; set; }
    public string ProductoNombre { get; set; } = "";
    public string? ProductoSku { get; set; }
    public string Categoria { get; set; } = "OTROS";
    public decimal Cantidad { get; set; }
    public decimal CostoUnitario { get; set; }
    public decimal Subtotal { get; set; }
    public decimal StockActualGramos { get; set; }
    public int StockActualUnidades { get; set; }
    public decimal CostoActualProducto { get; set; }
}

public class CafeCompraDto
{
    public int Id { get; set; }
    public string Numero { get; set; } = "";
    public int? ProveedorId { get; set; }
    public string? ProveedorNombre { get; set; }
    public DateTime Fecha { get; set; }
    public string? NumeroComprobante { get; set; }
    public string Estado { get; set; } = "BORRADOR";
    public decimal Total { get; set; }
    public string? Observaciones { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? ConfirmadaAt { get; set; }
    public DateTime? PagadaAt { get; set; }
    public DateTime? AnuladaAt { get; set; }
    public List<CafeCompraItemDto> Items { get; set; } = new();
}

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

public class CafeTopProductoClienteDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public string Categoria { get; set; } = "CAFE";
    public string? Marca { get; set; }
    public string Formato { get; set; } = "1KG";
    public int TimesOrdered { get; set; }
    public int TotalQuantity { get; set; }
    public DateTime LastPurchase { get; set; }
    public decimal StockGramos { get; set; }
    public int StockUnidades { get; set; }
    public decimal PrecioReferencia { get; set; }
}

// ===== Combos =====
public class CafeComboItemDto
{
    public int Id { get; set; }
    public int ProductoId { get; set; }
    public string ProductoNombre { get; set; } = "";
    public string Categoria { get; set; } = "CAFE";
    public string? Marca { get; set; }
    public string? ProductoSku { get; set; }
    public decimal? ProductoPvp1 { get; set; }
    public decimal? ProductoPvp2 { get; set; }
    public string Formato { get; set; } = "1KG";
    public int Cantidad { get; set; } = 1;
    public string? Molienda { get; set; }
    public bool EsDoyPack { get; set; }
    public int SortOrder { get; set; }
}

public class CafeComboDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Descripcion { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int ItemsCount { get; set; }
    public decimal PreviewPrecioBar { get; set; }
    public decimal PreviewPrecioOtro { get; set; }
    public List<CafeComboItemDto> Items { get; set; } = new();
}

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
    public string Nombre { get; set; } = "";
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

// ===== OEMs =====
public class CafeOemDto
{
    public int Id { get; set; }
    public string Codigo { get; set; } = "";
    public string? Descripcion { get; set; }
    public string? Marca { get; set; }
    public int? MarcaId { get; set; }
    public string? MarcaNombre { get; set; }
    public decimal Costo { get; set; }
    public decimal? PvpConIva { get; set; }
    public decimal? IvaPct { get; set; }
    public string? Barcode { get; set; }
    public string? Proveedor { get; set; }
    public int? UxB { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastImportAt { get; set; }
    public int VariantesCount { get; set; }
}

public class CreateCafeOemRequest
{
    public string Codigo { get; set; } = "";
    public string? Descripcion { get; set; }
    public string? Marca { get; set; }
    public int? MarcaId { get; set; }
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
    public int? MarcaId { get; set; }
    public bool ClearMarcaId { get; set; }
    public decimal? Costo { get; set; }
    public decimal? PvpConIva { get; set; }
    public decimal? IvaPct { get; set; }
    public string? Barcode { get; set; }
    public string? Proveedor { get; set; }
    public int? UxB { get; set; }
    public bool ClearUxB { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Consultas =====
public class CafeConsultaRequest
{
    public string Query { get; set; } = "";
}

public class CafeConsultaResultDto
{
    public string Tipo { get; set; } = "vacio";
    public string Titulo { get; set; } = "";
    public string? Subtitulo { get; set; }
    public string? Total { get; set; }
    public List<string> Columnas { get; set; } = new();
    public List<Dictionary<string, string>> Filas { get; set; } = new();
    public List<KeyValuePair<string, string>> Datos { get; set; } = new();
    public List<string> Ayuda { get; set; } = new();
}

// ===== Listas de precios =====
public class CafeListaPreciosFiltroRequest
{
    public int? ClienteId { get; set; }
    public string? Tipo { get; set; }
    public List<int>? MarcaIds { get; set; }
    public string? Categoria { get; set; }
    public string? Observaciones { get; set; }
}

public class CafeListaPreciosNegocioDto
{
    public string? Nombre { get; set; }
    public string? Telefono { get; set; }
    public string? WhatsappNumero { get; set; }
    public string? Direccion { get; set; }
    public string? Cuit { get; set; }
    public string? Email { get; set; }
    public string? Web { get; set; }
    public string? LogoUrl { get; set; }
}

public class CafeListaPreciosClienteDto
{
    public int? Id { get; set; }
    public string? Codigo { get; set; }
    public string? Nombre { get; set; }
    public string Tipo { get; set; } = "OTRO";
    public string? Telefono { get; set; }
    public string? Email { get; set; }
}

public class CafeListaPreciosItemCafeDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public decimal Precio1Kg { get; set; }
    public decimal PrecioMedio { get; set; }
    public decimal PrecioCuarto { get; set; }
}

public class CafeListaPreciosItemOtroDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public decimal Precio { get; set; }
}

public class CafeListaPreciosMarcaGroupDto
{
    public int? MarcaId { get; set; }
    public string MarcaNombre { get; set; } = "";
    public string? ProveedorNombre { get; set; }
    public List<CafeListaPreciosItemCafeDto> ItemsCafe { get; set; } = new();
    public List<CafeListaPreciosItemOtroDto> ItemsOtros { get; set; } = new();
}

public class CafeListaPreciosPreviewDto
{
    public DateTime Fecha { get; set; }
    public DateTime ValidezHasta { get; set; }
    public string TipoCliente { get; set; } = "OTRO";
    public CafeListaPreciosNegocioDto Negocio { get; set; } = new();
    public CafeListaPreciosClienteDto? Cliente { get; set; }
    public List<CafeListaPreciosMarcaGroupDto> Grupos { get; set; } = new();
    public string? Observaciones { get; set; }
}

// ===== Marcas =====
public class CafeMarcaDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public int? ProveedorId { get; set; }
    public string? ProveedorNombre { get; set; }
    public string? Notas { get; set; }
    public bool IsActive { get; set; }
    public bool BloqueaDescuento { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int ProductosCount { get; set; }
    public int OemsCount { get; set; }
}

public class CreateCafeMarcaRequest
{
    public string Nombre { get; set; } = "";
    public int? ProveedorId { get; set; }
    public string? Notas { get; set; }
}

public class UpdateCafeMarcaRequest
{
    public string? Nombre { get; set; }
    public int? ProveedorId { get; set; }
    public bool ClearProveedor { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
    public bool? BloqueaDescuento { get; set; }
}

// === Kits (productos compuestos / BOM) ===
public class CafeKitDto
{
    public int Id { get; set; }
    public string Sku { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? Descripcion { get; set; }
    public string Categoria { get; set; } = "OTROS";
    public string? Marca { get; set; }
    public int? MarcaId { get; set; }
    public string? MarcaNombre { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal IvaPct { get; set; } = 21m;
    public string? Notas { get; set; }
    public bool IsActive { get; set; } = true;
    public int StockVirtual { get; set; }
    public decimal CostoCalculado { get; set; }
    public List<CafeKitItemDto> Items { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    public decimal? Pvp2ConIva => Pvp2.HasValue ? Math.Round(Pvp2.Value * (1 + IvaPct / 100m), 2) : null;
}

public class CafeKitItemDto
{
    public int Id { get; set; }
    public int ProductoId { get; set; }
    public string? ProductoSku { get; set; }
    public string ProductoNombre { get; set; } = "";
    public int ProductoStock { get; set; }
    public decimal Cantidad { get; set; }
    public int KitsPosibles { get; set; }
}

public class CafeKitItemRequest
{
    public int? Id { get; set; }
    public int ProductoId { get; set; }
    public decimal Cantidad { get; set; } = 1m;
}

public class CreateCafeKitRequest
{
    public string Sku { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? Descripcion { get; set; }
    public string Categoria { get; set; } = "OTROS";
    public string? Marca { get; set; }
    public int? MarcaId { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? IvaPct { get; set; }
    public string? Notas { get; set; }
    public List<CafeKitItemRequest> Items { get; set; } = new();
}

public class UpdateCafeKitRequest
{
    public string? Sku { get; set; }
    public string? Nombre { get; set; }
    public string? Descripcion { get; set; }
    public string? Categoria { get; set; }
    public string? Marca { get; set; }
    public int? MarcaId { get; set; }
    public bool ClearMarcaId { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? IvaPct { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
    public List<CafeKitItemRequest>? Items { get; set; }
}

public class CafeHistorialPrecioDto
{
    public int Id { get; set; }
    public decimal? Pvp1Anterior { get; set; }
    public decimal? Pvp2Anterior { get; set; }
    public decimal? CostoAnterior { get; set; }
    public decimal? IvaPctAnterior { get; set; }
    public decimal? Pvp1Nuevo { get; set; }
    public decimal? Pvp2Nuevo { get; set; }
    public decimal? CostoNuevo { get; set; }
    public decimal? IvaPctNuevo { get; set; }
    public DateTime ChangedAt { get; set; }
    public string? ChangedBy { get; set; }
    public string? Motivo { get; set; }
}

public class CafeOemImportResultDto
{
    public int Creados { get; set; }
    public int Actualizados { get; set; }
    public int Omitidos { get; set; }
    public string? Proveedor { get; set; }
    public int VariantesPropagadas { get; set; }
    public List<string> Errores { get; set; } = new();
}

// === Descuentos por tipo de cliente y marca ===
public class CafeDescuentoGrillaFila
{
    public int? MarcaId { get; set; }
    public string MarcaNombre { get; set; } = "";
    public bool BloqueaDescuento { get; set; }
    public Dictionary<string, decimal?> DescuentoPorTipo { get; set; } = new();
}

public class CafeDescuentoGrillaResponse
{
    public List<string> Tipos { get; set; } = new();
    public List<CafeDescuentoGrillaFila> Filas { get; set; } = new();
}

public class UpsertDescuentoRequest
{
    public string TipoCliente { get; set; } = "OTRO";
    public int? MarcaId { get; set; }
    public decimal DescuentoPct { get; set; }
}
