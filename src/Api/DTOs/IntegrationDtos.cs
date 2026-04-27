using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record IntegrationDto(
    int Id,
    string Provider,
    string? AppId,
    bool HasSecret,
    string? RedirectUrl,
    string? Settings,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record SaveIntegrationRequest(
    [Required][MaxLength(50)] string Provider,
    [MaxLength(255)] string? AppId,
    [MaxLength(255)] string? AppSecret,
    [MaxLength(500)] string? RedirectUrl,
    string? Settings,
    bool IsActive
);
