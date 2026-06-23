namespace Web.Models;

// DTOs del modulo Pagos Movil — espejo de los DTOs del backend (PagosMovilController.cs)
// Pantalla móvil para precargar pagos + bandeja PC para confirmar.

public record EmpleadoActivoDto(int Id, string Nombre, string? Puesto);
public record ProveedorConDeudaDto(int Id, string Nombre, decimal Deuda, int CantidadFacturas);
public record CompraPendientePagoDto(int Id, string Numero, DateTime Fecha, decimal Total, decimal Pagado, decimal Saldo, string? NumeroComprobante);

public record PrecargarEmpleadoRequest(int EmpleadoId, string Concepto, decimal Monto, string MedioPago, string? Notas);
public record PrecargarFacturaRequest(int ProveedorId, List<PrecargarFacturaItem> Comprobantes, string MedioPago, string? Notas);
public record PrecargarFacturaItem(int CompraId, decimal Importe);

public record PendienteListDto(
    int Id, string Tipo,
    int? EmpleadoId, string? EmpleadoNombre,
    int? ProveedorId, string? ProveedorNombre,
    string Concepto, decimal Monto, string MedioPago, string? Notas,
    DateTime CreatedAt, string CreadoPor,
    int CantidadComprobantes);

public record PendienteDetalleDto(
    int Id, string Tipo,
    int? EmpleadoId, string? EmpleadoNombre,
    int? ProveedorId, string? ProveedorNombre,
    string Concepto, decimal Monto, string MedioPago, string? Notas,
    DateTime CreatedAt, string CreadoPor,
    List<PendienteComprobanteDetalleDto> Comprobantes);
public record PendienteComprobanteDetalleDto(int CompraId, string? CompraNumero, decimal Importe);

public record ConfirmarPagoMovilRequest(int? CajaId, DateTime? FechaPago);
public record RechazarPagoMovilRequest(string? Motivo);
public record EditarPagoMovilRequest(string? Concepto, decimal? Monto, string? MedioPago, string? Notas, List<PrecargarFacturaItem>? Comprobantes);

// DTO del area personal del empleado (pestaña "Mis cobros")
public record MisCobrosItemDto(int Id, DateTime FechaPago, string Concepto, string? Detalle, decimal Monto, string Metodo);
public record MisCobrosDto(bool Vinculado, string? NomEmpleadoNombre, decimal TotalMes, decimal TotalAnio, List<MisCobrosItemDto> Items);
