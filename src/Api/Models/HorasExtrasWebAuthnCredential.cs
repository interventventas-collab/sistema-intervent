using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-06-03: credencial WebAuthn registrada por un empleado. Cada empleado puede tener
/// varias (ej. una por cada celular que use). Al fichar, el cel firma con su clave privada
/// y el backend verifica con la publica guardada aca.
///
/// Persistencia compatible con Fido2NetLib: guardamos los campos necesarios para reconstruir
/// la PublicKeyCredentialDescriptor + la public key + counter de uso.
/// </summary>
[Table("HorasExtras_WebAuthnCredentials")]
public class HorasExtrasWebAuthnCredential
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int EmpleadoId { get; set; }
    [ForeignKey(nameof(EmpleadoId))]
    public HorasExtrasEmpleado? Empleado { get; set; }

    /// <summary>ID unico de la credencial (Base64Url). Lo manda el browser y se guarda para
    /// referenciar al hacer login.</summary>
    [Required]
    public string CredentialId { get; set; } = "";

    /// <summary>Clave publica de la credencial (CBOR -> Base64). Usada por Fido2NetLib para
    /// verificar la firma.</summary>
    [Required]
    public string PublicKey { get; set; } = "";

    /// <summary>User handle (Base64Url) que se le asigno al empleado en el registro.
    /// Es lo que devuelve el browser en login. Lo usamos para identificar el empleado.</summary>
    [Required]
    [MaxLength(200)]
    public string UserHandle { get; set; } = "";

    /// <summary>AAGUID del authenticator (qué tipo de cel/dispositivo lo registro). Solo info.</summary>
    [MaxLength(64)]
    public string? AaGuid { get; set; }

    /// <summary>Contador de uso. Se incrementa cada autenticacion. Detecta clones.</summary>
    public uint SignatureCounter { get; set; }

    /// <summary>Nombre amistoso del dispositivo (ej. "iPhone de Alexis"). Le permite al empleado
    /// reconocer y borrar credenciales viejas.</summary>
    [MaxLength(120)]
    public string? DeviceName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
}
