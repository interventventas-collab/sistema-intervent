using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record SupplierPriceListDto(
    int Id,
    string Name,
    int? SupplierId,
    string? SupplierName,
    string? Notes,
    DateTime? LastUploadAt,
    int ItemsCount,
    int LinkedProductsCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record SupplierPriceListItemDto(
    int Id,
    int PriceListId,
    string Code,
    string? Description,
    decimal CostPrice,
    decimal? SuggestedRetailPrice,
    string? Notes,
    int LinkedProductsCount,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateSupplierPriceListRequest(
    [Required, MaxLength(150)] string Name,
    int? SupplierId,
    [MaxLength(500)] string? Notes
);

public record UpdateSupplierPriceListRequest(
    [MaxLength(150)] string? Name,
    int? SupplierId,
    [MaxLength(500)] string? Notes
);

public record CreatePriceListItemRequest(
    [Required, MaxLength(100)] string Code,
    [MaxLength(500)] string? Description,
    decimal CostPrice,
    decimal? SuggestedRetailPrice,
    [MaxLength(500)] string? Notes
);

public record UpdatePriceListItemRequest(
    [MaxLength(100)] string? Code,
    [MaxLength(500)] string? Description,
    decimal? CostPrice,
    decimal? SuggestedRetailPrice,
    [MaxLength(500)] string? Notes
);

public record PriceListImportResult(
    int TotalRows,
    int Created,
    int Updated,
    int Skipped,
    int LinkedProductsUpdated,
    List<string> Errors
);
