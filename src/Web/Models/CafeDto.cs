namespace Web.Models;

public class CafeClienteDto
{
    public int Id { get; set; }
    public string? Codigo { get; set; }
    public string Nombre { get; set; } = "";
    public string? RazonSocial { get; set; }
    public string Tipo { get; set; } = "OTRO";
    public string? Cuit { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Direccion { get; set; }
    public string? Localidad { get; set; }
    public string? Ciudad { get; set; }
    public string? Cp { get; set; }
    public string? CondicionIvaDefault { get; set; }
    public string? DomicilioEntrega { get; set; }
    public string? Notas { get; set; }
    public string? ComentariosComprobante { get; set; }
    public bool IsActive { get; set; }
    /// <summary>Código interno correlativo (numérico) asignado por el operador con el botón.</summary>
    public int? CodigoInterno { get; set; }
    /// <summary>Enlace corto de Google Maps a la ubicación del cliente.</summary>
    public string? MapeoLink { get; set; }
    /// <summary>Latitud extraída del MapeoLink (cuando se resuelve el redirect de Google Maps).</summary>
    public decimal? MapeoLat { get; set; }
    /// <summary>Longitud extraída del MapeoLink.</summary>
    public decimal? MapeoLng { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateCafeClienteRequest
{
    public string Nombre { get; set; } = "";
    public string? RazonSocial { get; set; }
    public string Tipo { get; set; } = "OTRO";
    public string? Cuit { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Direccion { get; set; }
    public string? Localidad { get; set; }
    public string? Ciudad { get; set; }
    public string? Cp { get; set; }
    public string? CondicionIvaDefault { get; set; }
    public string? DomicilioEntrega { get; set; }
    public string? Notas { get; set; }
    public string? ComentariosComprobante { get; set; }
    public string? MapeoLink { get; set; }
    /// <summary>Código interno pre-asignado en el frontend (con el botón "Asignar código"
    /// antes de guardar). El backend lo respeta si está libre; si está tomado asigna el siguiente.</summary>
    public int? CodigoInterno { get; set; }
}

public class UpdateCafeClienteRequest
{
    public string? Nombre { get; set; }
    public string? RazonSocial { get; set; }
    public string? Tipo { get; set; }
    public string? Cuit { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? Direccion { get; set; }
    public string? Localidad { get; set; }
    public string? Ciudad { get; set; }
    public string? Cp { get; set; }
    public string? CondicionIvaDefault { get; set; }
    public string? DomicilioEntrega { get; set; }
    public string? Notas { get; set; }
    public string? ComentariosComprobante { get; set; }
    public bool? IsActive { get; set; }
    public string? MapeoLink { get; set; }
    public bool ClearMapeoLink { get; set; }
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
    /// <summary>Override por producto: reserva interna que se descuenta del stock al pushear a MeLi.
    /// null = usa el global. 0 = sin reserva. N = reservar N unidades.</summary>
    public int? StockMinimoMeLi { get; set; }
    public string? Notas { get; set; }
    public bool IsActive { get; set; }
    public decimal IvaPct { get; set; } = 21m;
    /// <summary>Modelo NUEVO de precios (solo OTROS). null = usa modelo legacy.</summary>
    public decimal? PrecioOtro { get; set; }
    public decimal? PrecioBar { get; set; }
    /// <summary>Precio del bulto completo (descuento por volumen, SOLO OTROS).</summary>
    public decimal? PrecioBulto { get; set; }
    public decimal? PrecioBultoOtro { get; set; }
    // Precios FUTUROS (cambio programado de precios — pedido 2026-05-20)
    public DateTime? FechaAplicaPreciosFuturos { get; set; }
    public decimal? PrecioPorKgFuturo { get; set; }
    public decimal? PrecioBarFuturo { get; set; }
    public decimal? PrecioOtroFuturo { get; set; }
    public decimal? PrecioBultoFuturo { get; set; }
    public decimal? PrecioBultoOtroFuturo { get; set; }
    public bool UsaPreciosFuturos { get; set; }
    // 2026-05-22: Clone Contabilium
    public bool IsVisibleEnVentas { get; set; } = true;
    public string? ImportSource { get; set; }
    // 2026-05-22: Packs prearmados (Pack x 100, etc.). Solo OTROS.
    public List<CafeProductoPackDto> Packs { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    // Datos del OEM (si esta vinculado). Permiten mostrar el sugerido del OEM en el listado.
    public decimal? OemPvpConIva { get; set; }
    public decimal? OemIvaPct { get; set; }

    // Calculados — Pvp1/Pvp2 se guardan SIN IVA. Multiplicar por (1 + IvaPct/100) da el con IVA.
    public decimal? Pvp1ConIva => Pvp1.HasValue ? Math.Round(Pvp1.Value * (1 + IvaPct / 100m), 2) : null;
    public decimal? Pvp2ConIva => Pvp2.HasValue ? Math.Round(Pvp2.Value * (1 + IvaPct / 100m), 2) : null;

    // OEM "sin IVA" calculado a partir del PvpConIva del OEM. Cuando hay OEM linkeado, este es el "sugerido real".
    public decimal? OemPvpSinIva => OemPvpConIva.HasValue && OemIvaPct.HasValue && OemIvaPct.Value > 0
        ? Math.Round(OemPvpConIva.Value / (1 + OemIvaPct.Value / 100m), 2)
        : OemPvpConIva;
}

public class CafeProductoPackDto
{
    public int Id { get; set; }
    public int Cantidad { get; set; }
    public string Nombre { get; set; } = "";
    public decimal? PrecioOverride { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
}

public class CafeProductoPackRequest
{
    public int? Id { get; set; }
    public int Cantidad { get; set; }
    public string Nombre { get; set; } = "";
    public decimal? PrecioOverride { get; set; }
    public int SortOrder { get; set; }
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
    public int? StockMinimoMeLi { get; set; }
    public string? Notas { get; set; }
    public decimal? IvaPct { get; set; }
    // Modelo NUEVO de precios (solo OTROS):
    public decimal? PrecioOtro { get; set; }
    public decimal? PrecioBar { get; set; }
    // Precio del bulto completo (descuento por volumen, SOLO OTROS):
    public decimal? PrecioBulto { get; set; }
    public decimal? PrecioBultoOtro { get; set; }
    /// <summary>Packs prearmados a crear junto con el producto. Opcional, solo OTROS.</summary>
    public List<CafeProductoPackRequest>? Packs { get; set; }
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
    public int? StockMinimoMeLi { get; set; }
    public bool ClearStockMinimoMeLi { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
    public decimal? IvaPct { get; set; }
    // Modelo NUEVO de precios (solo OTROS):
    public decimal? PrecioOtro { get; set; }
    public decimal? PrecioBar { get; set; }
    public bool ClearPrecioOtro { get; set; }
    public bool ClearPrecioBar { get; set; }
    // Precio del bulto completo (descuento por volumen, SOLO OTROS):
    public decimal? PrecioBulto { get; set; }
    public decimal? PrecioBultoOtro { get; set; }
    public bool ClearPrecioBulto { get; set; }
    public bool ClearPrecioBultoOtro { get; set; }

    // Precios FUTUROS (cambio programado)
    public DateTime? FechaAplicaPreciosFuturos { get; set; }
    public bool ClearFechaAplicaPreciosFuturos { get; set; }
    public decimal? PrecioPorKgFuturo { get; set; }
    public bool ClearPrecioPorKgFuturo { get; set; }
    public decimal? PrecioBarFuturo { get; set; }
    public bool ClearPrecioBarFuturo { get; set; }
    public decimal? PrecioOtroFuturo { get; set; }
    public bool ClearPrecioOtroFuturo { get; set; }
    public decimal? PrecioBultoFuturo { get; set; }
    public bool ClearPrecioBultoFuturo { get; set; }
    public decimal? PrecioBultoOtroFuturo { get; set; }
    public bool ClearPrecioBultoOtroFuturo { get; set; }
    /// <summary>Packs prearmados. Si null, no se tocan; si lista, reemplaza el set completo.</summary>
    public List<CafeProductoPackRequest>? Packs { get; set; }
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
    public string? NegocioRazonSocial { get; set; }
    public string? NegocioCondicionIva { get; set; }
    public string? NegocioIngresosBrutos { get; set; }
    public DateTime? NegocioInicioActividad { get; set; }
    public string? NegocioLocalidad { get; set; }
    public string? NegocioCp { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public string? ListaPreciosHeaderImageUrl { get; set; }
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
    public string? NegocioRazonSocial { get; set; }
    public string? NegocioCondicionIva { get; set; }
    public string? NegocioIngresosBrutos { get; set; }
    public DateTime? NegocioInicioActividad { get; set; }
    public string? NegocioLocalidad { get; set; }
    public string? NegocioCp { get; set; }
    public string? ListaPreciosHeaderImageUrl { get; set; }
}

// ===== Ventas =====
public class CafeVentaItemDto
{
    public int Id { get; set; }
    /// <summary>Nullable: para items "concepto libre" no hay producto del catálogo.</summary>
    public int? ProductoId { get; set; }
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
    /// <summary>True si es un item "concepto libre" (descripción + precio cargados a mano).</summary>
    public bool EsConceptoLibre { get; set; }
    /// <summary>Si va en envase plateado. Si EsDoyPack=false y EsEnvasePlateado=false → envase NEGRO (default).</summary>
    public bool EsEnvasePlateado { get; set; }
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
    public string? ClienteRazonSocial { get; set; }
    public string? ClienteDomicilioEntrega { get; set; }
    public string? ClienteComentariosComprobante { get; set; }
    public string? ClienteCuit { get; set; }
    public string? ClienteDireccion { get; set; }
    public string? ClienteLocalidad { get; set; }
    public string? ClienteCiudad { get; set; }
    public string? ClienteCp { get; set; }
    // ARCA — solo cargado si TipoComprobante in FA/FB/FC
    public string ArcaEstado { get; set; } = "no_aplica";
    public string? ArcaCae { get; set; }
    public DateTime? ArcaCaeVto { get; set; }
    public int? ArcaPtoVta { get; set; }
    public int? ArcaCbteNro { get; set; }
    public int? ArcaCbteTipoNum { get; set; }
    public string? ArcaError { get; set; }
    /// <summary>Si esta venta nació de otra (típicamente una proforma convertida a factura), Id origen.</summary>
    public int? OrigenVentaId { get; set; }
    /// <summary>Si esta proforma fue convertida a factura, Id de la factura resultante.</summary>
    public int? FacturadaComoVentaId { get; set; }
    /// <summary>True si esta venta fue creada como saldo de migración del sistema viejo
    /// (hay un Cafe_SaldosMigracion.VentaId apuntando a ella). Para mostrar badge visual.</summary>
    public bool EsSaldoMigracion { get; set; }
    /// <summary>Nota tipo post-it pegada por el admin a esta venta. Null = sin nota.</summary>
    public string? PinNota { get; set; }
    /// <summary>Token aleatorio para el link publico /comprobante/{token}. Null en ventas
    /// viejas pre-feature — al primer share el backend lo genera y persiste.</summary>
    public string? PublicToken { get; set; }
    /// <summary>Quien entrega la venta (Gabriel, Nacho, Maxi, Alexis, Miguel, Rodrigo, o
    /// 'Logistica tercerizada'). Opcional. Se muestra en el PDF.</summary>
    public string? EntregaPor { get; set; }
    /// <summary>Estado en el flujo de Preparacion de Pedidos. null = no entro al flujo.
    /// Valores: PARA_PREPARAR, EN_PREPARACION, LISTO, EN_CAMINO, ENTREGADO.</summary>
    public string? EstadoPreparacion { get; set; }
    public DateTime? PreparacionUpdatedAt { get; set; }
    /// <summary>Importe TOTAL con IVA que ARCA registró (para facturas A/B/C con CAE).
    /// Null en cotizaciones, proformas y facturas sin autorizar. Usado para mostrar
    /// el monto cobrable real en el listado de ventas.</summary>
    public decimal? ArcaImpTotal { get; set; }
    /// <summary>Si un repartidor marco "entregue" desde /repartidor/{token}, su Id+nombre.</summary>
    public int? EntregadoPorRepartidorId { get; set; }
    public string? EntregadoPorRepartidorNombre { get; set; }
    public DateTime? EntregadoAt { get; set; }
}

/// <summary>Tarjeta de venta en el tablero /cafe/preparacion. Trae solo lo que el
/// armador necesita ver (cliente, items, dia, repartidor). No trae montos ni info fiscal.</summary>
public class CafePreparacionVentaDto
{
    public int Id { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string ClienteNombre { get; set; } = "";
    public string? ClienteRazon { get; set; }
    public string? ClienteLocalidad { get; set; }
    public string? ClienteCiudad { get; set; }
    public string? ClienteTipo { get; set; }
    public string? WeekDays { get; set; }
    public string? EntregaPor { get; set; }
    public string EstadoPreparacion { get; set; } = "";
    public DateTime? PreparacionUpdatedAt { get; set; }
    public decimal Total { get; set; }
    public List<CafePreparacionItemDto> Items { get; set; } = new();
}

public class CafePreparacionItemDto
{
    public int Id { get; set; }
    public string ProductoNombre { get; set; } = "";
    public string Formato { get; set; } = "";
    public int Cantidad { get; set; }
    public string? Molienda { get; set; }
    public bool EsDoyPack { get; set; }
    public bool EsEnvasePlateado { get; set; }
    public string? Categoria { get; set; }
    public bool EsConceptoLibre { get; set; }
}

public class CafeCambiarEstadoPreparacionRequest
{
    /// <summary>Estado nuevo. Vacio o null = sacar la venta del flujo.</summary>
    public string EstadoNuevo { get; set; } = "";
    public string? OperadorNombre { get; set; }
    public string? Notas { get; set; }
}

public class ConvertirAFacturaRequest
{
    public string TipoFactura { get; set; } = "FB";
    public string? CondicionIva { get; set; }
}

public class CafeCotizarItemRequest
{
    public int ProductoId { get; set; }
    public string Formato { get; set; } = "1KG";
    public int Cantidad { get; set; } = 1;
    public string? Molienda { get; set; }
    public bool EsDoyPack { get; set; }
    public bool EsEnvasePlateado { get; set; }
    public decimal DescuentoPct { get; set; }
    /// <summary>Si el operador pisa el precio unitario a mano, viene cargado acá. Null = usar precio del catálogo.</summary>
    public decimal? PrecioUnitarioOverride { get; set; }
    /// <summary>Si es true, el item es "concepto libre" — usa DescripcionLibre + PrecioUnitarioOverride, no toca catálogo ni stock.</summary>
    public bool EsConceptoLibre { get; set; }
    /// <summary>Descripción libre que el operador escribió (solo si EsConceptoLibre).</summary>
    public string? DescripcionLibre { get; set; }
    /// <summary>Si viene seteado, pisa el nombre del producto en el snapshot de la línea (no afecta el catálogo).</summary>
    public string? DescripcionOverride { get; set; }
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
    public bool EsEnvasePlateado { get; set; }
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
    // Overrides ad-hoc para modo "Venta Rápida"
    public string? ClienteRazonSocialOverride { get; set; }
    public string? ClienteCuitOverride { get; set; }
    public string? ClienteDireccionOverride { get; set; }
    public string? ClienteLocalidadOverride { get; set; }
    public string? ClienteCiudadOverride { get; set; }
    public string? ClienteCpOverride { get; set; }
    public string? ClienteTelefonoOverride { get; set; }
    public string? ClienteDomicilioEntregaOverride { get; set; }
    public List<CafeCotizarItemRequest> Items { get; set; } = new();
    public decimal Descuento { get; set; }
    public string? Observaciones { get; set; }
    public string? WeekDays { get; set; }
    public bool IsPaid { get; set; }
    public string? TipoComprobante { get; set; }
    public string? CondicionIva { get; set; }
    public string? CondicionPago { get; set; }
    public string? EntregaPor { get; set; }
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
    // Overrides ad-hoc para modo "Venta Rápida"
    public string? ClienteRazonSocialOverride { get; set; }
    public string? ClienteCuitOverride { get; set; }
    public string? ClienteDireccionOverride { get; set; }
    public string? ClienteLocalidadOverride { get; set; }
    public string? ClienteCiudadOverride { get; set; }
    public string? ClienteCpOverride { get; set; }
    public string? ClienteTelefonoOverride { get; set; }
    public string? ClienteDomicilioEntregaOverride { get; set; }
    public string? Observaciones { get; set; }
    public string? TipoComprobante { get; set; }
    public string? CondicionIva { get; set; }
    public string? CondicionPago { get; set; }
    public string? WeekDays { get; set; }
    public bool? IsPaid { get; set; }
    public List<CafeCotizarItemRequest>? Items { get; set; }
    public decimal? Descuento { get; set; }
    public string? EntregaPor { get; set; }
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
    /// <summary>Si se pasa, la lista usa los precios "vigentes a esa fecha" — sirve para
    /// imprimir la lista nueva ANTES de que entre en vigencia.</summary>
    public DateTime? FechaVigencia { get; set; }
    public string? NumeroLista { get; set; }
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
    public string? ListaPreciosHeaderImageUrl { get; set; }
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
    public decimal Lista1Kg { get; set; }
    public decimal ListaMedio { get; set; }
    public decimal ListaCuarto { get; set; }
    public decimal DescuentoPct { get; set; }
}

public class CafeListaPreciosItemOtroDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public decimal Precio { get; set; }
    public decimal Lista { get; set; }
    public decimal DescuentoPct { get; set; }
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
    public DateTime? VigenteDesde { get; set; }
    public string? NumeroLista { get; set; }
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
    public decimal MargenPctSobreCosto { get; set; } = 100m;
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
    public decimal? MargenPctSobreCosto { get; set; }
}

public class UpdateCafeMarcaRequest
{
    public string? Nombre { get; set; }
    public int? ProveedorId { get; set; }
    public bool ClearProveedor { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
    public bool? BloqueaDescuento { get; set; }
    public decimal? MargenPctSobreCosto { get; set; }
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

// === Reglas de precios ===
public class CafeReglaPrecioDto
{
    public int Id { get; set; }
    public string TipoCliente { get; set; } = "OTRO";
    public string Categoria { get; set; } = "OTROS";
    public int? MarcaId { get; set; }
    public string? MarcaNombre { get; set; }
    public decimal DescuentoPct { get; set; }
}

public class CafeReglasPreciosResponse
{
    public List<string> TiposCliente { get; set; } = new();
    public List<string> Categorias { get; set; } = new();
    public List<CafeReglaPrecioDto> Reglas { get; set; } = new();
}

public class UpsertReglaPrecioRequest
{
    public string TipoCliente { get; set; } = "OTRO";
    public string Categoria { get; set; } = "OTROS";
    public int? MarcaId { get; set; }
    public decimal DescuentoPct { get; set; }
}

// --- Saldos migracion (saldos del sistema viejo a matchear con clientes) ---
public class CafeSaldoMigracionDto
{
    public int Id { get; set; }
    public string RazonSocialOriginal { get; set; } = "";
    public string? Tags { get; set; }
    public string? TipoDocumento { get; set; }
    public string? NroDocumento { get; set; }
    public string? CondicionIva { get; set; }
    public decimal Saldo { get; set; }
    public string Moneda { get; set; } = "$";
    public string Estado { get; set; } = "pendiente";
    public int? ClienteId { get; set; }
    public string? ClienteNombre { get; set; }
    public int? VentaId { get; set; }
    public string? VentaNumero { get; set; }
    public string? Notas { get; set; }
    public DateTime FechaImport { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CafeSaldosMigracionStatsDto
{
    public int Total { get; set; }
    public int Pendientes { get; set; }
    public int Asociados { get; set; }
    public int Ignorados { get; set; }
    public decimal SaldoPendiente { get; set; }
    public decimal SaldoAsociado { get; set; }
    public decimal SaldoTotal { get; set; }
}

public class CafeSaldoMigracionImportItem
{
    public string RazonSocialOriginal { get; set; } = "";
    public string? Tags { get; set; }
    public string? TipoDocumento { get; set; }
    public string? NroDocumento { get; set; }
    public string? CondicionIva { get; set; }
    public decimal Saldo { get; set; }
    public string? Moneda { get; set; }
}

public class CafeSaldoMigracionAsociarResultDto
{
    public int VentaId { get; set; }
    public string VentaNumero { get; set; } = "";
    public int ClienteId { get; set; }
}

public class CafeSaldoMigracionSugerenciaDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? RazonSocial { get; set; }
    public string? Cuit { get; set; }
    public int? CodigoInterno { get; set; }
    public string Motivo { get; set; } = "";
}

// --- Comodatos / Máquinas financiadas ---
public class CafeComodatoDto
{
    public int Id { get; set; }
    public int ClienteId { get; set; }
    public string? ClienteNombre { get; set; }
    public string Modalidad { get; set; } = "COMODATO";   // COMODATO | FINANCIADA
    public string Moneda { get; set; } = "ARS";           // ARS | USD
    public string? Marca { get; set; }
    public string? Modelo { get; set; }
    public string? NumeroSerie { get; set; }
    public DateTime? FechaEntrega { get; set; }
    public string Estado { get; set; } = "EN_CLIENTE";    // EN_CLIENTE | EN_TALLER | DEVUELTA | BAJA | PAGADA
    public DateTime? FechaDevolucion { get; set; }
    public string? Notas { get; set; }
    public decimal? ValorEstimado { get; set; }
    // FINANCIADA:
    public decimal? PrecioVenta { get; set; }
    public int? CuotasTotales { get; set; }
    public decimal? ValorCuota { get; set; }
    public int? DiaPagoMensual { get; set; }
    public decimal? SaldoFinanciamiento { get; set; }
    public decimal PagosAcumulados { get; set; }
    public int PagosCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CafeComodatoPagoDto
{
    public int Id { get; set; }
    public int ComodatoId { get; set; }
    public DateTime Fecha { get; set; }
    public decimal Importe { get; set; }
    public string? MedioPago { get; set; }
    public string? Notas { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CafeComodatoDetalleDto
{
    public CafeComodatoDto? Comodato { get; set; }
    public List<CafeComodatoPagoDto> Pagos { get; set; } = new();
}

public class CafeComodatosStatsDto
{
    public int ComodatosTotales { get; set; }
    public int ComodatosActivos { get; set; }
    public int FinanciadasTotales { get; set; }
    public int FinanciadasActivas { get; set; }
    public int FinanciadasPagadas { get; set; }
    /// <summary>Saldo legacy — equivale a SaldoFinanciamientoArs. Mantenido por compatibilidad.</summary>
    public decimal SaldoFinanciamientoTotal { get; set; }
    /// <summary>Saldo pendiente en ARS de financiadas activas.</summary>
    public decimal SaldoFinanciamientoArs { get; set; }
    /// <summary>Saldo pendiente en USD de financiadas activas.</summary>
    public decimal SaldoFinanciamientoUsd { get; set; }
    public decimal ValorEstimadoComodatos { get; set; }
}

public class CafeComodatoCreateRequest
{
    public int ClienteId { get; set; }
    public string Modalidad { get; set; } = "COMODATO";
    public string? Moneda { get; set; } = "ARS";
    public string? Marca { get; set; }
    public string? Modelo { get; set; }
    public string? NumeroSerie { get; set; }
    public DateTime? FechaEntrega { get; set; }
    public string? Notas { get; set; }
    public decimal? ValorEstimado { get; set; }
    public decimal? PrecioVenta { get; set; }
    public int? CuotasTotales { get; set; }
    public decimal? ValorCuota { get; set; }
    public int? DiaPagoMensual { get; set; }
}

public class CafeComodatoUpdateRequest
{
    public int? ClienteId { get; set; }
    public string? Moneda { get; set; }
    public string? Marca { get; set; }
    public string? Modelo { get; set; }
    public string? NumeroSerie { get; set; }
    public DateTime? FechaEntrega { get; set; }
    public string? Estado { get; set; }
    public DateTime? FechaDevolucion { get; set; }
    public string? Notas { get; set; }
    public decimal? ValorEstimado { get; set; }
    public decimal? PrecioVenta { get; set; }
    public int? CuotasTotales { get; set; }
    public decimal? ValorCuota { get; set; }
    public int? DiaPagoMensual { get; set; }
}

public class CafeComodatoPagoRequest
{
    public DateTime Fecha { get; set; }
    public decimal Importe { get; set; }
    public string? MedioPago { get; set; }
    public string? Notas { get; set; }
}

// ============================================================
// Preventas / Pedidos de vendedor
// ============================================================
public class CafePreventaAdminDto
{
    public int Id { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string VendedorNombre { get; set; } = "";
    public string ClienteNombre { get; set; } = "";
    public int TotalItems { get; set; }
    public string? Notas { get; set; }
    public string? FotoUrl { get; set; }
    public string Estado { get; set; } = "pendiente";
    public int? VentaIdFinal { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CafePreventaItemDto
{
    public int Id { get; set; }
    public int? ProductoId { get; set; }
    public string? ProductoNombre { get; set; }
    public string? DescripcionLibre { get; set; }
    public decimal Cantidad { get; set; }
    public decimal? PrecioSugerido { get; set; }
    public string? Observaciones { get; set; }
}

public class CafePreventaDetalleDto
{
    public int Id { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public int? ClienteId { get; set; }
    public string? ClienteNombreLibre { get; set; }
    public string? ClienteNombreCatalogo { get; set; }
    public string? ClienteTelefono { get; set; }
    public string? Notas { get; set; }
    public string? FotoUrl { get; set; }
    public string Estado { get; set; } = "pendiente";
    public DateTime CreatedAt { get; set; }
    public List<CafePreventaItemDto> Items { get; set; } = new();
    public int TotalItems { get; set; }
}

public class CafePreventaVendedorDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string Token { get; set; } = "";
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
}


public class ClienteSaldoPendienteDto
{
    public int ClienteId { get; set; }
    public string Nombre { get; set; } = "";
    public string? Tipo { get; set; }
    public string? Telefono { get; set; }
    public string? MapeoLink { get; set; }
    public int? CodigoInterno { get; set; }
    public int CantidadVentasPendientes { get; set; }
    public decimal SaldoPendiente { get; set; }
    public DateTime FechaMasAntigua { get; set; }
    public int DiasMasAntigua { get; set; }
    public bool TieneSaldoMigracion { get; set; }
    /// <summary>Saldo de comprobantes tipo X y PRO (no fiscales). Default 0.</summary>
    public decimal SaldoCotizacion { get; set; }
    /// <summary>Saldo de comprobantes tipo FA, FB, FC (con CAE, fiscales). Default 0.</summary>
    public decimal SaldoFactura { get; set; }
}
