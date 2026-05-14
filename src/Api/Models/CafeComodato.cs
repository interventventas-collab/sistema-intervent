using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Máquina de café colocada en un cliente. Dos modalidades:
///   COMODATO   → máquina nuestra, cliente la usa, no nos paga por ella.
///   FINANCIADA → cliente la compró, paga en cuotas. Al terminar es suya.
/// </summary>
[Table("Cafe_Comodatos")]
public class CafeComodato
{
    [Key] public int Id { get; set; }
    public int ClienteId { get; set; }
    [Required, MaxLength(20)] public string Modalidad { get; set; } = "COMODATO";
    [MaxLength(100)] public string? Marca { get; set; }
    [MaxLength(100)] public string? Modelo { get; set; }
    [MaxLength(100)] public string? NumeroSerie { get; set; }
    public DateTime? FechaEntrega { get; set; }
    [Required, MaxLength(20)] public string Estado { get; set; } = "EN_CLIENTE";
    public DateTime? FechaDevolucion { get; set; }
    [MaxLength(500)] public string? Notas { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? ValorEstimado { get; set; }

    // Solo para FINANCIADA
    [Column(TypeName = "decimal(18,2)")] public decimal? PrecioVenta { get; set; }
    public int? CuotasTotales { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? ValorCuota { get; set; }
    public int? DiaPagoMensual { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? SaldoFinanciamiento { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(ClienteId))] public CafeCliente? Cliente { get; set; }
    public List<CafeComodatoPago> Pagos { get; set; } = new();
}

[Table("Cafe_ComodatoPagos")]
public class CafeComodatoPago
{
    [Key] public int Id { get; set; }
    public int ComodatoId { get; set; }
    public DateTime Fecha { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Importe { get; set; }
    [MaxLength(30)] public string? MedioPago { get; set; }
    [MaxLength(300)] public string? Notas { get; set; }
    public DateTime CreatedAt { get; set; }

    [ForeignKey(nameof(ComodatoId))] public CafeComodato? Comodato { get; set; }
}
