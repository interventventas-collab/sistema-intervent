namespace Web.Models;

public class VisitaDto
{
    public int Id { get; set; }
    public int? ClienteId { get; set; }
    public string ClienteNombre { get; set; } = "";
    public string? Direccion { get; set; }
    public string? Localidad { get; set; }
    public string? Telefono { get; set; }
    public string Descripcion { get; set; } = "";
    public string Estado { get; set; } = "pendiente";
    public bool TieneFirma { get; set; }
    public string? NombreFirmante { get; set; }
    public string? PublicToken { get; set; }
    public string? ComentarioResolucion { get; set; }
    public DateTime? RealizadaAt { get; set; }
    public decimal? MapeoLat { get; set; }
    public decimal? MapeoLng { get; set; }
    public string? CreadoPor { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateVisitaRequest
{
    public int? ClienteId { get; set; }
    public string ClienteNombre { get; set; } = "";
    public string? Direccion { get; set; }
    public string? Localidad { get; set; }
    public string? Telefono { get; set; }
    public string Descripcion { get; set; } = "";
    public string? FirmaBase64 { get; set; }
    public string? NombreFirmante { get; set; }
    public decimal? MapeoLat { get; set; }
    public decimal? MapeoLng { get; set; }
    public string? CreadoPor { get; set; }
}
