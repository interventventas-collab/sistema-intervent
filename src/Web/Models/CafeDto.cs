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
    public string? Telefono2 { get; set; }
    public string? Email { get; set; }
    public string? Direccion { get; set; }
    public string? EntreCalles { get; set; }
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
    /// <summary>Si true, en /cafe/preparacion las cards de este cliente muestran botón mini impresora.</summary>
    public bool TieneMiniImpresora { get; set; }
    /// <summary>2026-06-22: si true, todas las ventas nuevas piden firma al entregar.</summary>
    public bool SolicitarFirmaEntrega { get; set; }
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
    public string? Telefono2 { get; set; }
    public string? Email { get; set; }
    public string? Direccion { get; set; }
    public string? EntreCalles { get; set; }
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
    public string? Telefono2 { get; set; }
    public string? Email { get; set; }
    public string? Direccion { get; set; }
    public string? EntreCalles { get; set; }
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
    /// <summary>2026-06-22: si true, todas las ventas nuevas piden firma al entregar.</summary>
    public bool? SolicitarFirmaEntrega { get; set; }
}

public class CafeProductoDto
{
    public int Id { get; set; }
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public string Nombre { get; set; } = "";
    /// <summary>2026-06-18: si true, este "producto" en realidad es un combo con EsCompuesto=1
    /// (un tacho armado, set, etc.). El listado de /cafe/productos los muestra mezclados con un chip
    /// para distinguir. Al editar/duplicar, se redirige a /cafe/combos. Default false.</summary>
    public bool EsCompuestoFake { get; set; } = false;
    /// <summary>2026-06-18: solo si EsCompuestoFake=true — precio de referencia del combo (PVP del armado).</summary>
    public decimal? PrecioReferenciaCompuesto { get; set; }
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
    /// <summary>2026-06-01 — Stock armable (cuantos productos "shell" se pueden armar desde
    /// los componentes linkeados via MeLi). Null si no aplica (productos físicos normales).</summary>
    public int? StockArmable { get; set; }
    /// <summary>2026-06-02 — Desglose por depósito. StockPropio = lo que hay en '9 de Abril'.
    /// StockFull = lo que hay en 'Full MeLi'. Si son null, ese depósito no aplica.</summary>
    public int? StockPropio { get; set; }
    public int? StockFull { get; set; }
    public string? Notas { get; set; }
    public bool IsActive { get; set; }
    public decimal IvaPct { get; set; } = 21m;
    /// <summary>Modelo NUEVO de precios (solo OTROS). null = usa modelo legacy.</summary>
    public decimal? PrecioOtro { get; set; }
    public decimal? PrecioBar { get; set; }
    /// <summary>2026-06-10: si true, todos los clientes (BAR y OTRO) pagan PrecioOtro.</summary>
    public bool SinPrecioBar { get; set; } = false;
    /// <summary>Precio del bulto completo (descuento por volumen, SOLO OTROS).</summary>
    public decimal? PrecioBulto { get; set; }
    public decimal? PrecioBultoOtro { get; set; }
    /// <summary>2026-07-07: formato por defecto al vender (null/"UNIT" = Suelto).</summary>
    public string? FormatoPorDefecto { get; set; }
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
    /// <summary>Multiplicador del OEM (default 1). Precio efectivo = OemPvpConIva × MultiplicadorOem.</summary>
    public decimal? MultiplicadorOem { get; set; }

