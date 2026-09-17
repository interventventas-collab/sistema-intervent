using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-09-17 — Una sesión abierta: "Osmar, Chrome en Windows, Oficina, hace 3 minutos".
///
/// POR QUÉ EXISTE ESTA TABLA. Hasta hoy el pase (JWT) era puro papel firmado: el sistema miraba la
/// firma y dejaba pasar, sin consultar nada. Eso tenía tres agujeros que el dueño quería tapar:
///   1. No había forma de ver quién estaba adentro ni desde qué aparato.
///   2. No se podía echar a nadie: el único que cerraba una sesión era su propio dueño.
///   3. Poner un usuario en "Inactivo" NO lo sacaba — seguía trabajando hasta 24 h, porque su pase
///      ya estaba firmado y nadie lo volvía a chequear.
///
/// Ahora cada pase lleva un número único (<see cref="Jti"/>) que apunta a una fila de acá, y en cada
/// pedido se chequea que la fila siga viva. Cerrar una sesión es marcarla muerta: el efecto es de
/// segundos, no hay que esperar a que venza nada.
///
/// UNA FILA POR APARATO, NO POR LOGIN. Si Osmar entra diez veces desde la misma compu, no quedan diez
/// renglones: se reusa la fila y se le cambia el número de pase. Así la pantalla muestra aparatos
/// (que es lo que él quiere mirar) y no un historial de tecleos. El efecto de costado es correcto y
/// buscado: volver a entrar desde la misma compu mata el pase anterior de esa compu.
/// </summary>
[Table("Users_Sesiones")]
public class UserSession
{
    public int Id { get; set; }

    /// <summary>Número único del pase (claim `jti` del JWT). Es el vínculo pase ↔ fila.</summary>
    [Required, MaxLength(40)] public string Jti { get; set; } = "";

    /// <summary>Usuario del sistema. NULL en las sesiones de huella del celu, que no son de un
    /// usuario sino de una persona (Osmar/Germán/Gabriel) y no tienen fila en Users.</summary>
    public int? UserId { get; set; }

    /// <summary>Nombre para mostrar: el usuario, o la persona de la huella.</summary>
    [Required, MaxLength(100)] public string Nombre { get; set; } = "";

    /// <summary>WEB = usuario y clave · HUELLA = el celu que abre WhatsApp con el dedo.</summary>
    [Required, MaxLength(10)] public string Tipo { get; set; } = TipoWeb;

    /// <summary>Lo que el navegador dice de sí mismo. Crudo, por si algún día hay que mirarlo.</summary>
    [MaxLength(400)] public string? UserAgent { get; set; }

    /// <summary>Traducido a cristiano: "Chrome en Windows", "Safari en iPhone".</summary>
    [MaxLength(80)] public string Dispositivo { get; set; } = "";

    /// <summary>Lo mismo pero sin la versión del navegador, para reconocer el MISMO aparato aunque
    /// Chrome se actualice. Si cambia esto, es un aparato nuevo de verdad.</summary>
    [MaxLength(80)] public string Huella { get; set; } = "";

    /// <summary>Apodo que le pone una persona: "La compu del depósito". El navegador nunca dice el
    /// nombre real del aparato — ni para nosotros ni para Google —, así que esto se carga a mano.</summary>
    [MaxLength(80)] public string? Apodo { get; set; }

    [MaxLength(60)] public string? IpCreacion { get; set; }
    [MaxLength(60)] public string? IpUltima { get; set; }

    /// <summary>La primera vez que se vio este aparato para esta persona. Lo mira el aviso de
    /// "entró desde un aparato nuevo" del panel de Alertas.</summary>
    public bool AparatoNuevo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiraAt { get; set; }

    /// <summary>Última vez que este pase pidió algo. Se refresca como mucho una vez por minuto para
    /// no escribir en la base en cada clic.</summary>
    public DateTime UltimaActividadAt { get; set; } = DateTime.UtcNow;

    // --- Cierre ---
    /// <summary>NULL = viva. Con fecha = muerta, no pasa más.</summary>
    public DateTime? CerradaAt { get; set; }
    /// <summary>Quién la cerró: el propio usuario, un admin, o el sistema.</summary>
    [MaxLength(100)] public string? CerradaPor { get; set; }
    /// <summary>Por qué se cerró, en castellano, para que la pantalla lo pueda mostrar.</summary>
    [MaxLength(120)] public string? CerradaMotivo { get; set; }

    public const string TipoWeb = "WEB";
    public const string TipoHuella = "HUELLA";

    public const string MotivoSalio = "Salió desde su pantalla";
    public const string MotivoEchada = "Cerrada desde Sesiones";
    public const string MotivoUsuarioInactivo = "El usuario quedó inactivo";
    public const string MotivoCambioClave = "Se cambió la clave";
    public const string MotivoEntroDeNuevo = "Volvió a entrar desde el mismo aparato";
}
