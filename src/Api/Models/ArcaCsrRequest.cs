using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Pedido de CSR temporal. Cuando un usuario genera un certificado nuevo desde
/// el wizard, en el Paso 1 se crea una clave privada RSA + CSR y se guarda
/// acá. El usuario descarga el .csr, va a ARCA, obtiene el .crt y vuelve al
/// Paso 3 (finalize), donde combinamos la clave privada de esta fila con el
/// .crt para generar el .pfx final y se borra esta fila.
/// La clave privada vive solo el tiempo que tarda el usuario en ir a ARCA y
/// volver. Una vez generado el .pfx, ya no hace falta y se elimina.
/// </summary>
[Table("ArcaCsrRequests")]
public class ArcaCsrRequest
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(11)]
    public string Cuit { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string Alias { get; set; } = string.Empty;

    /// <summary>RSA 2048 en formato PEM PKCS#8.</summary>
    [Required]
    public string PrivateKeyPem { get; set; } = string.Empty;

    /// <summary>CSR (Certificate Signing Request) en formato PEM, firmado con SHA256/PKCS1.</summary>
    [Required]
    public string CsrPem { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
