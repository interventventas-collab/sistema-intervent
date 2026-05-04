namespace Api.DTOs;

// ===== Clientes =====
public record CafeClienteDto(
    int Id, string Nombre, string Tipo,
    string? Telefono, string? Direccion, string? Notas,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt);

public class CreateCafeClienteRequest
{
    public string Nombre { get; set; } = string.Empty;
    public string Tipo { get; set; } = "OTRO";
    public string? Telefono { get; set; }
    public string? Direccion { get; set; }
    public string? Notas { get; set; }
}

public class UpdateCafeClienteRequest
{
    public string? Nombre { get; set; }
    public string? Tipo { get; set; }
    public string? Telefono { get; set; }
    public string? Direccion { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Productos =====
public record CafeProductoDto(
    int Id, string Nombre, string Categoria,
    decimal Costo, decimal? PrecioPorKg,
    decimal? Pvp1, decimal? Pvp2,
    decimal StockGramos, int StockUnidades,
    string? Notas, bool IsActive,
    DateTime CreatedAt, DateTime? UpdatedAt);

public class CreateCafeProductoRequest
{
    public string Nombre { get; set; } = string.Empty;
    public string Categoria { get; set; } = "CAFE";
    public decimal Costo { get; set; }
    public decimal? PrecioPorKg { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? StockGramos { get; set; }
    public int? StockUnidades { get; set; }
    public string? Notas { get; set; }
}

public class UpdateCafeProductoRequest
{
    public string? Nombre { get; set; }
    public string? Categoria { get; set; }
    public decimal? Costo { get; set; }
    public decimal? PrecioPorKg { get; set; }
    public decimal? Pvp1 { get; set; }
    public decimal? Pvp2 { get; set; }
    public decimal? StockGramos { get; set; }
    public int? StockUnidades { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Settings =====
public record CafeSettingDto(
    decimal CostoFraccionamiento, decimal RedondeoMultiplo,
    decimal MargenOtrosBarPct, decimal MargenOtrosNoBarPct,
    string? NegocioNombre, string? NegocioTelefono, string? NegocioWhatsappNumero,
    string? NegocioDireccion, string? NegocioCuit,
    DateTime? UpdatedAt);

public class UpdateCafeSettingRequest
{
    public decimal? CostoFraccionamiento { get; set; }
    public decimal? RedondeoMultiplo { get; set; }
    public decimal? MargenOtrosBarPct { get; set; }
    public decimal? MargenOtrosNoBarPct { get; set; }
    public string? NegocioNombre { get; set; }
    public string? NegocioTelefono { get; set; }
    public string? NegocioWhatsappNumero { get; set; }
    public string? NegocioDireccion { get; set; }
    public string? NegocioCuit { get; set; }
}
