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

// --- Arreglo masivo de fotos en infracción ---
public class FixInfractionPreviewItem
{
    public string MeliItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Accion { get; set; } = "";  // "quitar" | "apartar" | "ya_ok"
    public string Reason { get; set; } = "";
    public int RemoveCount { get; set; }
    public int RemainingCount { get; set; }
    public List<string> KeepPictureIds { get; set; } = new();
}

public class FixInfractionPreview
{
    public List<FixInfractionPreviewItem> Items { get; set; } = new();
    public int Quitar { get; set; }
    public int Apartar { get; set; }
    public int YaOk { get; set; }
}

public class ApplyFixResult
{
    public int Ok { get; set; }
    public int Error { get; set; }
    public List<string> Errores { get; set; } = new();
}
