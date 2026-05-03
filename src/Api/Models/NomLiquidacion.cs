using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Nom_Liquidaciones")]
public class NomLiquidacion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int EmpleadoId { get; set; }

    [ForeignKey(nameof(EmpleadoId))]
    public NomEmpleado? EmpleadoNav { get; set; }

    public int Anio { get; set; }
    public int Mes { get; set; }

    // Insumos del mes
    [Column(TypeName = "decimal(8,2)")]
    public decimal HorasTrabajadas { get; set; }

    [Column(TypeName = "decimal(8,2)")]
    public decimal HorasExtra { get; set; }

    [Column(TypeName = "decimal(8,2)")]
    public decimal RecargoHsExtraPct { get; set; } = 50m;

    [Column(TypeName = "decimal(5,2)")]
    public decimal DiasAusencia { get; set; }

    [Column(TypeName = "decimal(5,2)")]
    public decimal DiasVacaciones { get; set; }

    // Conceptos calculados
    [Column(TypeName = "decimal(18,2)")]
    public decimal SueldoBase { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal MontoHsExtra { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Comision { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Bonos { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal DescuentoFaltas { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Adelantos { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OtrosDescuentos { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalGanado { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalDescuentos { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NetoAPagar { get; set; }

    [MaxLength(20)]
    public string Estado { get; set; } = "pendiente";

    [MaxLength(1000)]
    public string? Notas { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<NomPago> Pagos { get; set; } = new List<NomPago>();
}

[Table("Nom_Pagos")]
public class NomPago
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int LiquidacionId { get; set; }

    [ForeignKey(nameof(LiquidacionId))]
    public NomLiquidacion? LiquidacionNav { get; set; }

    public DateTime FechaPago { get; set; }

    [Required, MaxLength(50)]
    public string Metodo { get; set; } = "efectivo";

    [Column(TypeName = "decimal(18,2)")]
    public decimal Monto { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
