namespace Api.DTOs;

public record PostitDto(int Id, string Texto, string Color, string? CreadoPor, string Scope, DateTime CreatedAt, DateTime? UpdatedAt);

public class CreatePostitRequest
{
    public string Texto { get; set; } = string.Empty;
    public string? Color { get; set; }
    public string? CreadoPor { get; set; }
    public string? Scope { get; set; }
}

public class UpdatePostitRequest
{
    public string? Texto { get; set; }
    public string? Color { get; set; }
}

/// <summary>2026-09-17: los ids del tablero en el orden en que tienen que quedar (el primero arriba).</summary>
public class OrdenarPostitsRequest
{
    public string? Scope { get; set; }
    public List<int> Ids { get; set; } = new();
}
