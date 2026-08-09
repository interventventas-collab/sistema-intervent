using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-08-04: estado + responsable de una conversación de WhatsApp/Instagram.
/// Una "conversación" se identifica por (Numero + LineaPhoneId) — igual que se agrupa en la
/// pantalla. Esta fila SOLO existe si alguien le puso estado o la pasó a otra persona; si no
/// existe, la conversación se muestra como "nueva" y sin asignar.
///
/// AsignadoOperador guarda la FIRMA del operador (OSMAR/GERMAN/GABRIEL/DEPOSITO), no un usuario,
/// porque esas personas comparten cuenta y se identifican por operador (ver OperatorService).
/// AsignadoVisto = el que la recibió ya la abrió (sirve para el aviso "te la pasó X").</summary>
[Table("WhatsApp_Conversaciones")]
public class WhatsAppConversacion
{
    public int Id { get; set; }
    [MaxLength(30)] public string Numero { get; set; } = "";
    [MaxLength(30)] public string? LineaPhoneId { get; set; }
    /// <summary>nueva | en_curso | esperando | finalizada</summary>
    [MaxLength(20)] public string Estado { get; set; } = "nueva";
    /// <summary>Firma del operador al que se le pasó (OSMAR/GERMAN/GABRIEL/DEPOSITO). Null = sin asignar.</summary>
    [MaxLength(40)] public string? AsignadoOperador { get; set; }
    /// <summary>Firma del operador que la pasó (para el aviso "te la pasó Gabriel").</summary>
    [MaxLength(40)] public string? AsignadoPor { get; set; }
    [MaxLength(300)] public string? AsignadoNota { get; set; }
    public DateTime? AsignadoAt { get; set; }
    /// <summary>El que la recibió ya la abrió. False = pendiente (badge/resaltado en "Míos").</summary>
    public bool AsignadoVisto { get; set; }
    /// <summary>2026-08-09: cuándo se archivó la charla (sacarla del listado). Null = no archivada.
    /// El listado la considera archivada solo si el cliente NO volvió a escribir después de esta fecha:
    /// si entra un mensaje entrante más nuevo, "se desarchiva" sola (reaparece en el listado general).</summary>
    public DateTime? ArchivadoAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
