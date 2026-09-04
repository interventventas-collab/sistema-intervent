namespace Api.DTOs;

/// <summary>2026-09-04 — Datos de la pantalla "Clonar publicación" (/meli/clonar).</summary>
public class ClonAtributoDto
{
    public string Id { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? ValueId { get; set; }
    public string? ValueName { get; set; }
}

/// <summary>2026-09-04: una publicación que compite por el mismo producto de catálogo.</summary>
public class ClonCompetidorDto
{
    public string MeliItemId { get; set; } = "";
    public long SellerId { get; set; }
    public decimal Precio { get; set; }
    public string? ListingTypeId { get; set; }
    /// <summary>True si esa publicación es de una de nuestras cuentas.</summary>
    public bool EsMio { get; set; }
}

/// <summary>Todo lo que se pudo leer de la publicación original, listo para editar y publicar.</summary>
public class ClonPreviewDto
{
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

    /// <summary>Publicación de catálogo: en MeLi conviene competir en el mismo catálogo.</summary>
    public bool EsCatalogo { get; set; }
    public string? CatalogProductId { get; set; }
    /// <summary>Cantidad de variantes del original. Esta versión NO las clona.</summary>
    public int CantidadVariantes { get; set; }

    /// <summary>
    /// 2026-09-04: los datos NO salieron de la publicación de un vendedor (MeLi lo prohíbe) sino de
    /// la FICHA DE CATÁLOGO de MercadoLibre. Es el camino válido para clonar algo de otro.
    /// </summary>
    public bool DesdeCatalogo { get; set; }
    /// <summary>Los que ya venden ese producto de catálogo, del más barato al más caro.</summary>
    public List<ClonCompetidorDto> Competidores { get; set; } = new();

    /// <summary>True si la publicación es de una de nuestras cuentas.</summary>
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
    /// <summary>MLA de la publicación de la que se clonó (para copiar el vínculo de stock).</summary>
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
    /// <summary>Copiar del original el producto vinculado y la receta, así comparten stock.</summary>
    public bool CopiarVinculoStock { get; set; }
}

public class ClonDuplicarRequest
{
    public string MeliItemId { get; set; } = "";
    /// <summary>0 = "la otra cuenta" (si hay exactamente dos conectadas).</summary>
    public int CuentaDestinoId { get; set; }
    public decimal? Precio { get; set; }
    public int? Stock { get; set; }
    public string? ListingTypeId { get; set; }
    /// <summary>Publicar igual aunque en la cuenta destino ya haya una parecida.</summary>
    public bool Forzar { get; set; }
}

public class ClonPublicarResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    public string? MeliItemId { get; set; }
    public string? Permalink { get; set; }
    public string? CuentaDestinoNombre { get; set; }
    /// <summary>True si además se copió el vínculo con el producto del sistema (stock compartido).</summary>
    public bool VinculoCopiado { get; set; }
    /// <summary>True cuando se frenó porque en la cuenta destino ya hay una publicación parecida.</summary>
    public bool YaExiste { get; set; }
}
