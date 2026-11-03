namespace Web.Models;

/// <summary>2026-11-03 — Espejo de /api/meli/publis-producto: en qué publicaciones de MeLi está
/// un producto (suelta, pack del mismo o combo con otros).</summary>
public class MeliPubliDeProductoDto
{
    public string MeliItemId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Thumbnail { get; set; }
    public string? Permalink { get; set; }
    public decimal Price { get; set; }
    public string Status { get; set; } = "";
    public string? LogisticType { get; set; }
    public int AvailableQuantity { get; set; }
    public int SoldQuantity { get; set; }
    public string? Sku { get; set; }
    public string? ListingTypeId { get; set; }
    public decimal Cantidad { get; set; }
    /// <summary>"suelta" | "pack" | "combo".</summary>
    public string Tipo { get; set; } = "";
    public string? Variante { get; set; }

    public int CantidadInt => (int)Math.Round(Cantidad);
    public bool Activa => Status == "active";
    public string Link => string.IsNullOrEmpty(Permalink)
        ? $"https://articulo.mercadolibre.com.ar/{MeliItemId.Replace("MLA", "MLA-")}" : Permalink;
    public string TipoTexto => Tipo switch
    {
        "suelta" => "suelta",
        "pack" => $"pack x{CantidadInt}",
        _ => CantidadInt > 1 ? $"combo · lleva {CantidadInt}" : "combo",
    };
    public string Envio => LogisticType switch
    {
        "fulfillment" => "Full",
        "self_service" => "Flex",
        null or "" or "not_specified" => "",
        _ => "Propio",
    };
}

public class MeliPublisDeProductoDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public int Activas { get; set; }
    public int Pausadas { get; set; }
    public List<MeliPubliDeProductoDto> Publis { get; set; } = new();
}

public class MeliProductoCandidatoDto
{
    public int Id { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
}

public class MeliPublisBusquedaDto
{
    public MeliPublisDeProductoDto? Producto { get; set; }
    public List<MeliProductoCandidatoDto> Candidatos { get; set; } = new();
}

public class MeliPublisConteoDto
{
    public int ProductoId { get; set; }
    public int Activas { get; set; }
    public int Pausadas { get; set; }
}
