using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record UserListDto(
    int Id,
    string Username,
    string Email,
    string? FirstName,
    string? LastName,
    string? Phone,
    string Role,
    int RoleId,
    DateTime CreatedAt,
    bool IsActive
);

public record CreateUserRequest(
    [Required][MaxLength(100)] string Username,
    [Required][EmailAddress][MaxLength(255)] string Email,
    [Required][MinLength(6)] string Password,
    [MaxLength(100)] string? FirstName,
    [MaxLength(100)] string? LastName,
    [MaxLength(50)] string? Phone,
    [Required] int RoleId
);

public record UpdateUserRequest(
    [MaxLength(100)] string? FirstName,
    [MaxLength(100)] string? LastName,
    [EmailAddress][MaxLength(255)] string? Email,
    [MaxLength(50)] string? Phone,
    int? RoleId,
    bool? IsActive
);
