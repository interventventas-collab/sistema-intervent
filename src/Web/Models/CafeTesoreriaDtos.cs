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
