namespace Api.DTOs;

// ===== Clientes =====
public record CafeClienteDto(
    int Id, string? Codigo, string Nombre, string? RazonSocial, string Tipo,
    string? Cuit, string? Telefono, string? Email,
    string? Direccion, string? Localidad, string? Ciudad, string? Cp,
    string? CondicionIvaDefault,
    string? DomicilioEntrega,
    string? Notas, string? ComentariosComprobante,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt,
    int? CodigoInterno = null, string? MapeoLink = null,
    decimal? MapeoLat = null, decimal? MapeoLng = null,
    bool TieneMiniImpresora = false);

public class CreateCafeClienteRequest
{
    public string Nombre { get; set; } = string.Empty;
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
    /// <summary>Enlace de Google Maps cargado al crear el cliente. Si viene, intentamos
    /// extraer las coordenadas automáticamente en el backend.</summary>
    public string? MapeoLink { get; set; }
    /// <summary>Código interno (correlativo) pre-asignado en el frontend antes de guardar.
    /// Si viene, el backend lo valida: si está libre lo usa, si está tomado asigna el siguiente disponible.</summary>
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
    /// <summary>Si true, en /cafe/preparacion las cards de este cliente muestran botón mini impresora.</summary>
    public bool? TieneMiniImpresora { get; set; }
}

// ===== Productos =====
// Convencion: Pvp1, Pvp2 se guardan SIN IVA. La UI calcula el con-IVA usando IvaPct.
public record CafeProductoDto(
    int Id, string? Sku, string? Barcode,
    string Nombre, string Categoria, string? Marca,
    int? MarcaId, string? MarcaNombre,
    decimal Costo, decimal? PrecioPorKg,
    decimal? Pvp1, decimal? Pvp2,
    decimal? BarPctSobreCosto, int? UxB,
    int? OemId, string? OemCodigo,
    decimal StockGramos, int StockUnidades,
    string? Notas, bool IsActive,
    decimal IvaPct,
    DateTime CreatedAt, DateTime? UpdatedAt,
    decimal? OemPvpConIva = null, decimal? OemIvaPct = null,
    // Modelo nuevo de precios para OTROS (null = no aplica / cae al modelo legacy):
    decimal? PrecioOtro = null, decimal? PrecioBar = null,
    // Precio del bulto completo (descuento por volumen, SOLO OTROS). Si cantidad >= UxB,
    // el sistema cobra (bultosCompletos × PrecioBulto + sueltas × PrecioBar/Otro).
    decimal? PrecioBulto = null, decimal? PrecioBultoOtro = null,
    // Precios FUTUROS (cambio programado de precios). Se cargan ahora pero se aplican recien
    // a partir de FechaAplicaPreciosFuturos.
    DateTime? FechaAplicaPreciosFuturos = null,
    decimal? PrecioPorKgFuturo = null,
    decimal? PrecioBarFuturo = null,
    decimal? PrecioOtroFuturo = null,
    decimal? PrecioBultoFuturo = null,
    decimal? PrecioBultoOtroFuturo = null,
    bool UsaPreciosFuturos = false,
    // 2026-05-22: Clone Contabilium
    bool IsVisibleEnVentas = true,
    string? ImportSource = null,
    // 2026-05-22: Packs prearmados (Pack x 100, etc.). Solo OTROS.
    List<CafeProductoPackDto>? Packs = null,
    // 2026-05-25: stock mínimo de reserva al pushear a MeLi (override por producto, null = global)
    int? StockMinimoMeLi = null,
    // 2026-06-01: para productos "shell" linkeados a publicaciones MeLi via componentes,
    // cantidad de cestos/combos armables a partir del stock real de los componentes (min).
    // Null si no aplica (productos físicos normales con stock propio).
    int? StockArmable = null,
    // 2026-06-02: desglose de stock por deposito (para mostrar 'X propio + Y Full' en la UI)
    // StockPropio = stock en deposito principal '9 de Abril'.
    // StockFull = stock en deposito 'Full MeLi'. Null si no esta en ese deposito.
    int? StockPropio = null,
    int? StockFull = null,
    // 2026-06-10: multiplicador del OEM. Precio efectivo = OemPvpConIva × MultiplicadorOem.
    // Necesario para mostrar el precio real en el catálogo cuando hay OEM linkeado.
    decimal? MultiplicadorOem = null,
    // 2026-06-10: si true, el producto NO tiene precio diferenciado para BAR — todos los
    // clientes (BAR y OTRO) pagan el PrecioOtro. Default false (comportamiento legacy).
    bool SinPrecioBar = false);

