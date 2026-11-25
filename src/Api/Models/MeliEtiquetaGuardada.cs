using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-11-25: copia de la etiqueta ORIGINAL de MeLi de cada envío, guardada la primera vez que se
/// imprime. MeLi deja de darla cuando el envío sale (NOT_PRINTABLE_STATUS); con esto "Recuperar
/// etiqueta impresa" devuelve exactamente la misma. Zpl = el bloque de la Zebra tal cual lo manda MeLi;
/// Pdf = el PDF de MeLi pedido para ese envío solo. Se borran a los 60 días.
/// </summary>
[Table("Meli_EtiquetasGuardadas")]
public class MeliEtiquetaGuardada
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public long MeliShipmentId { get; set; }
    public int MeliAccountId { get; set; }
    public string? Zpl { get; set; }
    public byte[]? Pdf { get; set; }
    public DateTime GuardadaAt { get; set; } = DateTime.UtcNow;
}
