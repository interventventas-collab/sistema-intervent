using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Cajas")]
public class CafeCaja
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Nombre { get; set; } = string.Empty;

    /// <summary>EFECTIVO | BANCO | BILLETERA_VIRTUAL | CHEQUES_CARTERA | V_PRIVADO</summary>
    [Required, MaxLength(30)]
    public string Tipo { get; set; } = "EFECTIVO";

    [Column(TypeName = "decimal(18,2)")]
    public decimal SaldoInicial { get; set; }

    public int Orden { get; set; }
    public bool IsActive { get; set; } = true;

    [MaxLength(500)]
    public string? Notas { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