public record CafeProductoPackDto(
    int Id, int Cantidad, string Nombre, decimal? PrecioOverride,
    bool IsActive, int SortOrder);

/// <summary>Item enviado por el frontend cuando edita los packs de un producto. Si Id viene
/// con valor, se actualiza el pack existente; si Id es null, se crea uno nuevo. Los packs que
/// existen en DB pero NO vienen en la lista se eliminan.</summary>
public class CafeProductoPackRequest
{
    public int? Id { get; set; }
    public int Cantidad { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public decimal? PrecioOverride { get; set; }
    public int SortOrder { get; set; }
}

public class CreateCafeProductoRequest
{
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string Nombre { get; set; } = string.Empty;
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
    /// <summary>Override por producto: reserva interna que se le resta al stock al pushear a MeLi.
    /// Null = usar el global de AppSettings (default 1). 0 = sin reserva. N = reservar N unidades.</summary>
    public int? StockMinimoMeLi { get; set; }
    public string? Notas { get; set; }
    public decimal? IvaPct { get; set; }
    // Modelo nuevo de precios para OTROS:
    public decimal? PrecioOtro { get; set; }
    public decimal? PrecioBar { get; set; }
    // Precio del bulto completo (descuento por volumen, SOLO OTROS).
    public decimal? PrecioBulto { get; set; }
    public decimal? PrecioBultoOtro { get; set; }
    /// <summary>2026-06-10: si true, todos los clientes pagan PrecioOtro (sin diferenciar BAR).</summary>
    public bool SinPrecioBar { get; set; } = false;
    /// <summary>Packs prearmados a crear junto con el producto. Opcional. Solo OTROS.</summary>
    public List<CafeProductoPackRequest>? Packs { get; set; }
}

// ===== Kits (productos compuestos / BOM) =====
public record CafeKitDto(
    int Id, string Sku, string Nombre, string? Descripcion,
    string Categoria, string? Marca, int? MarcaId, string? MarcaNombre,
    decimal? Pvp1, decimal? Pvp2, decimal IvaPct,
    string? Notas, bool IsActive,
    int StockVirtual, decimal CostoCalculado,
    List<CafeKitItemDto> Items,
    DateTime CreatedAt, DateTime? UpdatedAt);

public record CafeKitItemDto(
    int Id,
    int ProductoId,
    string? ProductoSku,
    string ProductoNombre,
    int ProductoStock,
    decimal Cantidad,
    int KitsPosibles); // floor(stock / cantidad) — cuantos kits permite armar este componente

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

public record CafeHistorialPrecioDto(
    int Id,
    decimal? Pvp1Anterior, decimal? Pvp2Anterior, decimal? CostoAnterior, decimal? IvaPctAnterior,
    decimal? Pvp1Nuevo, decimal? Pvp2Nuevo, decimal? CostoNuevo, decimal? IvaPctNuevo,
    DateTime ChangedAt, string? ChangedBy, string? Motivo);

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
    public bool ClearBarPctSobreCosto { get; set; }   // marca explicita para vaciar
    public bool ClearUxB { get; set; }
    public bool ClearOemId { get; set; }
    public decimal? StockGramos { get; set; }
    public int? StockUnidades { get; set; }
    /// <summary>Override por producto: reserva interna que se le resta al stock al pushear a MeLi.
    /// Null = no cambiar. 0 explícito = sin reserva. ClearStockMinimoMeLi=true → poner null.</summary>
    public int? StockMinimoMeLi { get; set; }
    public bool ClearStockMinimoMeLi { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
    public decimal? IvaPct { get; set; }
    // Modelo nuevo de precios para OTROS:
    public decimal? PrecioOtro { get; set; }
    public decimal? PrecioBar { get; set; }
    public bool ClearPrecioOtro { get; set; }
    public bool ClearPrecioBar { get; set; }
    /// <summary>2026-06-10: flag "todos los clientes pagan PrecioOtro" — si null, no cambia.</summary>
    public bool? SinPrecioBar { get; set; }
    // Precio del bulto completo (descuento por volumen, SOLO OTROS).
    public decimal? PrecioBulto { get; set; }
    public decimal? PrecioBultoOtro { get; set; }
    public bool ClearPrecioBulto { get; set; }
    public bool ClearPrecioBultoOtro { get; set; }

    // Precios FUTUROS (cambio programado). Si vienen cargados, se guardan y se aplican
    // automaticamente cuando hoy >= FechaAplicaPreciosFuturos.
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

    /// <summary>Packs prearmados del producto. Si es null, no se tocan los packs existentes.
    /// Si es una lista (incluso vacia), reemplaza la lista actual: items con Id se actualizan,
    /// items sin Id se crean, items que no aparezcan se borran.</summary>
    public List<CafeProductoPackRequest>? Packs { get; set; }
}

// ===== Settings =====
public record CafeSettingDto(
    decimal CostoFraccionamiento, decimal RedondeoMultiplo,
    decimal MargenOtrosBarPct, decimal MargenOtrosNoBarPct,
    string? NegocioNombre, string? NegocioTelefono, string? NegocioWhatsappNumero,
    string? NegocioDireccion, string? NegocioCuit,
    string? NegocioEmail, string? NegocioWeb, string? NegocioLogoUrl,
    string? WhatsappMensajeTemplate, string? WhatsappMensajeClienteTemplate,
    string? NegocioRazonSocial, string? NegocioCondicionIva,
    string? NegocioIngresosBrutos, DateTime? NegocioInicioActividad,
    string? NegocioLocalidad, string? NegocioCp,
    DateTime? UpdatedAt,
    string? ListaPreciosHeaderImageUrl = null,
    string? NegocioTelefono2 = null,
    string? NegocioWeb2 = null,
    decimal? CostoFraccionamientoFuturo = null,
    DateTime? FechaAplicaFraccionamientoFuturo = null);

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
    public string? NegocioTelefono2 { get; set; }
    public string? NegocioWeb2 { get; set; }
}

