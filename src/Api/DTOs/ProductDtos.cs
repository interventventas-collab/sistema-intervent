using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record ProductAccountLinkDto(string Nickname, int Count);

public record ProductListDto(
    int Id,
    string Title,
    string? Description,
    string? Brand,
    string? Model,
    string? Sku,
    string? Photo1,
    string? Photo2,
    string? Photo3,
    decimal CostPrice,
    decimal RetailPrice,
    int Stock,
    int CriticalStock,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    List<ProductAccountLinkDto> LinkedAccounts,
    int? BaseProductId,
    string? BaseProductSku,
    string? BaseProductTitle,
    int DerivedCount,
    int? BrandId,
    string? BrandName,
    bool RequiresExpiry
);

public record CreateProductRequest(
    [Required][MaxLength(200)] string Title,
    string? Description,
    [MaxLength(100)] string? Brand,
    [MaxLength(100)] string? Model,
    [MaxLength(100)] string? Sku,
    string? Photo1,
    string? Photo2,
    string? Photo3,
    decimal CostPrice,
    decimal RetailPrice,
    int Stock,
    int CriticalStock,
    int? BaseProductId,
    int? BrandId
);

public record UpdateProductRequest(
    [MaxLength(200)] string? Title,
    string? Description,
    [MaxLength(100)] string? Brand,
    [MaxLength(100)] string? Model,
    [MaxLength(100)] string? Sku,
    string? Photo1,
    string? Photo2,
    string? Photo3,
    decimal? CostPrice,
    decimal? RetailPrice,
    int? Stock,
    int? CriticalStock,
    bool? IsActive,
    int? BaseProductId,
    bool? ClearBaseProduct,
    int? BrandId,
    bool? ClearBrand
);

public record BulkProductIdsRequest(List<int> Ids);

public record BulkToggleStatusRequest(List<int> Ids, bool IsActive);
