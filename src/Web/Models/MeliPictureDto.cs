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
