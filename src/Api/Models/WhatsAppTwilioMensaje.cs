using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("WhatsApp_TwilioMensajes")]
public class WhatsAppTwilioMensaje
{
    public int Id { get; set; }
    [MaxLength(10)] public string Direccion { get; set; } = "INCOMING";
    [MaxLength(30)] public string Numero { get; set; } = "";
    [MaxLength(120)] public string? NombrePerfil { get; set; }
    public string? Cuerpo { get; set; }
    [MaxLength(500)] public string? MediaUrl { get; set; }
    /// <summary>2026-07-23: nombre original del adjunto (ej "Lista Take Away.pdf") para mostrarlo en el chat.</summary>
    [MaxLength(300)] public string? MediaFilename { get; set; }

    /// <summary>2026-07-23 (multi-línea): phone_number_id de Meta por el que entró/salió este mensaje.
    /// Preparación para tener 2+ números en la misma bandeja: cada chat responde por SU línea.</summary>
    [MaxLength(30)] public string? LineaPhoneId { get; set; }

    /// <summary>2026-07-24: estado de entrega que reporta Meta para los OUTGOING:
    /// sent (1 tilde), delivered (2 tildes grises), read (2 tildes azules), failed. Null = sin dato.</summary>
    [MaxLength(15)] public string? EstadoEntrega { get; set; }
    /// <summary>2026-08-22: cuando EstadoEntrega = "failed", POR QUE no llegó, explicado en castellano
    /// (ej "Ese número no tiene WhatsApp"). Antes se tiraba el motivo que manda Meta y en la pantalla
    /// solo quedaba un ⚠ mudo: el operador no sabía si era el número, la plantilla o la cuenta.</summary>
    [MaxLength(300)] public string? EntregaError { get; set; }
    /// <summary>Código de error de Meta (ej 131026). Sirve para buscar el caso raro en su documentación.</summary>
    public int? EntregaErrorCodigo { get; set; }
    public int? NumMedia { get; set; }
    /// <summary>ID del mensaje del proveedor: SID de Twilio o wamid.* de Meta Cloud API (este último es largo).</summary>
    [MaxLength(200)] public string? TwilioMessageSid { get; set; }
    /// <summary>2026-08-05: wamid del mensaje CITADO cuando este mensaje es una respuesta ("responder citando").
    /// Entrante: viene del context.id de Meta. Saliente: el wamid del mensaje al que contestamos.
    /// Se resuelve contra TwilioMessageSid del mensaje original para mostrar la burbuja citada.</summary>
    [MaxLength(200)] public string? ReplyToSid { get; set; }
    /// <summary>Canal de origen del mensaje: "TWILIO" (default) o "CLOUD" (API oficial de Meta).</summary>
    [MaxLength(10)] public string Canal { get; set; } = "TWILIO";
    public bool Procesado { get; set; }
    /// <summary>2026-08-07: si true, este mensaje NO se le muestra a los usuarios de Depósito
    /// (ven un cartelito "🚫 Mensaje ocultado" en su lugar). Lo marca admin/oficina. Sirve p.ej.
    /// cuando se pasa un pedido, se modifica y se remanda: el viejo se oculta.</summary>
    public bool OcultoDeposito { get; set; }
    /// <summary>2026-08-19: mensaje ANULADO. Un enviado NO se puede borrar del celular del cliente
    /// (limitación de Meta), así que cuando mandamos una corrección marcamos el equivocado y de
    /// NUESTRO lado se ve tachado y en gris, para que nadie del equipo siga trabajando sobre el dato
    /// viejo. El cliente lo sigue viendo normal. Null = no anulado.</summary>
    public DateTime? AnuladoAt { get; set; }
    /// <summary>Quién lo anuló (usuario u operador), para saber a quién preguntarle.</summary>
    [MaxLength(60)] public string? AnuladoPor { get; set; }
    [MaxLength(10)] public string? PedidoTrigger { get; set; }
    public int? VentaIdGenerada { get; set; }
    public string? RespuestaEnviada { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
