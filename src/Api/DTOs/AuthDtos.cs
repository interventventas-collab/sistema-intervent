using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record LoginRequest(
    [Required] string Username,
    [Required] string Password
);

public record RegisterRequest(
    [Required][MaxLength(100)] string Username,
    [Required][EmailAddress][MaxLength(255)] string Email,
    [Required][MinLength(6)] string Password
);

public record AuthResponse(
    string Token,
    string Username,
    string Role,
    DateTime ExpiresAt,
    List<string> Permissions
);

public record UserDto(
    int Id,
    string Username,
    string Email,
    string Role,
    DateTime CreatedAt,
    bool IsActive
);