// ===== Ventas =====
public record CafeVentaItemDto(
    int Id, int? ProductoId, string ProductoNombre, string Categoria,
    string Formato, int Cantidad,
    decimal PrecioUnitario, decimal CostoUnitario, decimal Subtotal,
    decimal GramosDescontados,
    string? Molienda, bool EsDoyPack,
    decimal DescuentoPct,
    bool EsConceptoLibre = false,
    bool EsEnvasePlateado = false,
    // 2026-06-08: items que vinieron del mismo combo (mismo ComboOrigenId)
    // se agrupan en una sola línea en el PDF/factura. ComboOrigenNombre se completa
    // sólo si Cafe_Combos tiene un registro vigente; si fue borrado queda null y
    // el agrupado igual funciona (usa "Combo" como fallback).
    int? ComboOrigenId = null,
    string? ComboOrigenNombre = null,
    string? ComboOrigenSku = null);

public record CafeVentaDto(
    int Id, string Numero, DateTime Fecha,
    int? ClienteId, string? ClienteNombre, string? ClienteTipo, string? ClienteTelefono,
    // 2026-06-08: codigo interno del cliente para mostrar (#123) al lado del nombre en el listado
    int? ClienteCodigoInterno,
    decimal Subtotal, decimal Descuento, decimal Total,
    decimal CostoTotal, decimal Margen,
    string? Observaciones, string Estado,
    string? WeekDays, bool EnRadar, bool IsPaid, bool Retira,
    string TipoComprobante, string CondicionIva, string CondicionPago,
    DateTime CreatedAt,
    List<CafeVentaItemDto> Items,
    string? ClienteRazonSocial,
    string? ClienteDomicilioEntrega,
    string? ClienteComentariosComprobante,
    string? ClienteCuit,
    string? ClienteDireccion,
    string? ClienteLocalidad,
    string? ClienteCiudad,
    string? ClienteCp,
    string ArcaEstado,
    string? ArcaCae,
    DateTime? ArcaCaeVto,
    int? ArcaPtoVta,
    int? ArcaCbteNro,
    int? ArcaCbteTipoNum,
    string? ArcaError,
    int? OrigenVentaId = null,
    int? FacturadaComoVentaId = null,
    bool EsSaldoMigracion = false,
    string? PinNota = null,
    string? PublicToken = null,
    string? EntregaPor = null,
    string? EstadoPreparacion = null,
    DateTime? PreparacionUpdatedAt = null,
    decimal? ArcaImpTotal = null,
    int? EntregadoPorRepartidorId = null,
    string? EntregadoPorRepartidorNombre = null,
    DateTime? EntregadoAt = null,
    string? DriveFileId = null,
    DateTime? DriveSubidoAt = null,
    int DriveSubidasCount = 0,
    string? ComentarioArmado = null,
    string? EscaneadoPorRepartidorNombre = null,
    DateTime? EscaneadoAt = null,
    // 2026-06-05: Transporte
    bool PorTransporte = false,
    string? TransporteEmpresa = null,
    string? TransporteDestino = null,
    // 2026-06-05: Quien cargo la venta (OSMAR/GERMAN/GABRIEL/etc). Null para ventas previas a la migracion.
    string? CreadoPorOperador = null);

