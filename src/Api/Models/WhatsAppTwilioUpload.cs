using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Archivos subidos al servidor para enviar via WhatsApp Twilio.
/// Twilio requiere URL publica del adjunto, asi que guardamos el archivo con un token
/// random y lo servimos via endpoint AllowAnonymous. Token expira en 24h.
/// </summary>
[Table("WhatsApp_TwilioUploads")]
public class WhatsAppTwilioUpload
{
    public int Id { get; set; }
    [MaxLength(64)] public string Token { get; set; } = "";
    [MaxLength(255)] public string OriginalFilename { get; set; } = "";
    [MaxLength(255)] public string StoredFilename { get; set; } = "";
    [MaxLength(120)] public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public int? UploadedByUserId { get; set; }
    [MaxLength(30)] public string? NumeroDestino { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(24);
    public DateTime? DownloadedAt { get; set; }
}
