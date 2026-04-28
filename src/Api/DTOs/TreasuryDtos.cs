using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record TreasuryAccountDto(
    int Id,
    string Code,
    string Name,
    string AccountType,
    string Currency,
    decimal InitialBalance,
    decimal CurrentBalance,
    string? Notes,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateTreasuryAccountRequest(
    [MaxLength(30)] string? Code,
    [Required, MaxLength(150)] string Name,
    [Required, MaxLength(30)] string AccountType,
    [MaxLength(10)] string? Currency,
    decimal InitialBalance,
    [MaxLength(500)] string? Notes
);

public record UpdateTreasuryAccountRequest(
    [MaxLength(30)] string? Code,
    [MaxLength(150)] string? Name,
    [MaxLength(30)] string? AccountType,
    [MaxLength(10)] string? Currency,
    decimal? InitialBalance,
    [MaxLength(500)] string? Notes,
    bool? IsActive
);

public record TreasuryMovementDto(
    int Id,
    int AccountId,
    string AccountName,
    DateTime Date,
    string MovementType,
    string? Concept,
    string? Description,
    decimal Amount,
    int? RelatedAccountId,
    string? RelatedAccountName,
    DateTime CreatedAt
);

public record CreateTreasuryMovementRequest(
    [Required] int AccountId,
    DateTime? Date,
    [Required, MaxLength(20)] string MovementType,
    [MaxLength(100)] string? Concept,
    [MaxLength(500)] string? Description,
    [Range(0.01, double.MaxValue)] decimal Amount,
    int? RelatedAccountId   // si MovementType == "transferencia"
);