public class CafeCotizarItemRequest
{
    public int ProductoId { get; set; }
    /// <summary>2026-06-01: si está seteado, el item es un Kit (producto compuesto). En ese caso
    /// ProductoId puede ser 0/ignorado. Formato y Cantidad se interpretan a nivel Kit (no del componente).
    /// Al guardar la venta, se descuentan los componentes del Kit (Cafe_KitItems).</summary>
    public int? KitId { get; set; }
    /// <summary>2026-06-05: si está seteado, el item es un Servicio del catalogo Cafe_Servicios
    /// (envio, mano de obra, etc). No descuenta stock. ProductoId queda en 0/null.</summary>
    public int? ServicioId { get; set; }
    public string Formato { get; set; } = "1KG";  // 1KG | MEDIO | CUARTO | UNIT
    public int Cantidad { get; set; } = 1;
    public string? Molienda { get; set; }   // EN GRANOS | MOLIDO FILTRO | MOLIDO ESPRESS | null
    public bool EsDoyPack { get; set; }
    public bool EsEnvasePlateado { get; set; }
    public decimal DescuentoPct { get; set; }   // 0-100, descuento porcentual de la linea
    /// <summary>Si el operador pisa el precio unitario a mano, viene cargado acá. Si es null,
    /// se calcula automáticamente con producto + matriz de precios. Si es &gt;= 0, ese
    /// valor reemplaza al calculado (después se le aplica el descuento de la línea).</summary>
    public decimal? PrecioUnitarioOverride { get; set; }

    // ---- Concepto libre ----
    /// <summary>Si es true, el item es un "concepto libre" (sin producto del catálogo).
    /// En ese caso ProductoId se ignora y se usan DescripcionLibre + PrecioUnitarioOverride.</summary>
    public bool EsConceptoLibre { get; set; }
    /// <summary>Texto que va en la descripción del item (solo si EsConceptoLibre=true).</summary>
    public string? DescripcionLibre { get; set; }

