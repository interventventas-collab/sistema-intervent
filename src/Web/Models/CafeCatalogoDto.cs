namespace Web.Models;

// 2026-08-01: Catalogo unificado — un item de la lista unica de codigos.
public class CafeCatalogoItemDto
{
    public int Id { get; set; }
    public string Tipo { get; set; } = "";     // producto | combo | compuesto | kit | servicio
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public string? Categoria { get; set; }
    public int? Stock { get; set; }
    public bool StockEsArmable { get; set; }
    public bool IsActive { get; set; }
    public string? Detalle { get; set; }
    public string EditRoute { get; set; } = "";
}
