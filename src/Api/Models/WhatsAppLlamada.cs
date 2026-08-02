using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Registro de cada evento de LLAMADA de WhatsApp (Meta WhatsApp Business Calling API, lanzada jul-2025).
///
/// PRIMER LADRILLO del proyecto de llamadas de voz (2026-08-02, pedido Osmar): por ahora SOLO
/// recibimos y anotamos los eventos que Meta manda al webhook (mismo endpoint que los mensajes,
/// pero en el bloque value.calls[]). Todavia NO se atiende audio en vivo — eso es la fase grande
/// (WebRTC/TURN + softphone en el navegador). Ver memoria project_whatsapp_llamadas_voz_calling_api.
///
/// Meta manda varios eventos por llamada (la misma CallId): "connect" cuando el cliente llama
/// (trae el SDP offer), "terminate" cuando corta (trae el motivo y la duracion), etc. Guardamos
/// cada evento como una fila para tener el historial completo, y ademas el JSON crudo por si
/// despues necesitamos el SDP o algun campo que hoy no mapeamos.
/// </summary>
[Table("WhatsApp_Llamadas")]
public class WhatsAppLlamada
{
    [Key]
    public int Id { get; set; }

    /// <summary>Id de la llamada que asigna Meta (wacid...). Varios eventos comparten la misma CallId.</summary>
    [MaxLength(120)]
    public string? CallId { get; set; }

    /// <summary>Numero del cliente (quien llama, formato E164 en digitos, ej "5491122334455").</summary>
    [MaxLength(30)]
    public string? Numero { get; set; }

    /// <summary>phone_number_id de NUESTRA linea a la que entro la llamada (para saber por cual atender).</summary>
    [MaxLength(40)]
    public string? LineaPhoneId { get; set; }

    /// <summary>Numero visible de nuestra linea (display_phone_number), ej "5491122522458".</summary>
    [MaxLength(30)]
    public string? LineaNumero { get; set; }

    /// <summary>Evento que reporto Meta: connect | terminate | reject | call_permission_request | etc.</summary>
    [MaxLength(40)]
    public string? Evento { get; set; }

    /// <summary>Quien inicio la llamada: USER_INITIATED (el cliente) | BUSINESS_INITIATED (nosotros).</summary>
    [MaxLength(30)]
    public string? Direccion { get; set; }

    /// <summary>Estado/motivo de terminacion si lo trae (ej duracion, "COMPLETED", etc).</summary>
    [MaxLength(60)]
    public string? Estado { get; set; }

    /// <summary>Duracion en segundos si la llamada termino (la reporta Meta en el terminate).</summary>
    public int? DuracionSegundos { get; set; }

    /// <summary>Cuando Meta dice que ocurrio el evento (segun su timestamp).</summary>
    public DateTime? TimestampEvento { get; set; }

    /// <summary>Cuando lo recibio nuestro webhook.</summary>
    public DateTime RecibidoAt { get; set; } = DateTime.UtcNow;

    /// <summary>JSON crudo del objeto call, por si necesitamos el SDP u otro campo mas adelante.</summary>
    public string? RawJson { get; set; }
}