    /// <summary>Si viene seteado, pisa el nombre del producto en el snapshot (ProductoNombreSnapshot)
    /// de esa línea de venta. NO modifica el producto del catálogo. Aplica a items del catálogo
    /// (no a concepto libre, que ya usa DescripcionLibre).</summary>
    public string? DescripcionOverride { get; set; }

    /// <summary>2026-06-08: Si este item proviene de un combo agregado a la venta (botón "Agregar combo"
    /// o producto compuesto buscado por SKU), marca el ID del combo origen. Sólo presentación:
    /// en el PDF/factura los items con mismo ComboOrigenId se agrupan en una sola línea con el
    /// nombre del combo. En la pantalla de carga/edit y en /cafe/preparacion se siguen viendo desglosados.</summary>
    public int? ComboOrigenId { get; set; }
}

public class CafeCotizarRequest
{
    public int? ClienteId { get; set; }
    public string? ClienteTipo { get; set; }  // override si no hay clienteId
    public List<CafeCotizarItemRequest> Items { get; set; } = new();
    public decimal Descuento { get; set; }
    // 2026-06-18: si se está editando una venta existente, mandar su Id para que el
    // cotizador no cuente su propio stock reservado como conflicto.
    public int? EditandoVentaId { get; set; }
}

public record CafeCotizadoItemDto(
    int ProductoId, string ProductoNombre, string Categoria,
    string Formato, int Cantidad,
    decimal PrecioUnitario, decimal CostoUnitario, decimal Subtotal,
    decimal GramosNecesarios, decimal StockGramosDisponible, int StockUnidadesDisponible,
    bool StockOk, string? Aviso,
    string? Molienda, bool EsDoyPack,
    decimal DescuentoPct,
    bool EsEnvasePlateado = false,
    int? KitId = null,           // 2026-06-01: si != null, este item es un Kit (producto compuesto)
    string? KitSku = null);       // SKU del kit para mostrar en la grilla

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

    // ---- Overrides ad-hoc para modo "Venta Rápida" (sin cliente del catálogo) ----
    // Si ClienteId es null o 0, estos campos se snapshotean en la venta tal como vienen.
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
    public bool EnRadar { get; set; }
    public bool Retira { get; set; }
    /// <summary>2026-06-05: si true, la venta se despacha por empresa de transporte.</summary>
    public bool PorTransporte { get; set; }
    public string? TransporteEmpresa { get; set; }
    public string? TransporteDestino { get; set; }
    public bool IsPaid { get; set; }
    public string? TipoComprobante { get; set; }
    public string? CondicionIva { get; set; }
    public string? CondicionPago { get; set; }
    public string? EntregaPor { get; set; }
    /// <summary>2026-06-02: Nota interna para armado (post-it en /cafe/preparacion). NO sale en PDF.</summary>
    public string? ComentarioArmado { get; set; }
}

public class UpdateCafeVentaFlagsRequest
{
    public string? WeekDays { get; set; }
    public bool? EnRadar { get; set; }
    public bool? Retira { get; set; }
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

    // ---- Overrides ad-hoc para modo "Venta Rápida" (sin cliente del catálogo) ----
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
    public bool? EnRadar { get; set; }
    public bool? Retira { get; set; }
    public bool? PorTransporte { get; set; }
    public string? TransporteEmpresa { get; set; }
    public string? TransporteDestino { get; set; }
    public bool? IsPaid { get; set; }
    public List<CafeCotizarItemRequest>? Items { get; set; }
    public decimal? Descuento { get; set; }
    public string? EntregaPor { get; set; }
    /// <summary>2026-06-02: Nota interna para armado (post-it en /cafe/preparacion). NO sale en PDF.</summary>
    public string? ComentarioArmado { get; set; }
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
    string? Notas,
    string? Cuit, string? CategoriaImpositiva,
    string? Direccion, string? CodigoPostal, string? Provincia, string? Ciudad, string? Web,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt,
    int ComprasCount, decimal TotalComprado);

