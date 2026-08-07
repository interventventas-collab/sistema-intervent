using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-08-07: huella (WebAuthn) para desbloquear la pantalla de WhatsApp del celu
/// (/whatsapp-movil). Cada persona (Osmar/Germán/Gabriel) registra su huella una vez por celu;
/// después entra tocando la huella. Independiente de la huella de la fichada de empleados.</summary>
[Table("WaMovil_WebAuthnCredentials")]
public class WaMovilWebAuthnCredential
{
    public int Id { get; set; }
    /// <summary>Nombre de la persona dueña de la huella (Osmar/Germán/Gabriel), tomado de wamovil.codigos.</summary>
    [MaxLength(60)] public string Persona { get; set; } = "";
    [MaxLength(400)] public string CredentialId { get; set; } = "";   // Base64
    [MaxLength(2000)] public string PublicKey { get; set; } = "";     // Base64 (CBOR)
    [MaxLength(200)] public string UserHandle { get; set; } = "";     // Base64
    [MaxLength(64)] public string? AaGuid { get; set; }
    public uint SignatureCounter { get; set; }
    [MaxLength(120)] public string? DeviceName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}
