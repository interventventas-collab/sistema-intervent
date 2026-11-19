using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 18/11/2026: "#gasto" por WhatsApp. Pedido del dueño: "escribir #gasto y un detalle libre, todo en un
/// mensaje, que me llegue una notificación y al tocarla me lleve a Nuevo pago viendo ese mensaje".
/// El sistema NO contesta nada por WhatsApp (para no pagar mensajes). Cada fila es un gasto que llegó;
/// en el sistema se le dejan comentarios y se aplica (tilde, queda atado al pago) o se rechaza.
/// </summary>
[Table("Gastos_WhatsApp")]
public class GastoWhatsApp
{
    public int Id { get; set; }
    /// <summary>Id del mensaje en WhatsApp_TwilioMensajes (el texto original queda también en el chat).</summary>
    public int? MensajeId { get; set; }
    [MaxLength(30)] public string Numero { get; set; } = "";
    [MaxLength(120)] public string? NombrePerfil { get; set; }
    /// <summary>Lo que escribió después de "#gasto".</summary>
    [MaxLength(1000)] public string Texto { get; set; } = "";
    /// <summary>Foto o PDF del ticket (el que vino con el mensaje o el que mandó enseguida después).</summary>
    [MaxLength(500)] public string? MediaUrl { get; set; }
    [MaxLength(300)] public string? MediaFilename { get; set; }
    /// <summary>Importe que el sistema leyó del texto (null si no encontró ninguno).</summary>
    [Column(TypeName = "decimal(18,2)")] public decimal? Importe { get; set; }
    /// <summary>EFECTIVO / TRANSFERENCIA / MERCADOPAGO si lo dijo en el texto.</summary>
    [MaxLength(20)] public string? Medio { get; set; }
    /// <summary>PENDIENTE / APLICADO / IGNORADO (19/11: reemplaza a RECHAZADO, que queda para lo viejo).</summary>
    [MaxLength(15)] public string Estado { get; set; } = "PENDIENTE";
    /// <summary>Pago a proveedor (Cafe_PagosProveedor) con el que se aplicó.</summary>
    public int? PagoId { get; set; }
    [MaxLength(30)] public string? PagoNumero { get; set; }
    [MaxLength(300)] public string? MotivoRechazo { get; set; }
    [MaxLength(120)] public string? ResueltoPor { get; set; }
    public DateTime? ResueltoAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("Gastos_WhatsApp_Comentarios")]
public class GastoWhatsAppComentario
{
    public int Id { get; set; }
    public int GastoId { get; set; }
    [MaxLength(1000)] public string Texto { get; set; } = "";
    [MaxLength(120)] public string? Usuario { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