public class CreateCafeProveedorRequest
{
    public string Nombre { get; set; } = string.Empty;
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
    int SortOrder,
    bool EsEnvasePlateado = false);

public record CafeComboDto(
    int Id, string Nombre, string? Descripcion,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt,
    int ItemsCount,
    decimal PreviewPrecioBar,    // suma de PVP1*cantidad (con costo de fraccionamiento si aplica)
    decimal PreviewPrecioOtro,   // suma de PVP2*cantidad
    List<CafeComboItemDto> Items,
    string? Sku = null,           // 2026-06-01: para que el buscador de venta pueda matchear por SKU
    bool EsCompuesto = false,     // 2026-06-01: si true, aparece tambien en pestana "Producto" del buscador
    // 2026-06-18: OEM en compuestos. Cuando esta cargado, el precio del compuesto se calcula
    // como OemPvpConIva * MultiplicadorOem (ignorando suma de componentes).
    int? OemId = null,
    string? OemCodigo = null,
    decimal? OemPvpConIva = null,
    decimal? OemIvaPct = null,
    decimal? MultiplicadorOem = null,
    // 2026-06-18: costo s/IVA = suma de (componente.Costo × cantidad), y stock disponible = min(componente.Stock / cantidad)
    decimal CostoSumaComponentes = 0m,
    int StockDisponible = 0);

public class CafeComboItemRequest
{
    public int ProductoId { get; set; }
    public string Formato { get; set; } = "1KG";
    public int Cantidad { get; set; } = 1;
    public string? Molienda { get; set; }
    public bool EsDoyPack { get; set; }
    public bool EsEnvasePlateado { get; set; }
    public int SortOrder { get; set; }
}

public class CreateCafeComboRequest
{
    public string Nombre { get; set; } = string.Empty;
    public string? Descripcion { get; set; }
    public List<CafeComboItemRequest> Items { get; set; } = new();
    // 2026-06-18: OEM opcional para compuestos. Si EsCompuesto + OemId presente,
    // el precio se toma de OEM.PvpConIva * MultiplicadorOem en vez de sumar componentes.
    public int? OemId { get; set; }
    public decimal? MultiplicadorOem { get; set; }
    public bool? EsCompuesto { get; set; }
}

public class UpdateCafeComboRequest
{
    public string? Nombre { get; set; }
    public string? Descripcion { get; set; }
    public bool? IsActive { get; set; }
    public List<CafeComboItemRequest>? Items { get; set; }
    // 2026-06-18: OEM. Si OemId viene con valor, se actualiza; si viene null explicito
    // (con el flag ClearOem=true) se desvincula.
    public int? OemId { get; set; }
    public decimal? MultiplicadorOem { get; set; }
    public bool? ClearOem { get; set; }
    public bool? EsCompuesto { get; set; }
}

// ===== OEMs (lista del proveedor) =====
public record CafeOemDto(
    int Id, string Codigo, string? Descripcion, string? Marca,
    int? MarcaId, string? MarcaNombre,
    decimal Costo, decimal? PvpConIva, decimal? IvaPct,
    string? Barcode, string? Proveedor, int? UxB,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt, DateTime? LastImportAt,
    int VariantesCount,
    // 2026-06-10: URL al producto en el sitio del proveedor (Colombraro, etc.)
    string? UrlWeb = null,
    // 2026-06-11: datos extraidos de la web del proveedor (scraping)
    string? ImagenUrl = null,
    string? DescripcionWeb = null,
    string? EspecificacionesJson = null,
    DateTime? ScrapedAt = null);

public class CreateCafeOemRequest
{
    public string Codigo { get; set; } = string.Empty;
    public string? Descripcion { get; set; }
    public string? Marca { get; set; }
    public int? MarcaId { get; set; }
    public decimal Costo { get; set; }
    public decimal? PvpConIva { get; set; }
    public decimal? IvaPct { get; set; }
    public string? Barcode { get; set; }
    public string? Proveedor { get; set; }
    public int? UxB { get; set; }
    public string? UrlWeb { get; set; }
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
    public string? UrlWeb { get; set; }
    public bool ClearUrlWeb { get; set; }
    public bool? IsActive { get; set; }
}

