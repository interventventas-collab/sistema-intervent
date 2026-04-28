using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record SupplierDto(
    int Id,
    string Name,
    string? Cuit,
    string? Phone,
    string? Email,
    string? Address,
    string? ContactName,
    string? Notes,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateSupplierRequest(
    [Required][MaxLength(200)] string Name,
    [MaxLength(20)] string? Cuit,
    [MaxLength(50)] string? Phone,
    [EmailAddress][MaxLength(255)] string? Email,
    [MaxLength(500)] string? Address,
    [MaxLength(150)] string? ContactName,
    string? Notes
);

public record UpdateSupplierRequest(
    [MaxLength(200)] string? Name,
    [MaxLength(20)] string? Cuit,
    [MaxLength(50)] string? Phone,
    [EmailAddress][MaxLength(255)] string? Email,
    [MaxLength(500)] string? Address,
    [MaxLength(150)] string? ContactName,
    string? Notes,
    bool? IsActive
);