    // 2026-06-10 — Precio efectivo (lo que el sistema realmente cobra).
    // Si hay OEM → OEM.PvpConIva × MultiplicadorOem.
    // Si no hay OEM → PrecioOtro × (1 + IvaPct/100).
    // Esta es la MISMA lógica que usa CafePricingService.PrecioCIvaAsync y MeliPricePushService,
    // así que el catálogo, Nueva Venta, listas de precios y MeLi muestran SIEMPRE el mismo número.
    public decimal? PrecioEfectivoConIva
    {
        get
        {
            // Prioridad 1: OEM
            if (OemPvpConIva.HasValue && OemPvpConIva.Value > 0m)
            {
                var mult = MultiplicadorOem ?? 1m;
                if (mult <= 0m) mult = 1m;
                return Math.Round(OemPvpConIva.Value * mult, 2);
            }
            // Prioridad 2: PrecioOtro (manual)
            if (PrecioOtro.HasValue && PrecioOtro.Value > 0m)
                return Math.Round(PrecioOtro.Value * (1 + IvaPct / 100m), 2);
            return null;
        }
    }
    /// <summary>"OEM", "MANUAL" o null. Identifica de dónde sale PrecioEfectivoConIva.</summary>
    public string? PrecioEfectivoFuente
    {
        get
        {
            if (OemPvpConIva.HasValue && OemPvpConIva.Value > 0m) return "OEM";
            if (PrecioOtro.HasValue && PrecioOtro.Value > 0m) return "MANUAL";
            return null;
        }
    }

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
    /// <summary>2026-06-10: todos los clientes pagan PrecioOtro (sin BAR diferenciado).</summary>
    public bool SinPrecioBar { get; set; } = false;
    // Precio del bulto completo (descuento por volumen, SOLO OTROS):
    public decimal? PrecioBulto { get; set; }
    public decimal? PrecioBultoOtro { get; set; }
    /// <summary>2026-07-07: formato por defecto al vender (null/"UNIT" = Suelto). Solo OTROS.</summary>
    public string? FormatoPorDefecto { get; set; }
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
    /// <summary>2026-06-10: si null no cambia, si true marca "todos pagan PrecioOtro".</summary>
    public bool? SinPrecioBar { get; set; }
    // Precio del bulto completo (descuento por volumen, SOLO OTROS):
    public decimal? PrecioBulto { get; set; }
    public decimal? PrecioBultoOtro { get; set; }
    public bool ClearPrecioBulto { get; set; }
    public bool ClearPrecioBultoOtro { get; set; }
    /// <summary>2026-07-07: formato por defecto al vender. ClearFormatoPorDefecto=true → Suelto.</summary>
    public string? FormatoPorDefecto { get; set; }
    public bool ClearFormatoPorDefecto { get; set; }

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
    public string? NegocioTelefono2 { get; set; }
    public string? NegocioWeb2 { get; set; }
    public decimal? CostoFraccionamientoFuturo { get; set; }
    public DateTime? FechaAplicaFraccionamientoFuturo { get; set; }
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
    public string? NegocioTelefono2 { get; set; }
    public string? NegocioWeb2 { get; set; }
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
    /// <summary>2026-06-08: si el item viene de un combo agregado a la venta, marca el id del combo.</summary>
    public int? ComboOrigenId { get; set; }
    /// <summary>Nombre del combo origen (resuelto desde Cafe_Combos al armar la venta).</summary>
    public string? ComboOrigenNombre { get; set; }
    /// <summary>SKU del combo origen (puede ser null si el combo no tiene SKU).</summary>
    public string? ComboOrigenSku { get; set; }
    /// <summary>2026-07-04: si el ítem es un Servicio del catálogo, su Id. Necesario para que al
    /// ver/editar la venta el servicio se muestre bien (nombre + precio) y no como "Producto no encontrado".</summary>
    public int? ServicioId { get; set; }
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
    // 2026-06-08: codigo interno del cliente para mostrar "(#123)" al lado del nombre
    public int? ClienteCodigoInterno { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Descuento { get; set; }
    public decimal Total { get; set; }
    public decimal CostoTotal { get; set; }
    public decimal Margen { get; set; }
    public string? Observaciones { get; set; }
    public string Estado { get; set; } = "emitido";
    public string? WeekDays { get; set; }
    public bool EnRadar { get; set; }
    public bool Retira { get; set; }
    /// <summary>2026-06-05: TRANSPORTE — la venta se despacha por empresa de transporte.</summary>
    public bool PorTransporte { get; set; }
    public string? TransporteEmpresa { get; set; }
    public string? TransporteDestino { get; set; }
    /// <summary>2026-06-05: Operador que cargo la venta (OSMAR/GERMAN/GABRIEL/etc). Null para
    /// ventas previas al feature.</summary>
    public string? CreadoPorOperador { get; set; }
    public bool IsPaid { get; set; }
    public string TipoComprobante { get; set; } = "X";
    public string CondicionIva { get; set; } = "CF";
    public string CondicionPago { get; set; } = "EFECTIVO";
    /// <summary>2026-07-14: solo presupuestos (PRO). true = PDF con desglose IVA; false = total sin IVA.</summary>
    public bool MostrarIvaProforma { get; set; } = true;
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
    /// <summary>2026-07-03: certificado/CUIT con el que se emitió (multi-sociedad). Null = default.</summary>
    public int? ArcaWebserviceAccountId { get; set; }
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
    /// <summary>2026-07-03: monto cobrado en la calle por el repartidor cuando entregó
    /// (suma de las CafeCobranzasPendientes de esta venta no rechazadas). Null si no cobró.
    /// Se muestra en el chip verde de entrega para saber cuánta plata trae el repartidor.</summary>
    public decimal? CobradoEnEntrega { get; set; }
    /// <summary>ID del archivo en Google Drive cuando se subió el PDF del comprobante. Null si nunca se subió.</summary>
    public string? DriveFileId { get; set; }
    /// <summary>Cuándo se subió el PDF a Google Drive. Null si nunca se subió.</summary>
    public DateTime? DriveSubidoAt { get; set; }
    /// <summary>Cuántas veces se subió. 0 = nunca, 1 = subida normal, 2+ = re-subido (UI marca diferente).</summary>
    public int DriveSubidasCount { get; set; }
    /// <summary>2026-06-02: Comentario INTERNO para armado (post-it amarillo en /cafe/preparacion).
    /// Independiente de Observaciones. NO sale en el PDF al cliente.</summary>
    public string? ComentarioArmado { get; set; }
    /// <summary>2026-06-05: nombre del repartidor que escaneó el QR de esta venta y la tiene
    /// cargada en su lista "Mis Pedidos". Null si nadie la escaneó todavía. Se muestra como chip
    /// "🚚 Lo tiene X" en el listado de ventas.</summary>
    public string? EscaneadoPorRepartidorNombre { get; set; }
    /// <summary>Cuándo fue escaneada. Para mostrar "hace X min" en el tooltip.</summary>
    public DateTime? EscaneadoAt { get; set; }
    /// <summary>2026-06-23: Concepto AFIP. 1=Productos (default), 2=Servicios, 3=Productos y Servicios.</summary>
    public int Concepto { get; set; } = 1;
    /// <summary>Solo aplica si Concepto in (2,3). Inicio del periodo de prestacion.</summary>
    public DateTime? ConceptoServDesde { get; set; }
    /// <summary>Solo aplica si Concepto in (2,3). Fin del periodo de prestacion.</summary>
    public DateTime? ConceptoServHasta { get; set; }
    /// <summary>2026-07-02: link de Google Maps propio de la venta (override del domicilio de entrega).</summary>
    public string? MapeoLink { get; set; }
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
    // 2026-05-30: info extra del comprobante para el armador del depósito.
    public string? ClienteTelefono { get; set; }
    public string? DomicilioEntrega { get; set; }
    public string? Observaciones { get; set; }
    public string? ComentariosCliente { get; set; }
    public string? WeekDays { get; set; }
    public string? EntregaPor { get; set; }
    public string EstadoPreparacion { get; set; } = "";
    public DateTime? PreparacionUpdatedAt { get; set; }
    public decimal Total { get; set; }
    /// <summary>Si está subido a Drive, ID del archivo. Para mostrar botón "Ver PDF" en la tarjeta.</summary>
    public string? DriveFileId { get; set; }
    /// <summary>Cuándo se subió a Drive. Null = nunca subido (mostrar botón "Subir a Drive").</summary>
    public DateTime? DriveSubidoAt { get; set; }
    /// <summary>Si true, el cliente tiene flag "mini impresora" — la card muestra botón impresora.</summary>
    public bool TieneMiniImpresora { get; set; }
    /// <summary>Cuándo se imprimió por última vez desde el tablero. Para chip "Impreso hace X".</summary>
    public DateTime? ImpresaAt { get; set; }
    /// <summary>Cuántas veces se imprimió en total.</summary>
    public int ImpresaCount { get; set; }
    /// <summary>Para sección "Ya armados": cantidad de items (sin traer el listado completo).</summary>
    public int? ItemsCount { get; set; }
    /// <summary>2026-06-02: Comentario INTERNO para armado (post-it amarillo). Cargado desde Nueva Venta.
    /// Si está vacío, no se muestra el chip. NO sale en el PDF al cliente.</summary>
    public string? ComentarioArmado { get; set; }
    /// <summary>2026-06-03: si true, el pedido se editó después de armado y se re-subió.
    /// La card muestra chip naranja "⚠ PEDIDO MODIFICADO" para avisar al armador.</summary>
    public bool ModificadoDespuesDeArmar { get; set; }
    /// <summary>2026-06-05: true cuando la venta no se pudo subir a Drive. La card muestra chip rojo "SIN DRIVE".</summary>
    public bool SinDrive { get; set; }
    public List<CafePreparacionItemDto> Items { get; set; } = new();
}

public class CafePreparacionItemDto
{
    public int Id { get; set; }
    public string ProductoNombre { get; set; } = "";
    // 2026-05-30: SKU del producto (si está linkeado al catálogo) — pedido del depósito.
    public string? Sku { get; set; }
    // 2026-06-15: stock del sistema al lado del SKU — el armador ve cuánto debería haber físico.
    public int? StockUnidades { get; set; }
    public decimal? StockGramos { get; set; }
    public string Formato { get; set; } = "";
    public int Cantidad { get; set; }
    public string? Molienda { get; set; }
    public bool EsDoyPack { get; set; }
    public bool EsEnvasePlateado { get; set; }
    public string? Categoria { get; set; }
    public bool EsConceptoLibre { get; set; }
    /// <summary>2026-06-08: si proviene de un combo, marca el id origen. Permite agrupar visualmente
    /// en /cafe/preparacion para que el armador vea qué pieza es parte de qué combo.</summary>
    public int? ComboOrigenId { get; set; }
    public string? ComboOrigenNombre { get; set; }
    public string? ComboOrigenSku { get; set; }
    /// <summary>2026-06-17: unidades por bulto del producto. Cuando el item es Formato="BULTO",
    /// el armador necesita saber cuantas unidades vienen en cada bulto para contar bien al armar.</summary>
    public int? UxB { get; set; }
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
    /// <summary>2026-07-03: certificado/CUIT con el que se factura (multi-sociedad). Null = default.</summary>
    public int? ArcaWebserviceAccountId { get; set; }
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
    /// <summary>2026-06-05: Si > 0, el item es un Servicio del catalogo Cafe_Servicios (envio, mano de obra, etc).</summary>
    public int? ServicioId { get; set; }
    /// <summary>2026-06-08: Si el item proviene de un combo agregado a la venta, marca el id del combo origen.
    /// Sólo presentación: en el PDF/factura los items con mismo ComboOrigenId se agrupan en UNA línea con
    /// el nombre del combo (el cliente no ve el desglose).</summary>
    public int? ComboOrigenId { get; set; }
}

public class CafeCotizarRequest
{
    public int? ClienteId { get; set; }
    public string? ClienteTipo { get; set; }
    public List<CafeCotizarItemRequest> Items { get; set; } = new();
    public decimal Descuento { get; set; }
    // 2026-06-18: si se está EDITANDO una venta existente, pasamos su Id para que el
    // cotizador NO cuente contra sí mismo el stock que esa venta ya tiene reservado.
    // Si es null = venta nueva, comportamiento normal.
    public int? EditandoVentaId { get; set; }
    // 2026-07-14: tipo de comprobante (X/PRO/FA/FB/FC). El PRESUPUESTO (PRO) no descuenta
    // stock → la falta de stock solo avisa, no bloquea la emisión.
    public string? TipoComprobante { get; set; }
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
    public bool EnRadar { get; set; }
    public bool Retira { get; set; }
    /// <summary>2026-06-05: la venta se despacha por empresa de transporte. Excluyente con EnRadar/Retira.</summary>
    public bool PorTransporte { get; set; }
    public string? TransporteEmpresa { get; set; }
    public string? TransporteDestino { get; set; }
    public bool IsPaid { get; set; }
    public string? TipoComprobante { get; set; }
    public string? CondicionIva { get; set; }
    public string? CondicionPago { get; set; }
    /// <summary>2026-07-14: solo presupuestos (PRO). true (default) = PDF con IVA; false = total sin IVA.</summary>
    public bool MostrarIvaProforma { get; set; } = true;
    public string? EntregaPor { get; set; }
    public string? ComentarioArmado { get; set; }
    /// <summary>2026-06-23: Concepto AFIP. 1=Productos (default), 2=Servicios, 3=Productos y Servicios.</summary>
    public int Concepto { get; set; } = 1;
    public DateTime? ConceptoServDesde { get; set; }
    public DateTime? ConceptoServHasta { get; set; }
    /// <summary>2026-07-02: link de Google Maps del domicilio de entrega. Se guarda en la venta.</summary>
    public string? MapeoLink { get; set; }
    /// <summary>2026-07-02: si true, además guarda el link en la ficha del cliente (para futuras entregas).</summary>
    public bool GuardarMapeoEnCliente { get; set; }
    /// <summary>2026-07-03: certificado/CUIT con el que se factura (multi-sociedad). Null = default.</summary>
    public int? ArcaWebserviceAccountId { get; set; }
}

public class UpdateCafeVentaFlagsRequest
{
    public string? WeekDays { get; set; }
    public bool? EnRadar { get; set; }
    public bool? Retira { get; set; }
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
    /// <summary>2026-07-14: solo presupuestos (PRO). true = PDF con IVA; false = total sin IVA.</summary>
    public bool? MostrarIvaProforma { get; set; }
    public string? WeekDays { get; set; }
    public bool? EnRadar { get; set; }
    public bool? Retira { get; set; }
    /// <summary>2026-06-05: la venta se despacha por empresa de transporte. Excluyente con EnRadar/Retira.</summary>
    public bool? PorTransporte { get; set; }
    public string? TransporteEmpresa { get; set; }
    public string? TransporteDestino { get; set; }
    public bool? IsPaid { get; set; }
    public List<CafeCotizarItemRequest>? Items { get; set; }
    public decimal? Descuento { get; set; }
    public string? EntregaPor { get; set; }
    public string? ComentarioArmado { get; set; }
    /// <summary>2026-06-23: Concepto AFIP. 1=Productos, 2=Servicios, 3=Productos y Servicios.</summary>
    public int? Concepto { get; set; }
    public DateTime? ConceptoServDesde { get; set; }
    public DateTime? ConceptoServHasta { get; set; }
    /// <summary>2026-07-02: link de Google Maps del domicilio de entrega. Se guarda en la venta.</summary>
    public string? MapeoLink { get; set; }
    /// <summary>2026-07-02: si true, además guarda el link en la ficha del cliente (para futuras entregas).</summary>
    public bool GuardarMapeoEnCliente { get; set; }
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
    public string? Sku { get; set; }   // 2026-06-01: para que el buscador matchee por SKU
    public bool EsCompuesto { get; set; }  // 2026-06-01: si true, aparece en pestaña Producto del buscador
    // 2026-06-18: OEM en compuestos. Cuando el compuesto tiene OEM cargado, el sistema usa
    // PvpConIva del OEM × MultiplicadorOem como precio (ignorando suma de componentes).
    public int? OemId { get; set; }
    public string? OemCodigo { get; set; }
    public decimal? OemPvpConIva { get; set; }
    public decimal? OemIvaPct { get; set; }
    public decimal? MultiplicadorOem { get; set; }
    // 2026-06-18: costo y stock calculados del compuesto
    public decimal CostoSumaComponentes { get; set; }
    public int StockDisponible { get; set; }
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
    public int? OemId { get; set; }
    public decimal? MultiplicadorOem { get; set; }
    public bool? ClearOem { get; set; }
    public bool? EsCompuesto { get; set; }
}

// 2026-06-18: sugeridor masivo OEM
public class SugerenciaOemDto
{
    public int ComboId { get; set; }
    public string ComboSku { get; set; } = "";
    public string ComboNombre { get; set; } = "";
    public int OemId { get; set; }
    public string OemCodigo { get; set; } = "";
    public string? OemDescripcion { get; set; }
    public decimal? OemPvpConIva { get; set; }
}

public class SugerirOemsResponse
{
    public int Total { get; set; }
    public int ConMatch { get; set; }
    public int SinMatch { get; set; }
    public List<SugerenciaOemDto> Sugerencias { get; set; } = new();
}

public class AplicarSugerenciaItem
{
    public int ComboId { get; set; }
    public int OemId { get; set; }
    public decimal Multiplicador { get; set; } = 1m;
}

public class AplicarSugerenciasOemRequest
{
    public List<AplicarSugerenciaItem> Items { get; set; } = new();
}

public class AplicarSugerenciasOemResponse
{
    public int Aplicadas { get; set; }
    public int Fallidas { get; set; }
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
    /// <summary>2026-06-10: URL al producto en el sitio del proveedor.</summary>
    public string? UrlWeb { get; set; }
    /// <summary>2026-06-11: campos extraidos de la web del proveedor.</summary>
    public string? ImagenUrl { get; set; }
    public string? DescripcionWeb { get; set; }
    public string? EspecificacionesJson { get; set; }
    public DateTime? ScrapedAt { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? LastImportAt { get; set; }
    public int VariantesCount { get; set; }
}

// 2026-07-10: producto (variante) vinculado a un OEM, para ver el impacto de un cambio de precio.
public class CafeOemVarianteDto
{
    public int Id { get; set; }
    public string? Sku { get; set; }
    public string? Nombre { get; set; }
    public string? Categoria { get; set; }
    public string? Marca { get; set; }
    public decimal Costo { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? BarPctSobreCosto { get; set; }
    public int? UxB { get; set; }
    public decimal StockUnidades { get; set; }
    public bool IsActive { get; set; }
}

// 2026-06-11: status del job masivo de scraping
public class CafeOemScrapeMasivoStatusDto
{
    public bool running { get; set; }
    public int total { get; set; }
    public int procesados { get; set; }
    public int exitosos { get; set; }
    public int errores { get; set; }
    public string? currentCodigo { get; set; }
    public DateTime? startedAt { get; set; }
    public DateTime? finishedAt { get; set; }
    public string? lastError { get; set; }
    public decimal porcentaje { get; set; }
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

// ===== Buscar precio por codigo (buscador simple) =====
public class CafePrecioLineaDto
{
    public string Etiqueta { get; set; } = "";
    public decimal SinIva { get; set; }
    public decimal ConIva { get; set; }
    public string? Nota { get; set; }
}

public class CafePrecioConsultaDto
{
    public bool Encontrado { get; set; }
    public string? Mensaje { get; set; }
    public string Sku { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Categoria { get; set; } = "";
    public string? Marca { get; set; }
    public string Stock { get; set; } = "";
    public decimal CostoSinIva { get; set; }
    public decimal IvaPct { get; set; } = 21m;
    public bool TieneOem { get; set; }
    public string? OemCodigo { get; set; }
    public bool Activo { get; set; } = true;
    public List<CafePrecioLineaDto> Precios { get; set; } = new();
}

// ===== Alta de clientes por enlace público =====
public class CafeClienteAltaDto
{
    public int Id { get; set; }
    public string NombreFantasia { get; set; } = "";
    public string? RazonSocial { get; set; }
    public string? Cuit { get; set; }
    public string? CondicionIva { get; set; }
    public string? ContactoNombre { get; set; }
    public string Telefono { get; set; } = "";
    public string? Email { get; set; }
    public string? DireccionFiscal { get; set; }
    public string? Direccion { get; set; }
    public string? EntreCalles { get; set; }
    public string? Localidad { get; set; }
    public string? MapeoLink { get; set; }
    public string? Comentarios { get; set; }
    public string Estado { get; set; } = "pendiente";
    public DateTime CreatedAt { get; set; }
}

/// <summary>Lo que el cliente envía desde el formulario público.</summary>
public class AltaClientePublicaRequest
{
    public string? NombreFantasia { get; set; }
    public string? RazonSocial { get; set; }
    public string? Cuit { get; set; }
    public string? CondicionIva { get; set; }
    public string? ContactoNombre { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? DireccionFiscal { get; set; }
    public string? Direccion { get; set; }
    public string? EntreCalles { get; set; }
    public string? Localidad { get; set; }
    public string? MapeoLink { get; set; }
    public string? Comentarios { get; set; }
}

/// <summary>Lo que manda el operador al dar de alta (puede corregir datos).</summary>
public class AprobarAltaClienteRequest
{
    public string? NombreFantasia { get; set; }
    public string? RazonSocial { get; set; }
    public string? Cuit { get; set; }
    public string? CondicionIva { get; set; }
    public string? ContactoNombre { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? DireccionFiscal { get; set; }
    public string? Direccion { get; set; }
    public string? EntreCalles { get; set; }
    public string? Localidad { get; set; }
    public string? MapeoLink { get; set; }
    public string? Comentarios { get; set; }
    public string? Tipo { get; set; }
    public string? Operador { get; set; }
}

public class AltaLinkDto { public string Token { get; set; } = ""; public string Ruta { get; set; } = ""; }
public class AltaInitDto { public bool Ok { get; set; } public string? Mensaje { get; set; } public string? NegocioNombre { get; set; } }
public class AltaEnviarResultDto { public bool Ok { get; set; } public string? Mensaje { get; set; } }
public class AprobarAltaResultDto { public bool Ok { get; set; } public int ClienteId { get; set; } public string? Codigo { get; set; } }

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

// 2026-07-10: vista previa de importacion de OEMs (dry-run)
public class CafeOemImportCambioDto
{
    public string Codigo { get; set; } = "";
    public string? Descripcion { get; set; }
    public bool EsNuevo { get; set; }
    public decimal? CostoViejo { get; set; }
    public decimal? CostoNuevo { get; set; }
    public decimal? PvpViejo { get; set; }
    public decimal? PvpNuevo { get; set; }
    public bool CambiaCosto { get; set; }
    public bool CambiaPvp { get; set; }
}

public class CafeOemImportPreviewDto
{
    public int Creados { get; set; }
    public int Actualizados { get; set; }
    public int Omitidos { get; set; }
    public string? Proveedor { get; set; }
    public bool TieneColumnaCosto { get; set; }
    public bool TieneColumnaPvp { get; set; }
    public List<CafeOemImportCambioDto> Cambios { get; set; } = new();
    public List<string> Errores { get; set; } = new();
}

// 2026-07-15: gestión masiva de Stock mínimo (StockMinimoMeLi) por Excel
public class StockMinimoCambioDto
{
    public int ProductoId { get; set; }
    public string? Codigo { get; set; }
    public string Descripcion { get; set; } = "";
    public int? MinimoViejo { get; set; }
    public int? MinimoNuevo { get; set; }
    public bool Asigna { get; set; }
    public bool Quita { get; set; }
}

public class StockMinimoPreviewDto
{
    public int TotalFilas { get; set; }
    public int SinCambios { get; set; }
    public int Asignan { get; set; }
    public int Quitan { get; set; }
    public int NoEncontrados { get; set; }
    public List<StockMinimoCambioDto> Cambios { get; set; } = new();
    public List<string> Errores { get; set; } = new();
}

public class StockMinimoApplyResultDto
{
    public int Actualizados { get; set; }
    public int Quitados { get; set; }
    public int NoEncontrados { get; set; }
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
    public string? Modalidad { get; set; }
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

// 2026-06-06: ventas "ocasionales" (sin cliente del catálogo) con saldo pendiente.
public class VentaOcasionalSaldoDto
{
    public int VentaId { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string ClienteNombreSnapshot { get; set; } = "";
    public string? TipoComprobante { get; set; }
    public decimal Total { get; set; }
    public decimal Pagado { get; set; }
    public decimal Saldo { get; set; }
    public int DiasMora { get; set; }
}

// ─────────────────────────────────────────────────────────────────────
// Listas de precios personalizadas (Fase 1 - 2026-06-09)
// ─────────────────────────────────────────────────────────────────────
public class ListaCustomDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public int? ClienteId { get; set; }
    public string? ClienteNombre { get; set; }
    public string? ClienteCodigo { get; set; }
    public string? TipoCliente { get; set; }
    public string? Observaciones { get; set; }
    public string? NumeroLista { get; set; }
    public string? BackgroundUrl { get; set; }
    public string? BadgeColor { get; set; }
    public bool MostrarMarca { get; set; } = true;
    public int CantidadSecciones { get; set; }
    public int CantidadItems { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class CrearListaCustomRequest
{
    public string Nombre { get; set; } = "";
    public int? ClienteId { get; set; }
    public string? TipoCliente { get; set; }
    public string? Observaciones { get; set; }
    public string? NumeroLista { get; set; }
    public string? BadgeColor { get; set; }
    public bool MostrarMarca { get; set; } = true;
}

public class CrearListaCustomResponse
{
    public int Id { get; set; }
}

// Fase 2: contenido completo de la lista (secciones + items resueltos con datos)
public class ContenidoListaCustomDto
{
    public ListaCustomDto Lista { get; set; } = new();
    public List<SeccionCustomDto> Secciones { get; set; } = new();
}

public class SeccionCustomDto
{
    public int Id { get; set; }
    public string Titulo { get; set; } = "";
    public int Orden { get; set; }
    public List<ItemCustomDto> Items { get; set; } = new();
}

public class ItemCustomDto
{
    public int Id { get; set; }
    public string TipoItem { get; set; } = "";
    public int RefId { get; set; }
    public int Orden { get; set; }
    public string? Notas { get; set; }
    public bool EsNovedad { get; set; }
    public string? Nombre { get; set; }
    public string? Sku { get; set; }
    public string? Marca { get; set; }
    public decimal? Precio { get; set; }
    public string? Detalle { get; set; }
    public decimal? Precio1Kg { get; set; }
    public decimal? PrecioMedio { get; set; }
    public decimal? PrecioCuarto { get; set; }
}

public class ItemDisponibleDto
{
    public string Tipo { get; set; } = "";
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Sku { get; set; }
    public string? Marca { get; set; }
    public decimal? PrecioBar { get; set; }
    public decimal? PrecioOtro { get; set; }
    public string? Detalle { get; set; }
}

// 2026-07-14: borrador de venta compartido (servidor)
public class BorradorServerDto
{
    public int Id { get; set; }
    public string? ClienteNombre { get; set; }
    public int ItemsCount { get; set; }
    public decimal Total { get; set; }
    public string? CreadoPorOperador { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string PayloadJson { get; set; } = "";
}

public class SaveBorradorRequest
{
    public string PayloadJson { get; set; } = "";
    public string? ClienteNombre { get; set; }
    public int ItemsCount { get; set; }
    public decimal Total { get; set; }
}
