namespace Web.Models;

// 2026-08-04: Salud / infracciones de una publicación (por qué no está activa y qué hacer).
public class MeliSaludItemDto
{
    public string MeliItemId { get; set; } = "";
    public int AccountId { get; set; }
    public string AccountNickname { get; set; } = "";
    public string Title { get; set; } = "";
    public string Status { get; set; } = "";
    public string StatusLabel { get; set; } = "";
    public List<string> SubStatus { get; set; } = new();
    public string Motivo { get; set; } = "";
    public string QueHacer { get; set; } = "";
    public double? Health { get; set; }
    public string? Permalink { get; set; }
    public string? Thumbnail { get; set; }
    public int AvailableQuantity { get; set; }
}

public class MeliSaludResponse
{
    public int TotalRevisadas { get; set; }
    public int ConProblemas { get; set; }
    public List<MeliSaludItemDto> Items { get; set; } = new();
    public List<string> Errores { get; set; } = new();
}
