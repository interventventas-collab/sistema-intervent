namespace Web.Models;

// ========== Cajas ==========
public class CafeCajaDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "EFECTIVO";
    public decimal SaldoInicial { get; set; }
    public int Orden { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notas { get; set; }
    public decimal SaldoActual { get; set; }
}

// ========== Cheques ==========
public class CafeChequeDto
{
    public int Id { get; set; }
    public string Numero { get; set; } = "";
    public string Banco { get; set; } = "";
    public string? Emisor { get; set; }
    public int? ClienteOrigenId { get; set; }
    public string? ClienteOrigenNombre { get; set; }
    public decimal Importe { get; set; }
    public DateTime? FechaCobro { get; set; }
    public DateTime? FechaVencimiento { get; set; }
    public string Estado { get; set; } = "EN_CARTERA";
    public DateTime? FechaCambioEstado { get; set; }
    public string? Observaciones { get; set; }
    public int? CobranzaOrigenId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateChequeRequest
{
    public string Numero { get; set; } = "";
    /// <summary>FK al catalogo Cafe_Bancos (forma nueva). Si va vacio se usa Banco como fallback.</summary>
    public int? BancoId { get; set; }
    public string? Banco { get; set; }
    public string? Emisor { get; set; }
    public decimal Importe { get; set; }
    public DateTime? FechaCobro { get; set; }
    public DateTime? FechaVencimiento { get; set; }
    public int? ClienteOrigenId { get; set; }
    public string? Observaciones { get; set; }
}

// ========== Bancos (catalogo maestro) ==========
public class CafeBancoDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Alias { get; set; }
    public string? Cuit { get; set; }
    public bool IsActive { get; set; }
    public int SortOrder { get; set; }
    public int UsoEnCheques { get; set; }
    public int UsoEnEcheqs { get; set; }
    /// <summary>Lo que se muestra en dropdowns y tablas: Alias ?? Nombre.</summary>
    public string Display => string.IsNullOrWhiteSpace(Alias) ? Nombre : Alias!;
}

public class CreateBancoRequest
{
    public string Nombre { get; set; } = "";
    public string? Alias { get; set; }
    public string? Cuit { get; set; }
    public int? SortOrder { get; set; }
}

public class UpdateBancoRequest
{
    public string? Nombre { get; set; }
    public string? Alias { get; set; }
    public string? Cuit { get; set; }
    public bool? IsActive { get; set; }
    public int? SortOrder { get; set; }
}

// ========== Sincronizacion MeLi (publicaciones extendidas) ==========
public class SyncConfigDto
{
    public bool SyncStock { get; set; } = true;
    public bool SyncPrecio { get; set; }
    public decimal AjustePct { get; set; }
    public decimal AjusteFijo { get; set; }
    public DateTime? LastSyncAt { get; set; }
}

public class PublicacionExtendidaDto
{
    public string MeliItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Sku { get; set; } = "";
    public string? Thumbnail { get; set; }
    public string Status { get; set; } = "";
    public string? LogisticType { get; set; }
    public string CategoryId { get; set; } = "";
    public string ListingTypeId { get; set; } = "";
    public int CafeProductoId { get; set; }
    public string CafeProductoNombre { get; set; } = "";
    public string? CafeProductoMarca { get; set; }
    public decimal Costo { get; set; }
    public decimal IvaPct { get; set; }
    public decimal? PrecioBar { get; set; }
    public decimal? PrecioOtro { get; set; }
    public decimal? PrecioBarConIva { get; set; }
    public decimal? PrecioOtroConIva { get; set; }
    public decimal MargenSinComisDollar { get; set; }
    public decimal MargenSinComisPct { get; set; }
    public int StockSistema { get; set; }
    public int StockMeli { get; set; }
    public decimal PrecioMeli { get; set; }
    public decimal? PrecioMeliCalculado { get; set; }
    public decimal ComisionPct { get; set; }
    public decimal ComisionFija { get; set; }
    public decimal ComisionTotal { get; set; }
    public decimal NetoDeMeliConIva { get; set; }
    public decimal NetoDeMeliSinIva { get; set; }
    public decimal MargenRealConMeli { get; set; }
    public decimal MargenRealConMeliPct { get; set; }
    public decimal DiferenciaPrecio { get; set; }
    public decimal DiferenciaPrecioPct { get; set; }
    public SyncConfigDto Config { get; set; } = new();
}

