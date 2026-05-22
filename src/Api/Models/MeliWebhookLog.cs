using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Log de cada webhook que llega a /api/meli/webhook. Sirve para debugging, auditoria
/// y para reintentar manualmente si algo se proceso mal.
///
/// MeLi entrega webhooks "at-least-once": cuando algo cambia (orden, item, question, etc)
/// MeLi llama nuestro endpoint con el resource y topic. Si fallamos en responder 200 OK
/// rapido (&lt; 5s), MeLi lo reintenta hasta 10 veces con backoff exponencial.
///
/// Agregado 2026-05-22 como parte de la migracion a event-driven (reemplazar Contabilium
/// como hub de stock/precio MeLi).
/// </summary>
[Table("MeliWebhookLogs")]
public class MeliWebhookLog
{
    [Key]
    public int Id { get; set; }

    /// <summary>orders_v2 | items | questions | claims | shipments | etc.</summary>
    [MaxLength(40)]
    public string? Topic { get; set; }

    /// <summary>Path del recurso afectado, ej: /orders/2000000000.</summary>
    [MaxLength(200)]
    public string? Resource { get; set; }

    /// <summary>MeLi user_id del seller dueño del recurso (matchea contra MeliAccounts.MeliUserId).</summary>
    public long? UserId { get; set; }

    /// <summary>Cuantas veces MeLi reintento este mismo webhook (empieza en 1).</summary>
    public int? Attempts { get; set; }

    /// <summary>Cuando MeLi envio el webhook (segun su servidor).</summary>
    public DateTime? SentAt { get; set; }

    /// <summary>Cuando lo recibio nuestro endpoint.</summary>
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Cuando terminamos de procesarlo en background. Null = todavia pendiente o no se proceso.</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>True si el procesamiento termino sin excepcion. Null = todavia no proceso.</summary>
    public bool? ProcessedOk { get; set; }

    /// <summary>Detalle del error si ProcessedOk = false.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>JSON crudo que mando MeLi (por si necesitamos reprocesar).</summary>
    public string? RawBody { get; set; }
}
