namespace Web.Models.Mobile;

/// <summary>
/// Item del catálogo unificado (producto o combo) tal como lo consume el componente
/// ProductSearchInput. Se carga UNA vez al iniciar la app y queda en memoria del WASM.
/// </summary>
public class CatalogItem
{
    public string Sku { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "";    // "producto" | "combo"
    public int Id { get; set; }
    public string? Categoria { get; set; }
    public decimal Stock { get; set; }
    public bool EsCafe { get; set; }
    public DateTime? StockChangedAt { get; set; }

    // Campos pre-calculados al cargar (para ranking rápido)
    public string SkuUpper { get; set; } = "";
    public string NombreUpper { get; set; } = "";
}

/// <summary>Resultado de una búsqueda con score + tipo de match (para debug en UI).</summary>
public class SearchHit
{
    public CatalogItem Item { get; set; } = default!;
    public int Score { get; set; }
    public string MatchType { get; set; } = "";  // exact-sku, prefix-sku, contains-sku, contains-nombre, fuzzy
}