public class UpdateSyncConfigRequest
{
    public bool SyncStock { get; set; } = true;
    public bool SyncPrecio { get; set; }
    public decimal AjustePct { get; set; }
    public decimal AjusteFijo { get; set; }
}

public class UpdatePrecioResultDto
{
    public decimal NuevoPrecio { get; set; }
    public decimal ComisionTotal { get; set; }
    public decimal NetoConIva { get; set; }
    public decimal NetoSinIva { get; set; }
}

// ========== Cobranzas ==========
public class ComprobantePendienteDto
{
    public int VentaId { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public decimal Total { get; set; }
    public decimal Pagado { get; set; }
    public decimal Saldo { get; set; }
    /// <summary>Solo se rellena cuando se agruparon varias sucursales por CUIT — sirve
    /// para mostrar de que sucursal viene cada comprobante.</summary>
    public int? ClienteId { get; set; }
    public string? ClienteNombre { get; set; }
}

public class SucursalMismoCuitDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Cuit { get; set; }
}

// ========== Cheques Banco (importacion desde Excel del banco) ==========
public class ChequeBancoDto
{
    public int Id { get; set; }
    public string IdBanco { get; set; } = "";
    public string Tipo { get; set; } = "";           // RECIBIDO | EMITIDO | ENDOSADO
    public string Numero { get; set; } = "";
    public string? Cmc7 { get; set; }
    public string? Clausula { get; set; }
    public string? BancoEmisor { get; set; }
    public DateTime? FechaEmision { get; set; }
    public DateTime? FechaPago { get; set; }
    public decimal Importe { get; set; }
    public string Estado { get; set; } = "";
    public string? Motivo { get; set; }
    public string? CuentaLibradora { get; set; }
    public string? CbuDeposito { get; set; }
    public string? LibradorNombre { get; set; }
    public string? LibradorCuit { get; set; }
    public string? BeneficiarioActualNombre { get; set; }
    public string? BeneficiarioActualCuit { get; set; }
    public string? ContraparteNombre { get; set; }
    public string? ContraparteCuit { get; set; }
    public int CantidadEndosos { get; set; }
    public int CantidadCesiones { get; set; }
    public int CantidadAvales { get; set; }
    // Linkeo a cobranza (cuando el e-cheq se asocio)
    public int? CafeChequeId { get; set; }
    public int? CobranzaId { get; set; }
    public string? CobranzaNumero { get; set; }
}

public class SugerenciaClienteEcheqDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? RazonSocial { get; set; }
    public string? Cuit { get; set; }
    public int? CodigoInterno { get; set; }
}

public class AsociarECheqRequest
{
    public int ClienteId { get; set; }
    public decimal Retenciones { get; set; }
    public string? Observaciones { get; set; }
    public List<ImputarComprobanteItem> Comprobantes { get; set; } = new();
}

public class ImputarComprobanteItem
{
    public int? VentaId { get; set; }
    public decimal Importe { get; set; }
}

public class ChequesResumenDto
{
    public int Cantidad { get; set; }
    public decimal Importe { get; set; }
}

public class ChequesBancoStatsDto
{
    public ChequesResumenDto EmitidosPorPagar { get; set; } = new();
    public ChequesResumenDto RecibidosDisponibles { get; set; } = new();
    public ChequesResumenDto EmitidosPagados { get; set; } = new();
    public ChequesResumenDto RecibidosUsados { get; set; } = new();
}

public class ImportChequeBancoResultDto
{
    public string Archivo { get; set; } = "";
    public string TipoDetectado { get; set; } = "";
    public int Nuevos { get; set; }
    public int Actualizados { get; set; }
    public int SinCambios { get; set; }
    public List<string> Errores { get; set; } = new();
}

