namespace Web.Models;

public class PostitDto
{
    public int Id { get; set; }
    public string Texto { get; set; } = "";
    public string Color { get; set; } = "amarillo";
    public string? CreadoPor { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreatePostitRequest
{
    public string Texto { get; set; } = "";
    public string? Color { get; set; }
    public string? CreadoPor { get; set; }
}

public class UpdatePostitRequest
{
    public string? Texto { get; set; }
    public string? Color { get; set; }
}
