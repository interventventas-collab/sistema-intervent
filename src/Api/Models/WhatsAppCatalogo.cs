using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-08-04: Catálogos permanentes para enviar por WhatsApp (PDF, documentos, imágenes).
/// A diferencia de WhatsApp_TwilioUploads (que expira en 24h), estos archivos quedan
/// guardados para siempre hasta que el operador los borre. Se guardan en /data/whatsapp-uploads
/// (mismo volume persistente) y se envían generando un token de descarga temporal al momento de mandar.
/// </summary>
[Table("WhatsApp_Catalogos")]
public class WhatsAppCatalogo
{
    public int Id { get; set; }
    [MaxLength(64)] public string Token { get; set; } = "";
    [MaxLength(255)] public string OriginalFilename { get; set; } = "";
    [MaxLength(255)] public string StoredFilename { get; set; } = "";
    [MaxLength(120)] public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public int? UploadedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
