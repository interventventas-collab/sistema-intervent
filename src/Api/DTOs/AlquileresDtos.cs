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
    int Id, string Nombre, string? Empresa, string? Telefono, string? Email,
    string? DireccionDefault, string? Notas,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt);

public class CreateAlqClienteRequest
{
    public string Nombre { get; set; } = string.Empty;
    public string? Empresa { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? DireccionDefault { get; set; }
    public string? Notas { get; set; }
}

public class UpdateAlqClienteRequest
{
    public string? Nombre { get; set; }
    public string? Empresa { get; set; }
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? DireccionDefault { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
}
