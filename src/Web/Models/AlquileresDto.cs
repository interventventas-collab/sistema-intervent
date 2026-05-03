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
    public string? Telefono { get; set; }
    public string? Email { get; set; }
    public string? DireccionDefault { get; set; }
    public string? Notas { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

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
