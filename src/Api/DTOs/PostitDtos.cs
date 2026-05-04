namespace Api.DTOs;

public record PostitDto(int Id, string Texto, string Color, string? CreadoPor, DateTime CreatedAt, DateTime? UpdatedAt);

public class CreatePostitRequest
{
    public string Texto { get; set; } = string.Empty;
    public string? Color { get; set; }
    public string? CreadoPor { get; set; }
}

public class UpdatePostitRequest
{
    public string? Texto { get; set; }
    public string? Color { get; set; }
}
