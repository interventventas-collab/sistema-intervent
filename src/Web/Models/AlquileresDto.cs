namespace Web.Models;

public class AlqEquipoDto
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Nombre { get; set; } = string.Empty;
    public string? Categoria { get; set; }
    public string? Descripcion { get; set; }
    public int StockTotal { get; set; }
    public decimal PrecioDiario { get; set; }
    public decimal? PrecioReposicion { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateAlqEquipoRequest
{
    public string? Sku { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string? Categoria { get; set; }
    public string? Descripcion { get; set; }
    public int StockTotal { get; set; }
    public decimal PrecioDiario { get; set; }
    public decimal? PrecioReposicion { get; set; }
}

public class UpdateAlqEquipoRequest
{
    public string? Sku { get; set; }
    public string? Nombre { get; set; }
    public string? Categoria { get; set; }
    public string? Descripcion { get; set; }
    public int? StockTotal { get; set; }
    public decimal? PrecioDiario { get; set; }
    public decimal? PrecioReposicion { get; set; }
    public bool? IsActive { get; set; }
}

public class AlqClienteDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string? Empresa { get; set; }
    public string? DniCuit { get; set; }
    public string? Telefono { get; set; }
    public string? Telefono2 { get; set; }
    public string? Email { get; set; }
    public string? DireccionDefault { get; set; }
    public string? Piso { get; set; }
    public string? Depto { get; set; }
    public string? Barrio { get; set; }
    public string? EntreCalles { get; set; }
    public string? Notas { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateAlqClienteRequest
{
    public string Nombre { get; set; } = string.Empty;
    public string? Empresa { get; set; }
    public string? DniCuit { get; set; }
    public string? Telefono { get; set; }
    public string? Telefono2 { get; set; }
    public string? Email { get; set; }
    public string? DireccionDefault { get; set; }
    public string? Piso { get; set; }
    public string? Depto { get; set; }
    public string? Barrio { get; set; }
    public string? EntreCalles { get; set; }
    public string? Notas { get; set; }
}

public class UpdateAlqClienteRequest
{
    public string? Nombre { get; set; }
    public string? Empresa { get; set; }
    public string? DniCuit { get; set; }
    public string? Telefono { get; set; }
    public string? Telefono2 { get; set; }
    public string? Email { get; set; }
    public string? DireccionDefault { get; set; }
    public string? Piso { get; set; }
    public string? Depto { get; set; }
    public string? Barrio { get; set; }
    public string? EntreCalles { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Reservas =====
public class AlqReservaItemDto
{
    public int Id { get; set; }
    public int EquipoId { get; set; }
    public string EquipoSku { get; set; } = "";
    public string EquipoNombre { get; set; } = "";
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
}

public class AlqReservaDto
{
    public int Id { get; set; }
    public string Numero { get; set; } = "";
    public int ClienteId { get; set; }
    public string ClienteNombre { get; set; } = "";
    public string? ClienteTelefono { get; set; }
    public DateTime FechaEntrega { get; set; }
    public DateTime FechaRetiro { get; set; }
    public string? HoraInicio { get; set; }
    public string? HoraFin { get; set; }
    public string? DireccionEvento { get; set; }
    public decimal MontoTotal { get; set; }
    public decimal Descuento { get; set; }
    public decimal Sena { get; set; }
    public string Estado { get; set; } = "reservado";
    public string? Notas { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<AlqReservaItemDto> Items { get; set; } = new();

    // ===== QR + Repartidor (2026-06-26) =====
    public string? PublicToken { get; set; }
    public decimal MontoCobrado { get; set; }
    public int? EntregadoPorRepartidorId { get; set; }
    public string? EntregadoPorRepartidorNombre { get; set; }
    public DateTime? EntregadoAt { get; set; }
    public string? ComentarioEntrega { get; set; }
    public int? RetiradoPorRepartidorId { get; set; }
    public string? RetiradoPorRepartidorNombre { get; set; }
    public DateTime? RetiradoAt { get; set; }
    public string? ComentarioRetiro { get; set; }
    public int? AsignadoARepartidorId { get; set; }
    public string? AsignadoARepartidorNombre { get; set; }
}

public class CreateAlqReservaItemRequest
{
    public int EquipoId { get; set; }
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
}

public class CreateAlqReservaRequest
{
    public int ClienteId { get; set; }
    public DateTime FechaEntrega { get; set; }
    public DateTime FechaRetiro { get; set; }
    public string? HoraInicio { get; set; }
    public string? HoraFin { get; set; }
    public string? DireccionEvento { get; set; }
    public decimal Descuento { get; set; }
    public decimal Sena { get; set; }
    public decimal? MontoTotalManual { get; set; }
    public string? Estado { get; set; }
    public string? Notas { get; set; }
    public List<CreateAlqReservaItemRequest> Items { get; set; } = new();
}

// ===== Resumen dashboard (2026-06-26) =====
public record AlqProximoDto(int Id, string Numero, string ClienteNombre, DateTime Fecha, string Tipo);
public class AlqDashboardResumen
{
    public int EnCalle { get; set; }
    public decimal MontoEnCalle { get; set; }
    public decimal SaldoACobrar { get; set; }
    public int ARetirarHoy { get; set; }
    public int Vencidos { get; set; }
    public int EntregasHoy { get; set; }
    public int RetirosHoy { get; set; }
    public List<AlqProximoDto> Proximos { get; set; } = new();
}

// ===== Repartidor / Cobranzas pendientes (2026-06-26) =====
public record AlqCobranzaPendienteDto(
    int Id, int ReservaId, string ReservaNumero, string ClienteNombre,
    int RepartidorId, string RepartidorNombre,
    decimal Importe, string Tipo, bool MarcadoEntregado, bool MarcadoRetirado,
    string? Notas, string Estado, string? RechazadaMotivo,
    DateTime CreatedAt, decimal ReservaSaldo);

public class UpdateAlqReservaRequest
{
    public int? ClienteId { get; set; }
    public DateTime? FechaEntrega { get; set; }
    public DateTime? FechaRetiro { get; set; }
    public string? HoraInicio { get; set; }
    public string? HoraFin { get; set; }
    public string? DireccionEvento { get; set; }
    public decimal? Descuento { get; set; }
    public decimal? Sena { get; set; }
    public decimal? MontoTotalManual { get; set; }
    public string? Estado { get; set; }
    public string? Notas { get; set; }
    public List<CreateAlqReservaItemRequest>? Items { get; set; }
}

public class AlqDisponibilidadDto
{
    public int EquipoId { get; set; }
    public string EquipoSku { get; set; } = "";
    public string EquipoNombre { get; set; } = "";
    public int StockTotal { get; set; }
    public int StockComprometido { get; set; }
    public int Disponible { get; set; }
}

public class AlqCondicionesDto
{
    public string Texto { get; set; } = "";
}
