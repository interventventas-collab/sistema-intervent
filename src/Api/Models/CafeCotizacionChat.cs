using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Un presupuesto de PRODUCTOS que se le pasó a alguien por el chat (botón 🧮 Cotizar productos).
/// 2026-10-17. Pedido del dueño: "que se guarden los presupuestos que voy pasando y me deje pasar
/// a venta precargada".
///
/// Igual que los de alquiler (AlqCotizacion) va pegado al TELÉFONO: muchos consultan sin ser
/// clientes. Se guarda solo al tocar "Pasar al chat". Los renglones van en ItemsJson con el
/// precio CONGELADO del día: al pasarlo a venta se respeta lo que se le dijo al cliente.
/// </summary>
[Table("Cafe_CotizacionesChat")]
public class CafeCotizacionChat
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Teléfono del chat (solo dígitos).</summary>
    [Required, MaxLength(30)]
    public string Telefono { get; set; } = string.Empty;

    public int? ClienteId { get; set; }

    /// <summary>Total tal cual se le pasó (con o sin IVA según ConIva).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; }

    public bool ConIva { get; set; }

    /// <summary>El mensaje tal cual se le pasó.</summary>
    [MaxLength(4000)]
    public string? Texto { get; set; }

    /// <summary>Lista de CafeCotizacionChatItemDto en JSON.</summary>
    public string ItemsJson { get; set; } = "[]";

    [MaxLength(60)]
    public string? Operador { get; set; }

    /// <summary>Venta en la que se convirtió (desde el modo venta del chat). Null = todavía no.</summary>
    public int? VentaId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
