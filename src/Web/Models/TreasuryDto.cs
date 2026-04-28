namespace Web.Models;

public class TreasuryAccountDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string AccountType { get; set; } = "caja";
    public string Currency { get; set; } = "ARS";
    public decimal InitialBalance { get; set; }
    public decimal CurrentBalance { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateTreasuryAccountRequest
{
    public string? Code { get; set; }
    public string Name { get; set; } = string.Empty;
    public string AccountType { get; set; } = "caja";
    public string? Currency { get; set; }
    public decimal InitialBalance { get; set; }
    public string? Notes { get; set; }
}

public class UpdateTreasuryAccountRequest
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? AccountType { get; set; }
    public string? Currency { get; set; }
    public decimal? InitialBalance { get; set; }
    public string? Notes { get; set; }
    public bool? IsActive { get; set; }
}

public class TreasuryMovementDto
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public DateTime Date { get; set; }
    public string MovementType { get; set; } = "ingreso";
    public string? Concept { get; set; }
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public int? RelatedAccountId { get; set; }
    public string? RelatedAccountName { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateTreasuryMovementRequest
{
    public int AccountId { get; set; }
    public DateTime? Date { get; set; }
    public string MovementType { get; set; } = "ingreso";
    public string? Concept { get; set; }
    public string? Description { get; set; }
    public decimal Amount { get; set; }
    public int? RelatedAccountId { get; set; }
}
