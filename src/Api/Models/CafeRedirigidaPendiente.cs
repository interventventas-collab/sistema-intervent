using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-09-26 (pedido del dueño): una REDIRIGIDA cargada escribiendo "redi" al WhatsApp FRIKAF.
/// El cliente le pagó directo a un empleado o a un proveedor (o quedó en la privada); quien se entera
/// lo manda por WhatsApp y queda en la bolsita 💰 (Cobranzas a aprobar) para que la oficina la vuelque
/// como cualquier cobro de repartidor. El cliente y el que recibe se pueden elegir de una lista o
/// escribir con palabras: en ese caso se eligen de verdad al volcar.
/// Mientras se está cargando por WhatsApp la fila queda en BORRADOR (con el paso en que va).
/// </summary>
[Table("Cafe_RedirigidasPendientes")]
public class CafeRedirigidaPendiente
{
    public int Id { get; set; }

    /// <summary>BORRADOR (cargándose por WhatsApp) | PENDIENTE | APROBADA | RECHAZADA</summary>
    [Required, MaxLength(20)] public string Estado { get; set; } = "BORRADOR";
    /// <summary>En qué pregunta va la charla (solo en BORRADOR): cliente | recibe | destino | importe | adjunto | confirmar.</summary>
    [MaxLength(20)] public string? Paso { get; set; }
    public DateTime? ExpiraAt { get; set; }

    // Quién la envía (el cliente que pagó)
    public int? ClienteId { get; set; }
    [ForeignKey(nameof(ClienteId))] public CafeCliente? Cliente { get; set; }
    [MaxLength(200)] public string? ClienteTexto { get; set; }

    // Quién la recibe
    /// <summary>EMPLEADO | PROVEEDOR | PRIVADA | TEXTO</summary>
    [MaxLength(15)] public string? RecibeTipo { get; set; }
    public int? EmpleadoId { get; set; }
    public int? ProveedorId { get; set; }
    [MaxLength(200)] public string? RecibeTexto { get; set; }
    /// <summary>Si la recibe un empleado: de qué se le descuenta (viajes | sueldo). Mismo valor que la cobranza.</summary>
    [MaxLength(15)] public string? Destino { get; set; }

    [Column(TypeName = "decimal(18,2)")] public decimal Importe { get; set; }

    /// <summary>2026-09-26 (versión simple): lo que escribieron por WhatsApp — quién la mandó, a quién le
    /// llegó, lo que sepan. El cliente y el que la recibe se eligen en la PC al volcar, leyendo esto.</summary>
    [MaxLength(1000)] public string? Mensaje { get; set; }

    [MaxLength(80)] public string? EnviadoPor { get; set; }
    [MaxLength(60)] public string? EnviadoNumero { get; set; }

    public int? CobranzaCreadaId { get; set; }
    [MaxLength(200)] public string? RechazadaMotivo { get; set; }
    [MaxLength(120)] public string? RevisadaPor { get; set; }
    public DateTime? RevisadaAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(CafeRedirigidaPendienteAdjunto.PendienteId))]
    public List<CafeRedirigidaPendienteAdjunto> Adjuntos { get; set; } = new();
}

/// <summary>Foto o comprobante que mandaron por WhatsApp con la redirigida. El archivo ya lo guardó el
/// webhook en /data/whatsapp-uploads; al volcar se copia a los adjuntos de la cobranza.</summary>
[Table("Cafe_RedirigidasPendientesAdjuntos")]
public class CafeRedirigidaPendienteAdjunto
{
    public int Id { get; set; }
    public int PendienteId { get; set; }
    [MaxLength(200)] public string StoredFilename { get; set; } = "";
    [MaxLength(260)] public string NombreOriginal { get; set; } = "";
    [MaxLength(120)] public string? MimeType { get; set; }
    public long Tamano { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
