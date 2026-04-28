using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record BrandDto(
    int Id,
    string Name,
    string? Description,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateBrandRequest(
    [Required][MaxLength(150)] string Name,
    [MaxLength(500)] string? Description
);

public record UpdateBrandRequest(
    [MaxLength(150)] string? Name,
    [MaxLength(500)] string? Description,
    bool? IsActive
);
