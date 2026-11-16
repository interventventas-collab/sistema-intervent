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

public record DepTareasResumenItemDto(string Titulo, string? Empleado, string Para,
    int Id = 0, string Tipo = "", string? Comentario = null, DateTime? CreatedAt = null, int Total = 0);
// 2026-11-16: Nuevas = las que nadie empezó (cartel "TAREA NUEVA" en la pantalla del depósito)
public record DepTareasResumenDto(int Pendientes, List<DepTareasResumenItemDto> Lista, List<DepTareasResumenItemDto>? Nuevas = null);

// 2026-11-16: alquileres día por día para el depósito (GET /api/deposito-tareas/alquileres)
public record AlqEquipoCantDto(string Nombre, int Cantidad);
public record AlqReservaDepoDto(int Id, string Numero, string Cliente, string? Direccion, DateTime Entrega, DateTime Retiro,
    string Estado, List<AlqEquipoCantDto> Equipos);
public record AlqMovDepoDto(string Tipo, List<AlqEquipoCantDto> Equipos, List<int> Reservas);
public record AlqDiaDepoDto(DateTime Fecha, List<AlqMovDepoDto> Movimientos);
public record AlqDepoResDto(List<AlqDiaDepoDto> Dias, List<AlqReservaDepoDto> Reservas, int HoyYManiana);

public record DepTareaNombrePendienteDto(int ItemId, int ProductoId, string? Sku, string NombreActual, string NombrePropuesto,
    string? PropuestoPor, DateTime? PropuestoAt);

// 2026-11-03: avisos del botón flotante 📋 (GET /api/deposito-tareas/avisos)
public record DepAvisoMuchaDto(int ItemId, int TareaId, string? Sku, string Nombre, int? Decia, int Ahora, string? Quien, DateTime? At);
public record DepAvisoNoEncontradoDto(int ItemId, int TareaId, string? Sku, string Nombre, string? Quien, DateTime? At);
public record DepAvisoTareaDto(int Id, string Tipo, string Titulo, string Para, string Empleado, int Total, int Hechos, DateTime? TerminadaAt, string? TerminadaPor);
public record DepAvisoFotoDto(int ItemId, string? Sku, string Nombre, string? Motivo, string? FotoActual, string? FotoNueva, string? Quien, DateTime? At);
public record DepAvisosDto(int Pendientes, List<DepTareaNombrePendienteDto> Nombres, List<DepAvisoMuchaDto> Mucha,
    List<DepAvisoNoEncontradoDto> NoEncontrados, List<DepAvisoTareaDto> Terminadas, List<DepAvisoTareaDto> EnCurso,
    List<DepAvisoFotoDto>? Fotos = null);

// 2026-11-16: alquileres en "Pedidos para armar" (GET /api/deposito-tareas/alquileres-tablero)
public record AlqTableroItemDto(int TareaId, string? Comentario, string? Empleado, DateTime CreatedAt,
    DateTime? TerminadaAt, string? TerminadaPor, string? Telefono, string? Hora, AlqReservaDepoDto Reserva);
