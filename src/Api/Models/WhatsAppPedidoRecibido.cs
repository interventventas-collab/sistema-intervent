using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Pedido recibido vía WhatsApp del vendedor (el hermano), que el sistema parsea con IA
/// y crea una venta DRAFT_WHATSAPP para que el usuario la confirme con un click.
///
/// Flujo:
/// 1. Vendedor manda mensaje al WhatsApp del sistema con formato "#PEDIDO Cliente\n productos..."
/// 2. (Fase 2) Playwright lee el mensaje y crea registro acá con TextoCrudo
///    (Fase 1) El usuario pega el texto manualmente en la pantalla /cafe/pedidos-whatsapp
/// 3. IA parsea TextoCrudo → detecta Cliente + lista de productos
/// 4. Estado pasa de NUEVO a PARSEADO
/// 5. Usuario click "Crear venta" → se crea CafeVenta con esos productos
/// 6. Estado pasa a VENTA_CREADA con VentaIdGenerada
/// </summary>
[Table("WhatsAppPedidosRecibidos")]
public class WhatsAppPedidoRecibido
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(40)]
    public string Telefono { get; set; } = "";

    [Required]
    public string TextoCrudo { get; set; } = "";

    public int? ClienteId { get; set; }

    [MaxLength(200)]
    public string? ClienteNombre { get; set; }

    /// <summary>JSON con la lista de productos detectados por IA. Estructura:
    /// [{ "Sku": "F2", "Nombre": "Café Brasil Premium", "Cantidad": 25, "Formato": "1KG", "PrecioOverride": null, "Notas": "..." }, ...]</summary>
    public string? ProductosParseados { get; set; }

    [MaxLength(500)]
    public string? ParseError { get; set; }

    /// <summary>NUEVO | PARSEADO | VENTA_CREADA | ERROR | DESCARTADO</summary>
    [Required, MaxLength(20)]
    public string Estado { get; set; } = "NUEVO";

    /// <summary>2026-07-23: que documento pidio el que escribio, segun el trigger del mensaje.
    /// PEDIDO (##/#nro, como siempre) | COTIZACION (XC) | PRESUPUESTO (XP) | FACTURA (XF).</summary>
    [Required, MaxLength(20)]
    public string TipoSolicitado { get; set; } = "PEDIDO";

    public int? VentaIdGenerada { get; set; }

    public DateTime RecibidoAt { get; set; } = DateTime.UtcNow;
    public DateTime? ParseadoAt { get; set; }
    public DateTime? VentaCreadaAt { get; set; }

    /// <summary>manual | whatsapp_auto</summary>
    [Required, MaxLength(20)]
    public string Source { get; set; } = "manual";

    public DateTime? SeenAt { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }
}
