namespace Web.Models;

public class BrandDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool HasExpiry { get; set; }
    // CSV de empresas en las que se muestra esta marca. NULL/vacio = visible para TODAS.
    public string? Companies { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateBrandRequest
{
    public string? Code { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool? HasExpiry { get; set; }
    public string? Companies { get; set; }
}

public class UpdateBrandRequest
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public bool? HasExpiry { get; set; }
    public string? Companies { get; set; }
    public bool? IsActive { get; set; }
}