// ========== Calendario Notas ==========
public class CalendarioNotaDto
{
    public int Id { get; set; }
    public DateTime Fecha { get; set; }
    public string Titulo { get; set; } = "";
    public string? Descripcion { get; set; }
    public decimal? Importe { get; set; }
    public string? Color { get; set; }
    public string? CreadoPor { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CrearCalendarioNotaRequest
{
    public DateTime Fecha { get; set; }
    public string Titulo { get; set; } = "";
    public string? Descripcion { get; set; }
    public decimal? Importe { get; set; }
    public string? Color { get; set; }
    public string? CreadoPor { get; set; }
}

// ========== Extracto Bancario ==========
public class ExtractoMovimientoDto
{
    public int Id { get; set; }
    public DateTime Fecha { get; set; }
    public string? Descripcion { get; set; }
    public decimal Debitos { get; set; }
    public decimal Creditos { get; set; }
    public decimal Saldo { get; set; }
    public string? Concepto { get; set; }
    public string? ObservacionesCliente { get; set; }
    public string? LeyendaAdicional1 { get; set; }  // razon social
    public string? LeyendaAdicional2 { get; set; }  // CUIT
    public string? TipoMovimiento { get; set; }
    public int? VentaIdAsociada { get; set; }
    public string? VentaNumeroAsociada { get; set; }
    public string? AsociadoPor { get; set; }
    public DateTime? AsociadoAt { get; set; }
    // Sugerencia
    public int? ClienteSugeridoId { get; set; }
    public string? ClienteSugeridoNombre { get; set; }
}

public class SaldoBancoDto
{
    public decimal Saldo { get; set; }
    public DateTime UltimaFecha { get; set; }
    public int CantidadMovimientos { get; set; }
    public DateTime? UltimoImportadoAt { get; set; }
}

public class ImportExtractoResultDto
{
    public string Archivo { get; set; } = "";
    public int Nuevos { get; set; }
    public int SinCambios { get; set; }
    public List<string> Errores { get; set; } = new();
}

public class AsociarMovimientoRequest
{
    public int VentaId { get; set; }
    public string? Operador { get; set; }
}

public class MovimientoDisponibleDto
{
    public int Id { get; set; }
    public DateTime Fecha { get; set; }
    public string? Descripcion { get; set; }
    public decimal Importe { get; set; }
    public string? Concepto { get; set; }
    public int? VentaIdAsociada { get; set; }
    public string? VentaNumeroAsociada { get; set; }
}

public class MarcarMovimientosUsadosRequest
{
    public List<int> MovimientoIds { get; set; } = new();
    public int CobranzaId { get; set; }
}

// ========== Repartidores + Cobranzas pendientes (QR) ==========
public class RepartidorDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? DniUltimos3 { get; set; }
    public bool IsActive { get; set; } = true;
}

public class CrearRepartidorRequest
{
    public string Nombre { get; set; } = "";
    public string? DniUltimos3 { get; set; }
}

public class EditarRepartidorRequest
{
    public string Nombre { get; set; } = "";
    public string? DniUltimos3 { get; set; }
    public bool IsActive { get; set; } = true;
}

public class CobranzaPendienteDto
{
    public int Id { get; set; }
    public int VentaId { get; set; }
    public string VentaNumero { get; set; } = "";
    public int? ClienteId { get; set; }
    public string? ClienteNombre { get; set; }
    public decimal VentaTotal { get; set; }
    public int RepartidorId { get; set; }
    public string RepartidorNombre { get; set; } = "";
    public decimal Importe { get; set; }
    public bool MarcadoEntregado { get; set; }
    public string? Notas { get; set; }
    public string Estado { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class AprobarCobranzaPendienteRequest
{
    public string? Operador { get; set; }
    public int? CajaId { get; set; }
}

public class RechazarCobranzaPendienteRequest
{
    public string? Motivo { get; set; }
    public string? Operador { get; set; }
}

public class ArqueoItemDto
{
    public int VentaId { get; set; }
    public string VentaNumero { get; set; } = "";
    public string? ClienteNombre { get; set; }
    public decimal Importe { get; set; }
    public bool MarcadoEntregado { get; set; }
    public string Estado { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public class ArqueoDto
{
    public int RepartidorId { get; set; }
    public string RepartidorNombre { get; set; } = "";
    public DateTime Fecha { get; set; }
    public decimal TotalPendiente { get; set; }
    public decimal TotalAprobado { get; set; }
    public int CantPendiente { get; set; }
    public int CantAprobado { get; set; }
    public List<ArqueoItemDto> Items { get; set; } = new();
}

// ========== Repartidor publico (mobile) ==========
public class RepartidorPublicItemDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
}

public class InfoVentaPublicDto
{
    public int VentaId { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string? ClienteNombre { get; set; }
    public string? ClienteDireccion { get; set; }
    public string? ClienteLocalidad { get; set; }
    public string? ClienteCiudad { get; set; }
    public decimal TotalCobrable { get; set; }
    public decimal SaldoPendiente { get; set; }
    public bool YaEntregada { get; set; }
    public string? EntregadoPor { get; set; }
    public List<ItemSimplePublicDto> Items { get; set; } = new();
}

public class ItemSimplePublicDto
{
    public int Cantidad { get; set; }
    public string Nombre { get; set; } = "";
    public string Formato { get; set; } = "";
    public string? Molienda { get; set; }
    public bool EsDoyPack { get; set; }
    public bool EsEnvasePlateado { get; set; }
}

public class CobranzaListDto
{
    public int Id { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public int ClienteId { get; set; }
    public string ClienteNombre { get; set; } = "";
    public decimal Total { get; set; }
    public decimal Retenciones { get; set; }
    public string Estado { get; set; } = "";
}

public class CobranzaDetalleDto
{
    public int Id { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public int ClienteId { get; set; }
    public string ClienteNombre { get; set; } = "";
    public decimal Total { get; set; }
    public decimal Retenciones { get; set; }
    public string Estado { get; set; } = "";
    public string? Operador { get; set; }
    public string? Observaciones { get; set; }
    public List<CobranzaComprobanteDto> Comprobantes { get; set; } = new();
    public List<CobranzaMedioDto> Medios { get; set; } = new();
}

public class CobranzaComprobanteDto
{
    public int Id { get; set; }
    public int? VentaId { get; set; }
    public string? VentaNumero { get; set; }
    public decimal Importe { get; set; }
}

public class CobranzaMedioDto
{
    public int Id { get; set; }
    public int CajaId { get; set; }
    public string CajaNombre { get; set; } = "";
    public decimal Importe { get; set; }
    public string? Referencia { get; set; }
    public int? ChequeId { get; set; }
}

// ========== Depositos + Stock por deposito ==========
public class CafeDepositoDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Direccion { get; set; }
    public string? Notas { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; } = true;
    public int Orden { get; set; }
    public int CantidadProductos { get; set; }
    public decimal StockGramosTotal { get; set; }
    public int StockUnidadesTotal { get; set; }
}

public class StockProductoDto
{
    public int ProductoId { get; set; }
    public string Codigo { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Categoria { get; set; } = "";
    public decimal StockGramos { get; set; }
    public int StockUnidades { get; set; }
}

// ========== Saldos por venta ==========
public class VentaSaldoDto
{
    public int VentaId { get; set; }
    public decimal Total { get; set; }
    public decimal Pagado { get; set; }
    public decimal Saldo { get; set; }
}

// ========== Pagos a proveedores ==========
public class CompraPendienteDto
{
    public int CompraId { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public decimal Total { get; set; }
    public decimal Pagado { get; set; }
    public decimal Saldo { get; set; }
    public string? NumeroComprobanteProveedor { get; set; }
}

public class PagoListDto
{
    public int Id { get; set; }
    public string Numero { get; set; } = "";
    public DateTime Fecha { get; set; }
    public int ProveedorId { get; set; }
    public string ProveedorNombre { get; set; } = "";
    public decimal Total { get; set; }
    public decimal Retenciones { get; set; }
    public string Estado { get; set; } = "";
}

public class EstadoCuentaProvDto
{
    public int ProveedorId { get; set; }
    public string Nombre { get; set; } = "";
    public decimal Saldo { get; set; }
    public List<MovimientoProvDto> Movimientos { get; set; } = new();
}

public class MovimientoProvDto
{
    public DateTime Fecha { get; set; }
    public string Tipo { get; set; } = "";
    public string Numero { get; set; } = "";
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
    public decimal Saldo { get; set; }
    public string? Detalle { get; set; }
}

// ========== Estado de cuenta ==========
public class MovimientoCuentaDto
{
    public DateTime Fecha { get; set; }
    public string Tipo { get; set; } = "";
    public string Numero { get; set; } = "";
    public decimal Debe { get; set; }
    public decimal Haber { get; set; }
    public decimal SaldoAcumulado { get; set; }
    public string? Detalle { get; set; }
}

public class EstadoCuentaDto
{
    public int ClienteId { get; set; }
    public string ClienteNombre { get; set; } = "";
    public decimal Saldo { get; set; }
    public List<MovimientoCuentaDto> Movimientos { get; set; } = new();
}
