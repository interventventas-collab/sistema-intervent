using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-06-03: configuracion singleton (siempre Id=1) del modo nuevo de fichada.
/// Mientras ActivarModoNuevo=false, el endpoint publico se comporta exactamente como
/// venia funcionando (sin validar IP/huella). Cuando se prende, aplican las reglas.
/// </summary>
[Table("HorasExtras_ConfigFichada")]
public class HorasExtrasConfigFichada
{
    [Key]
    public int Id { get; set; } = 1;

    /// <summary>Toggle maestro. Mientras este false, todo sigue igual que antes.</summary>
    public bool ActivarModoNuevo { get; set; } = false;

    /// <summary>IP publica del WiFi #1 del negocio. Vacio = no validado.</summary>
    [MaxLength(64)]
    public string? Wifi1Ip { get; set; }
    [MaxLength(80)]
    public string? Wifi1Label { get; set; }

    /// <summary>IP publica del WiFi #2 del negocio (oficina secundaria, depósito, etc).</summary>
    [MaxLength(64)]
    public string? Wifi2Ip { get; set; }
    [MaxLength(80)]
    public string? Wifi2Label { get; set; }

    /// <summary>Si true, el empleado tiene que confirmar con huella biometrica.
    /// Si el celu no soporta -> fallback PIN (igual que hoy).</summary>
    public bool RequiereHuella { get; set; } = false;

    /// <summary>Si true, se captura GPS al fichar y se guarda en HorasExtras_FichadaMeta.
    /// NO bloquea — solo dato de auditoria.</summary>
    public bool LoguearGps { get; set; } = false;

    public DateTime? UpdatedAt { get; set; }
    [MaxLength(120)]
    public string? UpdatedBy { get; set; }
}
