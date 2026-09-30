using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>30/09/2026: respaldo automático de TODOS los comprobantes en Google Drive (por si se
/// cae el sistema). Una fila por comprobante subido: qué es, dónde quedó en Drive y cuándo.
///   • El robot (DriveRespaldoBackgroundService) compara la última modificación del comprobante
///     contra MarcaSubida: si se editó después, lo vuelve a subir PISANDO el mismo archivo.
///   • Si falla, queda UltimoError y se reintenta más tarde (Intentos sube, espera más cada vez).
/// Tipo + EntidadId es único.</summary>
[Table("Drive_Respaldos")]
public class DriveRespaldo
{
    public int Id { get; set; }

    /// <summary>VENTA | COBRANZA | ALQ_RESERVA | ALQ_FACTURA | ALQ_NC | COMODATO</summary>
    [Required, MaxLength(20)] public string Tipo { get; set; } = "";
    public int EntidadId { get; set; }

    [MaxLength(200)] public string? DriveFileId { get; set; }
    [MaxLength(260)] public string? NombreArchivo { get; set; }
    [MaxLength(200)] public string? Carpeta { get; set; }

    /// <summary>UpdatedAt (o CreatedAt) del comprobante tal como estaba cuando se armó el PDF subido.</summary>
    public DateTime? MarcaSubida { get; set; }
    public DateTime? SubidoAt { get; set; }

    [MaxLength(1000)] public string? UltimoError { get; set; }
    public int Intentos { get; set; }
    public DateTime? UltimoIntentoAt { get; set; }
}