public record CafeOemImportResultDto(
    int Creados, int Actualizados, int Omitidos,
    string? Proveedor,
    int VariantesPropagadas,
    List<string> Errores);

// ===== Consultas (busqueda interna en lenguaje natural) =====
public class CafeConsultaRequest
{
    public string Query { get; set; } = string.Empty;
}

public class CafeConsultaResultDto
{
    /// <summary>Como renderizar: "tabla" | "ficha" | "vacio" | "ayuda" | "error".</summary>
    public string Tipo { get; set; } = "vacio";
    public string Titulo { get; set; } = "";
    public string? Subtitulo { get; set; }
    public string? Total { get; set; }
    /// <summary>Headers de la tabla (en orden). Si Tipo=tabla.</summary>
    public List<string> Columnas { get; set; } = new();
    /// <summary>Filas de la tabla. Cada fila es un dict {columna => valor formateado}. Si Tipo=tabla.</summary>
    public List<Dictionary<string, string>> Filas { get; set; } = new();
    /// <summary>Para Tipo=ficha: pares clave/valor.</summary>
    public List<KeyValuePair<string, string>> Datos { get; set; } = new();
    /// <summary>Lista de ejemplos cuando no se encuentra nada o no se entiende.</summary>
    public List<string> Ayuda { get; set; } = new();
}

// ===== Listas de precios =====
public class CafeListaPreciosFiltroRequest
{
    public int? ClienteId { get; set; }       // si > 0 toma el tipo del cliente
    public string? Tipo { get; set; }          // BAR | OTRO — usado si no hay clienteId
    public List<int>? MarcaIds { get; set; }   // null o vacio = todas
    public string? Categoria { get; set; }     // CAFE | OTROS | null = ambas
    public string? Observaciones { get; set; } // texto libre que va en el bloque comercial del PDF
    /// <summary>Si se pasa, se generan los precios "vigentes" a esa fecha — usa el precio Futuro
    /// si la fecha es >= FechaAplicaPreciosFuturos del producto. Sino usa el precio Actual.
    /// Sirve para entregar al cliente la lista con los precios nuevos antes de que se apliquen.</summary>
    public DateTime? FechaVigencia { get; set; }
    /// <summary>Numero/etiqueta de la lista (texto libre — ej: "5", "5/2026", "Mayo 2026").
    /// Si esta cargado, aparece en el header del PDF debajo del titulo "LISTA DE PRECIOS".</summary>
    public string? NumeroLista { get; set; }
}

public record CafeListaPreciosNegocioDto(
    string? Nombre, string? Telefono, string? WhatsappNumero,
    string? Direccion, string? Cuit,
    string? Email, string? Web, string? LogoUrl,
    string? ListaPreciosHeaderImageUrl = null);

public record CafeListaPreciosClienteDto(
    int? Id, string? Codigo, string? Nombre, string Tipo,
    string? Telefono, string? Email);

public record CafeListaPreciosItemCafeDto(
    int ProductoId, string? Sku, string Nombre,
    decimal Precio1Kg, decimal PrecioMedio, decimal PrecioCuarto,
    decimal Lista1Kg, decimal ListaMedio, decimal ListaCuarto,
    decimal DescuentoPct);

public record CafeListaPreciosItemOtroDto(
    int ProductoId, string? Sku, string Nombre,
    decimal Precio, decimal Lista, decimal DescuentoPct);

public record CafeListaPreciosMarcaGroupDto(
    int? MarcaId, string MarcaNombre, string? ProveedorNombre,
    List<CafeListaPreciosItemCafeDto> ItemsCafe,
    List<CafeListaPreciosItemOtroDto> ItemsOtros);

