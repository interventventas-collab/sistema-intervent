using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Un presupuesto de alquiler que se le pasó a alguien por WhatsApp. 2026-08-31.
///
/// Va pegado al TELÉFONO, no al cliente: la mayoría consulta sin ser cliente todavía
/// ("muchos consultan solo para saber"). Si después confirma, se da de alta el cliente y
/// como el cliente se vincula al teléfono, queda todo junto solo.
///
/// Los renglones guardan el NOMBRE y el PRECIO congelados del día que se cotizó: si en tres
/// meses la silla vale otra cosa, acá tiene que verse lo que se le presupuestó, no lo de hoy.
/// </summary>
[Table("Alq_Cotizaciones")]
public class AlqCotizacion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Teléfono del chat donde se cotizó (solo dígitos, como los guarda WhatsApp).</summary>
    [Required, MaxLength(30)]
    public string Telefono { get; set; } = string.Empty;

    /// <summary>Si el chat ya estaba vinculado a un cliente, queda anotado. Puede ser null.</summary>
    public int? ClienteId { get; set; }

    /// <summary>Día del evento que puso el operador al cotizar. Opcional.</summary>
    public DateTime? FechaEvento { get; set; }

    [MaxLength(200)]
    public string? FleteZona { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal FleteMonto { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Descuento { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Total { get; set; }

    /// <summary>El mensaje tal cual se le pasó al cliente, para poder repetirlo igual.</summary>
    [MaxLength(4000)]
    public string? Texto { get; set; }

    /// <summary>Quién la hizo (operador del PIN, si había).</summary>
    [MaxLength(60)]
    public string? Operador { get; set; }

    /// <summary>Se marca cuando esta cotización se convirtió en una reserva de verdad.</summary>
    public int? ReservaId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<AlqCotizacionItem> Items { get; set; } = new List<AlqCotizacionItem>();
}

[Table("Alq_CotizacionItems")]
public class AlqCotizacionItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int CotizacionId { get; set; }

    [ForeignKey(nameof(CotizacionId))]
    public AlqCotizacion? CotizacionNav { get; set; }

    /// <summary>Puede quedar en null si el equipo se borró del catálogo: el nombre igual queda guardado.</summary>
    public int? EquipoId { get; set; }

    /// <summary>Nombre congelado del día que se cotizó.</summary>
    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    public int Cantidad { get; set; }

    /// <summary>Precio unitario congelado del día que se cotizó.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal PrecioUnitario { get; set; }
}
