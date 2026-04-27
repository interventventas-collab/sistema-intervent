using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record RoleDto(
    int Id,
    string Name,
    string? Description,
    DateTime CreatedAt,
    int UserCount,
    List<string> Permissions
);

public record CreateRoleRequest(
    [Required][MaxLength(50)] string Name,
    [MaxLength(255)] string? Description,
    List<string>? Permissions
);

public record UpdateRoleRequest(
    [MaxLength(50)] string? Name,
    [MaxLength(255)] string? Description,
    List<string>? Permissions
);

public record MenuTreeDto(
    string GroupKey,
    string Label,
    List<MenuItemDto> Items
);

public record MenuItemDto(
    string Key,
    string Label,
    string Route
);
