using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("TreasuryAccounts")]
public class TreasuryAccount
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(30)]
    public string Code { get; set; } = string.Empty;

    [Required, MaxLength(150)]
    public string Name { get; set; } = string.Empty;

    // caja | banco | billetera | otro
    [Required, MaxLength(30)]
    public string AccountType { get; set; } = "caja";

    [Required, MaxLength(10)]
    public string Currency { get; set; } = "ARS";

    [Column(TypeName = "decimal(18,2)")]
    public decimal InitialBalance { get; set; }

    [MaxLength(500)]
    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

[Table("TreasuryMovements")]
public class TreasuryMovement
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int AccountId { get; set; }
    [ForeignKey(nameof(AccountId))]
    public TreasuryAccount? Account { get; set; }

    public DateTime Date { get; set; }

    // ingreso | egreso (las transferencias se guardan como dos filas vinculadas por TransferGroupId)
    [Required, MaxLength(20)]
    public string MovementType { get; set; } = "ingreso";

    [MaxLength(100)]
    public string? Concept { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Amount { get; set; }

    public int? RelatedAccountId { get; set; }
    public int? RelatedSaleId { get; set; }
    public int? RelatedEmployeeId { get; set; }
    public Guid? TransferGroupId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
