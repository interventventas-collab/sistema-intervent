namespace Api.DTOs;

public record VisitaDto(
    int Id,
    int? ClienteId,
    string ClienteNombre,
    string? Direccion,
    string? Localidad,
    string? Telefono,
    string Descripcion,
    string Estado,
    bool TieneFirma,
    string? NombreFirmante,
    string? PublicToken,
    string? ComentarioResolucion,
    DateTime? RealizadaAt,
    decimal? MapeoLat,
    decimal? MapeoLng,
    string? CreadoPor,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>DTO publico (por token, sin auth): incluye la firma para mostrarla en el recibo.</summary>
public record VisitaPublicaDto(
    int Id,
    string ClienteNombre,
    string? Direccion,
    string? Localidad,
    string? Telefono,
    string Descripcion,
    string Estado,
    string? FirmaBase64,
    string? NombreFirmante,
    string? ComentarioResolucion,
    DateTime? RealizadaAt,
    DateTime CreatedAt);

public class CreateVisitaRequest
{
    public int? ClienteId { get; set; }
    public string ClienteNombre { get; set; } = string.Empty;
    public string? Direccion { get; set; }
    public string? Localidad { get; set; }
    public string? Telefono { get; set; }
    public string Descripcion { get; set; } = string.Empty;
    public string? FirmaBase64 { get; set; }
    public string? NombreFirmante { get; set; }
    public decimal? MapeoLat { get; set; }
    public decimal? MapeoLng { get; set; }
    public string? CreadoPor { get; set; }
}

public class UpdateVisitaRequest
{
    public string? Descripcion { get; set; }
    public string? FirmaBase64 { get; set; }
    public string? NombreFirmante { get; set; }
}

/// <summary>Body para marcar una visita como realizada desde el escaneo del QR (Etapa 2).</summary>
public class MarcarVisitaRealizadaRequest
{
    public string? Comentario { get; set; }
}
