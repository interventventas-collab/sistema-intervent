namespace Web.Models;

public class GaliciaAccountDto
{
    public int Id { get; set; }
    public string Usuario { get; set; } = "";
    public string? Alias { get; set; }
    public bool HasPassword { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SaveGaliciaAccountRequest
{
    public string Usuario { get; set; } = "";
    public string? Password { get; set; }
    public string? Alias { get; set; }
    public bool IsActive { get; set; } = true;
}

public class GaliciaSincronizarResultDto
{
    public bool Ok { get; set; }
    public int Nuevos { get; set; }
    public int SinCambios { get; set; }
    public string? Error { get; set; }
    public List<string>? Detalles { get; set; }
}

public class GaliciaTestStatusDto
{
    public bool Running { get; set; }
    public string Step { get; set; } = "";
    public GaliciaTestResultDto? Result { get; set; }
}

public class GaliciaTestResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public bool? Submitted { get; set; }
    public bool? LoggedIn { get; set; }
    public bool? NeedsToken { get; set; }
    public string? Url { get; set; }
}
