using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record ProfileDto(
    int Id,
    string Username,
    string Email,
    string? FirstName,
    string? LastName,
    string? Phone,
    string Role,
    DateTime CreatedAt
);

public record UpdateProfileRequest(
    [MaxLength(100)] string? FirstName,
    [MaxLength(100)] string? LastName,
    [EmailAddress][MaxLength(255)] string? Email,
    [MaxLength(50)] string? Phone
);

public record ChangePasswordRequest(
    [Required] string CurrentPassword,
    [Required][MinLength(6)] string NewPassword
);
