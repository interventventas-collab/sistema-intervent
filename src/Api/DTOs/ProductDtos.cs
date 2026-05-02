using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record ProductAccountLinkDto(string Nickname, int Count);

public record ProductListDto(
    int Id,
    string Title,
    string? DisplayName,
    string? Description,
    string? Brand,
    string? Model,
    string? Sku,
    string? Barcode,
    string? OemCode,
    string? ImageUrl,
    string? Photo1,
    string? Photo2,
    string? Photo3,
    decimal CostPrice,
    decimal RetailPrice,
    decimal? RetailPrice2,
    decimal? Pvp3MarkupPercent,
    decimal? VatRate,
    string? PurchaseAccount,
    string? SaleAccount,
    string? InventoryAccount,
    decimal Stock,
    int CriticalStock,
    string StockUnit,
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
    bool RequiresExpiry,
    bool IsBase,
    bool IsService,
    int? UnitsPerPack,
    decimal Fraction,
    decimal MarkupAmount
);

public record CreateProductRequest(
    [Required][MaxLength(200)] string Title,
    [MaxLength(200)] string? DisplayName,
    string? Description,
    [MaxLength(100)] string? Brand,
    [MaxLength(100)] string? Model,
    [MaxLength(100)] string? Sku,
    [MaxLength(50)] string? Barcode,
    [MaxLength(100)] string? OemCode,
    [MaxLength(1000)] string? ImageUrl,
    string? Photo1,
    string? Photo2,
    string? Photo3,
    decimal CostPrice,
    decimal RetailPrice,
    decimal? RetailPrice2,
    decimal? Pvp3MarkupPercent,
    decimal? VatRate,
    [MaxLength(100)] string? PurchaseAccount,
    [MaxLength(100)] string? SaleAccount,
    [MaxLength(100)] string? InventoryAccount,
    decimal Stock,
    int CriticalStock,
    string StockUnit,
    int? BaseProductId,
    int? BrandId,
    bool? IsBase,
    bool? IsService,
    int? UnitsPerPack,
    decimal? Fraction,
    decimal? MarkupAmount
);

public record UpdateProductRequest(
    [MaxLength(200)] string? Title,
    [MaxLength(200)] string? DisplayName,
    string? Description,
    [MaxLength(100)] string? Brand,
    [MaxLength(100)] string? Model,
    [MaxLength(100)] string? Sku,
    [MaxLength(50)] string? Barcode,
    [MaxLength(100)] string? OemCode,
    [MaxLength(1000)] string? ImageUrl,
    string? Photo1,
    string? Photo2,
    string? Photo3,
    decimal? CostPrice,
    decimal? RetailPrice,
    decimal? RetailPrice2,
    decimal? Pvp3MarkupPercent,
    bool? ClearRetailPrice2,           // pasar true para vaciar PVP 2 explicito
    bool? ClearPvp3MarkupPercent,
    decimal? VatRate,
    [MaxLength(100)] string? PurchaseAccount,
    [MaxLength(100)] string? SaleAccount,
    [MaxLength(100)] string? InventoryAccount,
    decimal? Stock,
    int? CriticalStock,
    [MaxLength(10)] string? StockUnit,
    bool? IsActive,
    int? BaseProductId,
    bool? ClearBaseProduct,
    int? BrandId,
    bool? ClearBrand,
    bool? IsBase,
    bool? IsService,
    int? UnitsPerPack,
    bool? ClearUnitsPerPack,
    decimal? Fraction,
    decimal? MarkupAmount
);

public record BulkProductIdsRequest(List<int> Ids);

/// <summary>
/// Crea una variedad nueva de cafe (u otro producto fraccionable): genera el
/// producto padre + 3 hijos (1 kg, 1/2 kg, 1/4 kg) en una sola operacion.
/// Pensado para que el usuario cargue rapido cafes nuevos sin repetir el flujo
/// de "crear padre, despues crear hijo, despues otro hijo, otro hijo, etc".
/// </summary>
public record CreateCoffeeVarietyRequest(
    [Required][MaxLength(150)] string Name,            // Ej: "Cafe Brasil Seleccion"
    [Required][MaxLength(20)] string SkuBase,          // Ej: "F2" -> hijos quedan F2-1KG, F2-500G, F2-250G
    int? BrandId,
    [Range(0, double.MaxValue)] decimal CostPerKg,     // sin IVA, costo del kilo
    [Range(0, double.MaxValue)] decimal RetailPerKg,   // sin IVA, PVP del kilo
    [Range(0, double.MaxValue)] decimal MarkupAmount,  // recargo $ por fraccionar (default 1000). Aplica a 1/2 y 1/4
    decimal? VatRate,                                  // default 21
    int InitialStock1Kg,
    int InitialStock500g,
    int InitialStock250g
);

public record CreateCoffeeVarietyResponse(
    int ParentId,
    string ParentSku,
    int Child1KgId,
    int Child500gId,
    int Child250gId
);

public record BulkToggleStatusRequest(List<int> Ids, bool IsActive);

// Resultado de un upsert (Create que puede convertirse en Update si el SKU ya existe).
// Action puede ser "created" o "updated". PriceWarning se setea si bajo el precio.
public record ProductUpsertResult(
    ProductListDto Product,
    string Action,
    string? PriceWarning
);
