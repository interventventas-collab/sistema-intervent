using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Employees")]
public class Employee
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(30)]
    public string Code { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string FirstName { get; set; } = string.Empty;

    [Required, MaxLength(100)]
    public string LastName { get; set; } = string.Empty;

    [MaxLength(20)]
    public string? Dni { get; set; }

    [MaxLength(20)]
    public string? Cuil { get; set; }

    [MaxLength(100)]
    public string? Position { get; set; }

    [Column(TypeName = "date")]
    public DateTime? HireDate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal BaseSalary { get; set; }

    [MaxLength(100)]
    public string? Bank { get; set; }

    [MaxLength(30)]
    public string? Cbu { get; set; }

    [MaxLength(50)]
    public string? Phone { get; set; }

    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(500)]
    public string? Address { get; set; }

    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

[Table("PayrollPayments")]
public class PayrollPayment
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int PayrollId { get; set; }
    [ForeignKey(nameof(PayrollId))]
    public Payroll? Payroll { get; set; }

    public DateTime Date { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public int? AccountId { get; set; }
    [ForeignKey(nameof(AccountId))]
    public TreasuryAccount? Account { get; set; }

    // efectivo | transferencia | tarjeta_debito | tarjeta_credito | cheque | mercadopago | otro
    [Required, MaxLength(30)]
    public string PaymentMethod { get; set; } = "efectivo";

    [MaxLength(100)]
    public string? Concept { get; set; }   // ej. "Adelanto", "Pago final", "Quincena"

    [MaxLength(500)]
    public string? Notes { get; set; }

    public int? TreasuryMovementId { get; set; }   // referencia al movimiento generado en tesoreria

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("Payrolls")]
public class Payroll
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int EmployeeId { get; set; }
    [ForeignKey(nameof(EmployeeId))]
    public Employee? Employee { get; set; }

    public int Year { get; set; }
    public int Month { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal BaseSalary { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Bonuses { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Deductions { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal GrossTotal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal NetTotal { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public bool IsPaid { get; set; }
    public DateTime? PaidAt { get; set; }

    public int? PaidFromAccountId { get; set; }
    [ForeignKey(nameof(PaidFromAccountId))]
    public TreasuryAccount? PaidFromAccount { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<PayrollPayment> Payments { get; set; } = new List<PayrollPayment>();
}
