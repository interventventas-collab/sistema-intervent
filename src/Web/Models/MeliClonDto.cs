namespace Web.Models;

/// <summary>2026-09-04 — Espejo de los DTOs de /api/meli/clon (pantalla "Clonar publicación").</summary>
public class ClonAtributoDto
{
    public string Id { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? ValueId { get; set; }
    public string? ValueName { get; set; }
}

/// <summary>2026-09-04: una publicación que ya compite por el mismo producto de catálogo.</summary>
public class ClonCompetidorDto
{
    public string MeliItemId { get; set; } = "";
    public long SellerId { get; set; }
    public decimal Precio { get; set; }
    public string? ListingTypeId { get; set; }
    public bool EsMio { get; set; }
}

public class ClonPreviewDto
{
    /// <summary>2026-09-04: los datos salieron de la FICHA DE CATÁLOGO de MeLi, no de la publicación de un vendedor.</summary>
    public bool DesdeCatalogo { get; set; }
    /// <summary>Los que ya venden ese producto, del más barato al más caro.</summary>
    public List<ClonCompetidorDto> Competidores { get; set; } = new();

    public bool Ok { get; set; }
    public string? Error { get; set; }

    public string MeliItemId { get; set; } = "";
    public string Titulo { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string? CategoriaNombre { get; set; }
    public string? Descripcion { get; set; }
    public decimal Precio { get; set; }
    public int Stock { get; set; }
    public string Condition { get; set; } = "new";
    public string ListingTypeId { get; set; } = "gold_special";
    public bool FreeShipping { get; set; }
    public string? Permalink { get; set; }
    public string? Thumbnail { get; set; }
    public string? Sku { get; set; }
    public int Vendidas { get; set; }

    public List<string> Fotos { get; set; } = new();
    public List<ClonAtributoDto> Atributos { get; set; } = new();

    public bool EsCatalogo { get; set; }
    public string? CatalogProductId { get; set; }
    public int CantidadVariantes { get; set; }

    public bool EsPropia { get; set; }
    public int? CuentaOrigenId { get; set; }
    public string? CuentaOrigenNombre { get; set; }
    public string? VendedorNickname { get; set; }
    public int? ProductoVinculadoId { get; set; }
    public string? ProductoVinculadoNombre { get; set; }
}

public class ClonPublicarRequest
{
    public int CuentaDestinoId { get; set; }
    public string? MeliItemOrigen { get; set; }
    public string Titulo { get; set; } = "";
    public string CategoryId { get; set; } = "";
    public string? Descripcion { get; set; }
    public decimal Precio { get; set; }
    public int Stock { get; set; } = 1;
    public string Condition { get; set; } = "new";
    public string ListingTypeId { get; set; } = "gold_special";
    public bool FreeShipping { get; set; } = true;
    public string? Sku { get; set; }
    public List<string> Fotos { get; set; } = new();
    public List<ClonAtributoDto> Atributos { get; set; } = new();
    public bool CopiarVinculoStock { get; set; }
}

public class ClonDuplicarRequest
{
    public string MeliItemId { get; set; } = "";
    public int CuentaDestinoId { get; set; }
    public decimal? Precio { get; set; }
    public int? Stock { get; set; }
    public string? ListingTypeId { get; set; }
    public bool Forzar { get; set; }
}

public class ClonPublicarResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? MeliItemId { get; set; }
    public string? Permalink { get; set; }
    public string? CuentaDestinoNombre { get; set; }
    public bool VinculoCopiado { get; set; }
    public bool YaExiste { get; set; }
}
