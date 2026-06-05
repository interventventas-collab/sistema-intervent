namespace Web.Models;

/// <summary>2026-06-05: Catalogo de servicios (envio, mano de obra, etc).</summary>
public class CafeServicioDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Descripcion { get; set; }
    public decimal Precio { get; set; }
    public decimal IvaPct { get; set; }
    public bool IsActive { get; set; }
}

public class CafeServicioUpsertRequest
{
    public string Nombre { get; set; } = "";
    public string? Descripcion { get; set; }
    public decimal Precio { get; set; }
    public decimal? IvaPct { get; set; }
    public bool IsActive { get; set; } = true;
}
