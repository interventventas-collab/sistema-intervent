namespace Web.Models;

// --- Gestion de fotos de publicaciones existentes (Etapa 1) ---
public class MeliPictureDto
{
    public string Id { get; set; } = "";
    public string Url { get; set; } = "";
}

public class MeliItemPicturesDto
{
    public List<MeliPictureDto> Pictures { get; set; } = new();
    public bool CatalogListing { get; set; }
    public string? Permalink { get; set; }
}

/// <summary>Una foto de la lista final. Se usa UNA de las tres: Id (foto existente que se conserva),
/// Source (URL externa nueva) o DataUri (archivo subido en base64).</summary>
public class PictureSpec
{
    public string? Id { get; set; }
    public string? Source { get; set; }
    public string? DataUri { get; set; }
}

public class UpdateItemPicturesRequest
{
    public List<PictureSpec> Pictures { get; set; } = new();
}

// --- Detección de fotos en infracción (Etapa 1.5) ---
public class PhotoInfractionDto
{
    public string MeliItemId { get; set; } = "";
    public string Reason { get; set; } = "";
    public bool PhotoRelated { get; set; }
    public List<string> PictureIds { get; set; } = new();
    public bool AfectaPortada { get; set; }
}

public class ScanPhotoInfractionsResult
{
    public int TotalInfractions { get; set; }
    public int Matched { get; set; }
    public List<PhotoInfractionDto> Items { get; set; } = new();
    public Dictionary<string, int> Breakdown { get; set; } = new();
}

public class PictureDiagnosisDto
{
    public bool HasIssues { get; set; }
    public List<string> Issues { get; set; } = new();
}
