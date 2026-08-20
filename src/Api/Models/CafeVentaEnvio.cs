using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-08-20: estado del envío del comprobante al cliente, una fila por venta+canal.
/// Cumple dos funciones a la vez:
///   • COLA: al emitir se anota PENDIENTE con ProgramadoPara = ahora + demora (5 min por
///     defecto). El robot lo manda recién cuando llega la hora, así hay tiempo de corregir
///     la venta antes de que salga. El PDF se arma en el momento del envío, no al encolar,
///     así que una corrección dentro de esos minutos viaja sola.
///   • HISTORIAL: después queda como ENVIADO (con fecha y destino) o ERROR (con el motivo),
///     que es lo que pintan los cartelitos 📧/📱 del listado de ventas. Antes de esto el
///     sistema no anotaba en ningún lado que una venta se había mandado.
/// Reenviar por el mismo canal PISA la fila (índice único VentaId+Canal): el cartelito
/// muestra siempre el último intento.</summary>
[Table("Cafe_VentasEnvios")]
public class CafeVentaEnvio
{
    public int Id { get; set; }
    public int VentaId { get; set; }

    /// <summary>EMAIL | WHATSAPP</summary>
    [Required, MaxLength(10)] public string Canal { get; set; } = "";

    /// <summary>PENDIENTE | ENVIADO | ERROR | CANCELADO</summary>
    [Required, MaxLength(12)] public string Estado { get; set; } = EstadoPendiente;

    /// <summary>Correo o teléfono al que sale. Se resuelve al encolar y se vuelve a mirar al enviar.</summary>
    [MaxLength(150)] public string? Destino { get; set; }

    /// <summary>Solo WhatsApp: desde qué línea sale (phone_id de Meta). Null = la de por defecto.</summary>
    [MaxLength(40)] public string? LineaPhoneId { get; set; }

    /// <summary>UTC. Cuándo le toca salir. Null en filas que ya se resolvieron.</summary>
    public DateTime? ProgramadoPara { get; set; }

    /// <summary>UTC. Cuándo salió de verdad.</summary>
    public DateTime? EnviadoAt { get; set; }

    /// <summary>Por qué no salió, en castellano y para mostrarle al usuario en el cartelito.</summary>
    [MaxLength(400)] public string? Error { get; set; }

    /// <summary>2026-08-20: texto propio que escribió el operador para ESTE envío. Si está, va en
    /// lugar del texto de siempre ("Hola X! Te paso el comprobante..."). Vacío = sale el de siempre.</summary>
    public string? Mensaje { get; set; }

    /// <summary>2026-08-20: mensaje SUELTO que sale DESPUÉS del comprobante, como un segundo
    /// mensaje (o un segundo mail). Sirve para aclarar algo sin ensuciar el envío del comprobante.</summary>
    public string? MensajeAparte { get; set; }

    public int Intentos { get; set; }

    /// <summary>true = lo disparó el tilde "mandarle siempre" de la ficha del cliente
    /// (no lo tildó nadie a mano en esa venta). Solo sirve para explicarlo en pantalla.</summary>
    public bool Automatico { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public const string CanalEmail = "EMAIL";
    public const string CanalWhatsapp = "WHATSAPP";
    public const string EstadoPendiente = "PENDIENTE";
    public const string EstadoEnviado = "ENVIADO";
    public const string EstadoError = "ERROR";
    public const string EstadoCancelado = "CANCELADO";
}
