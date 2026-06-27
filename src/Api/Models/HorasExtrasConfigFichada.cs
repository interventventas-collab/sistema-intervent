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

    // ─── 2026-06-27: bloqueo por GPS (geocerca) ───
    /// <summary>Toggle maestro del bloqueo por ubicacion. Si true, los empleados marcados con
    /// ProbarGpsFichada solo pueden fichar si estan dentro del radio del negocio. Independiente
    /// del WiFi: se puede usar "solo GPS" dejando ActivarModoNuevo en false.</summary>
    public bool BloquearPorGps { get; set; } = false;

    /// <summary>Coordenadas del negocio (centro de la geocerca). Se capturan una vez parado en el
    /// local con el boton "Capturar ubicacion del negocio". Si estan en null, el bloqueo no aplica.</summary>
    [Column(TypeName = "decimal(10,7)")]
    public decimal? NegocioLat { get; set; }
    [Column(TypeName = "decimal(10,7)")]
    public decimal? NegocioLon { get; set; }

    /// <summary>Radio permitido en metros alrededor del negocio. Default 150 (tolerante para que el
    /// GPS impreciso de interiores no rechace de mas a quien si esta adentro).</summary>
    public int RadioMetros { get; set; } = 150;

    public DateTime? UpdatedAt { get; set; }
    [MaxLength(120)]
    public string? UpdatedBy { get; set; }
}
