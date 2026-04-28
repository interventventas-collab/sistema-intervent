using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record BrandDto(
    int Id,
    string Code,
    string Name,
    string? Description,
    bool HasExpiry,
    string? Companies,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateBrandRequest(
    [MaxLength(30)] string? Code,
    [Required][MaxLength(150)] string Name,
    [MaxLength(500)] string? Description,
    bool? HasExpiry,
    [MaxLength(200)] string? Companies
);

public record UpdateBrandRequest(
    [MaxLength(30)] string? Code,
    [MaxLength(150)] string? Name,
    [MaxLength(500)] string? Description,
    bool? HasExpiry,
    [MaxLength(200)] string? Companies,
    bool? IsActive
);
