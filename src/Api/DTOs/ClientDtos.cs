using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record ClientDto(
    int Id,
    string Code,
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

public record CreateClientRequest(
    [MaxLength(30)] string? Code,
    [Required][MaxLength(200)] string Name,
    [MaxLength(20)] string? Cuit,
    [MaxLength(50)] string? Phone,
    [EmailAddress][MaxLength(255)] string? Email,
    [MaxLength(500)] string? Address,
    [MaxLength(150)] string? ContactName,
    string? Notes
);

public record UpdateClientRequest(
    [MaxLength(30)] string? Code,
    [MaxLength(200)] string? Name,
    [MaxLength(20)] string? Cuit,
    [MaxLength(50)] string? Phone,
    [EmailAddress][MaxLength(255)] string? Email,
    [MaxLength(500)] string? Address,
    [MaxLength(150)] string? ContactName,
    string? Notes,
    bool? IsActive
);
