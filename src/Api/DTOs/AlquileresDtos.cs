namespace Api.DTOs;

// ===== Equipos =====
public record AlqEquipoDto(
    int Id, string Sku, string Nombre, string? Categoria, string? Descripcion,
    int StockTotal, decimal PrecioDiario, decimal? PrecioReposicion,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt);

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

// ===== Clientes =====
public record AlqClienteDto(
    int Id, string Nombre, string? Empresa, string? DniCuit,
    string? Telefono, string? Telefono2, string? Email,
    string? DireccionDefault, string? Piso, string? Depto, string? Barrio, string? EntreCalles,
    string? Notas,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt);

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
public record AlqReservaItemDto(int Id, int EquipoId, string EquipoSku, string EquipoNombre, int Cantidad, decimal PrecioUnitario);

public record AlqReservaDto(
    int Id, string Numero,
    int ClienteId, string ClienteNombre, string? ClienteTelefono,
    DateTime FechaEntrega, DateTime FechaRetiro,
    string? HoraInicio, string? HoraFin,
    string? DireccionEvento,
    decimal MontoTotal, decimal Descuento, decimal Sena,
    string Estado, string? Notas,
    DateTime CreatedAt, DateTime? UpdatedAt,
    List<AlqReservaItemDto> Items);

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
    /// <summary>Si viene con valor, se usa como total final (modo "importe a mano"). Si es null, se calcula sumando los items.</summary>
    public decimal? MontoTotalManual { get; set; }
    public string? Estado { get; set; }
    public string? Notas { get; set; }
    public List<CreateAlqReservaItemRequest> Items { get; set; } = new();
}

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
    /// <summary>Si viene con valor, se usa como total final (modo "importe a mano"). Si es null, se calcula sumando los items.</summary>
    public decimal? MontoTotalManual { get; set; }
    public string? Estado { get; set; }
    public string? Notas { get; set; }
    public List<CreateAlqReservaItemRequest>? Items { get; set; }
}

// ===== Disponibilidad =====
public record AlqDisponibilidadDto(int EquipoId, string EquipoSku, string EquipoNombre, int StockTotal, int StockComprometido, int Disponible);
