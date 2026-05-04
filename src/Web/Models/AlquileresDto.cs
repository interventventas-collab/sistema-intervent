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
