namespace Web.Models;

// 2026-11-03: Tareas del depósito (/deposito/tareas en la compu + "Tareas pendientes" en el celu).
// Ver Api/Controllers/DepositoTareasController.cs.

public record DepTareaEmpleadoDto(int Id, string Nombre, string NombreCompleto, string? LinkRepartidor, string? LinkTareas);

public record DepTareaCandidatoDto(int Id, string? Sku, string Nombre, int Stock, string? Lugar, string? Barcode, string Motivo);

public record DepTareaDto(int Id, string Tipo, string Titulo, string? Comentario, int? EmpleadoId, string Empleado,
    string Para, DateTime CreatedAt, DateTime? TerminadaAt, string? TerminadaPor,
    int Total, int Hechos, int NoEncontrados, int Diferencias, int SinRevisar, int MuchaDiferencia = 0);

public record DepTareaItemDto(int Id, int ProductoId, string? Sku, string Nombre, int StockAhora, string? Lugar, string? Barcode,
    DateTime? HechoAt, string? HechoPor, int? Contado, int? StockAlContar, bool NoEncontrado, string? Valor,
    string? Revision, string? RevisionPor);

public record DepTareaDetalleDto(int Id, string Tipo, string Titulo, string? Comentario, List<DepTareaItemDto> Items);

public record DepTareaCrearResultDto(bool Ok, int Id, int Items);
public record DepTareaAceptarResultDto(bool Ok, int StockAntes, int StockDespues);
public record DepTareaLinkDto(string Token);

public record DepTareasResumenItemDto(string Titulo, string? Empleado, string Para);
public record DepTareasResumenDto(int Pendientes, List<DepTareasResumenItemDto> Lista);

public record DepTareaNombrePendienteDto(int ItemId, int ProductoId, string? Sku, string NombreActual, string NombrePropuesto,
    string? PropuestoPor, DateTime? PropuestoAt);