public record CafeListaPreciosPreviewDto(
    DateTime Fecha,
    DateTime ValidezHasta,
    string TipoCliente,
    CafeListaPreciosNegocioDto Negocio,
    CafeListaPreciosClienteDto? Cliente,
    List<CafeListaPreciosMarcaGroupDto> Grupos,
    string? Observaciones,
    // Si se calcularon precios para una fecha distinta a hoy (precios "vigentes desde X").
    // El frontend lo usa para imprimir un banner "Vigente desde dd/MM/yyyy" arriba de la lista.
    DateTime? VigenteDesde = null,
    // Numero/etiqueta de la lista (ej: "5/2026") — se imprime debajo de "LISTA DE PRECIOS".
    string? NumeroLista = null);

// ===== Marcas =====
public record CafeMarcaDto(
    int Id, string Nombre,
    int? ProveedorId, string? ProveedorNombre,
    string? Notas, bool IsActive, bool BloqueaDescuento,
    decimal MargenPctSobreCosto,
    DateTime CreatedAt, DateTime? UpdatedAt,
    int ProductosCount, int OemsCount);

public class CreateCafeMarcaRequest
{
    public string Nombre { get; set; } = string.Empty;
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

// ===== Descuentos por canal x marca =====
public record CafeDescuentoClienteDto(
    int Id,
    string TipoCliente,
    int? MarcaId,
    string? MarcaNombre,
    bool MarcaBloqueaDescuento,
    decimal DescuentoPct);

// Vista grilla: una fila por marca activa, columnas con el descuento por tipo de cliente.
public record CafeDescuentoGrillaFila(
    int? MarcaId,                 // null = fila "(general)"
    string MarcaNombre,           // "(General — todas las marcas)" para la fila general
    bool BloqueaDescuento,
    Dictionary<string, decimal?> DescuentoPorTipo);  // { "BAR": 25, "OTRO": 25 }

public record CafeDescuentoGrillaResponse(
    List<string> Tipos,
    List<CafeDescuentoGrillaFila> Filas);

public class UpsertDescuentoRequest
{
    public string TipoCliente { get; set; } = "OTRO";
    public int? MarcaId { get; set; }   // null = general
    public decimal DescuentoPct { get; set; }
}

// ===== Reglas de precios (tipo cliente x categoria x marca opcional) =====
public record CafeReglaPrecioDto(
    int Id, string TipoCliente, string Categoria,
    int? MarcaId, string? MarcaNombre, decimal DescuentoPct);

public class UpsertReglaPrecioRequest
{
    public string TipoCliente { get; set; } = "OTRO";
    public string Categoria { get; set; } = "OTROS";
    public int? MarcaId { get; set; }
    public decimal DescuentoPct { get; set; }
}

// ===== Duplicar comprobante =====
/// <summary>Payload que devuelve el endpoint duplicar — datos pre-armados para que el
/// frontend abra el modal de "Nueva venta" con todo cargado. NO crea la venta nueva
/// en la DB; eso ocurre cuando el usuario confirma desde el modal.</summary>
public record DuplicarVentaPayloadDto(
    int? ClienteId,
    string? ClienteNombre,
    string ClienteTipo,
    string TipoComprobante,
    string CondicionIva,
    string CondicionPago,
    string? WeekDays,
    bool EnRadar,
    bool Retira,
    string? Observaciones,
    List<CafeCotizarItemRequest> Items,
    string? OrigenNumero,    // ej "CAFE-2026-0001" — solo para mostrar en el modal "Duplicado de X"
    string? ComentarioArmado = null);

// ===== Convertir Proforma → Factura =====
public class ConvertirAFacturaRequest
{
    /// <summary>"FA" | "FB" | "FC" — el tipo de factura a emitir</summary>
    public string TipoFactura { get; set; } = "FB";
    /// <summary>Condición IVA del receptor. Si null, se hereda de la proforma.</summary>
    public string? CondicionIva { get; set; }
}
