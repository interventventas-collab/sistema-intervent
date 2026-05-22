using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Cheques")]
public class CafeCheque
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string Numero { get; set; } = string.Empty;

    [Required, MaxLength(150)]
    public string Banco { get; set; } = string.Empty;

    /// <summary>FK opcional al catalogo Cafe_Bancos. Cuando esta cargado, el campo Banco
    /// queda como snapshot del nombre al momento de la creacion (compat con cheques viejos).
    /// La UI nueva siempre setea BancoId; las queries que muestran el banco prefieren
    /// BancoNav?.Alias ?? BancoNav?.Nombre ?? Banco.</summary>
    public int? BancoId { get; set; }
    [ForeignKey(nameof(BancoId))]
    public CafeBanco? BancoNav { get; set; }

    [MaxLength(200)]
    public string? Emisor { get; set; }

    public int? ClienteOrigenId { get; set; }
    [ForeignKey(nameof(ClienteOrigenId))]
    public CafeCliente? ClienteOrigen { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }

    [Column(TypeName = "date")]
    public DateTime? FechaCobro { get; set; }

    [Column(TypeName = "date")]
    public DateTime? FechaVencimiento { get; set; }

    /// <summary>EN_CARTERA | DEPOSITADO | ACREDITADO | COBRADO_VENTANILLA | ENDOSADO | RECHAZADO</summary>
    [Required, MaxLength(30)]
    public string Estado { get; set; } = "EN_CARTERA";

    public DateTime? FechaCambioEstado { get; set; }

    [MaxLength(500)]
    public string? Observaciones { get; set; }

    /// <summary>Cobranza que origino este cheque (cuando se cobro a un cliente).</summary>
    public int? CobranzaOrigenId { get; set; }

    /// <summary>Si el cheque fue endosado a un proveedor (estado ENDOSADO).</summary>
    public int? ProveedorEndosoId { get; set; }

    /// <summary>Pago a proveedor que origino el endoso (si aplica).</summary>
    public int? PagoOrigenId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
