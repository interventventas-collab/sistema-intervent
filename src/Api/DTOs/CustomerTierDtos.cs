using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record CustomerTierDto(
    int Id,
    string Name,
    string Code,
    decimal AdjustmentPercent,
    bool IsDefault,
    bool IsActive,
    int SortOrder,
    string? Notes,
    int ClientCount,        // cuantos clientes apuntan a esta lista
    int OverrideCount,      // cuantas excepciones de precio tiene
    string? Companies       // CSV de empresas. null/vacio = visible en todas.
);

public record CreateCustomerTierRequest(
    [Required][MaxLength(60)] string Name,
    [Required][MaxLength(20)] string Code,
    [Range(-100, 1000)] decimal AdjustmentPercent,
    bool IsDefault,
    int SortOrder,
    [MaxLength(500)] string? Notes,
    [MaxLength(200)] string? Companies
);

public record UpdateCustomerTierRequest(
    [MaxLength(60)] string? Name,
    [Range(-100, 1000)] decimal? AdjustmentPercent,
    bool? IsDefault,
    bool? IsActive,
    int? SortOrder,
    [MaxLength(500)] string? Notes,
    [MaxLength(200)] string? Companies
);

/// <summary>
/// Precio calculado de un producto para una lista. Si Override = true significa
/// que viene de ProductPriceOverrides; si no, es el calculo automatico.
/// </summary>
public record ProductTierPriceDto(
    int CustomerTierId,
    string TierName,
    string TierCode,
    decimal AdjustmentPercent,
    decimal Price,            // sin IVA
    decimal PriceWithVat,     // con IVA aplicado
    bool Override,            // true si hay un ProductPriceOverride
    int? OverrideId,
    string? OverrideNotes
);

public record SetProductPriceOverrideRequest(
    [Required] int ProductId,
    [Required] int CustomerTierId,
    [Required] decimal Price, // sin IVA
    [MaxLength(300)] string? Notes
);
