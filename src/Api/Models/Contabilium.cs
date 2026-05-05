namespace Api.Models;

public class ContabProducto
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string? SkuPadre { get; set; }
    public string? Tipo { get; set; }
    public string? Nombre { get; set; }
    public string? Atributo1 { get; set; }
    public string? VarianteAtributo1 { get; set; }
    public string? Atributo2 { get; set; }
    public string? VarianteAtributo2 { get; set; }
    public string? CodigoBarras { get; set; }
    public string? CodigoOem { get; set; }
    public string? Estado { get; set; }
    public decimal? CostoInterno { get; set; }
    public decimal? Precio { get; set; }
    public decimal? Iva { get; set; }
    public decimal? PrecioFinal { get; set; }
    public decimal? Stock { get; set; }
    public string? Rubro { get; set; }
    public string? SubRubro { get; set; }
    public string? Proveedor { get; set; }
    public string? Descripcion { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}

public class ContabCombo
{
    public int Id { get; set; }
    public string SkuCombo { get; set; } = string.Empty;
    public string? Nombre { get; set; }
    public string? Descripcion { get; set; }
    public string? Estado { get; set; }
    public decimal? CostoInterno { get; set; }
    public decimal? Rentabilidad { get; set; }
    public decimal? PrecioUnitario { get; set; }
    public decimal? Iva { get; set; }
    public decimal? PrecioFinal { get; set; }
    public bool? PrecioAutomatico { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}

public class ContabComboItem
{
    public int Id { get; set; }
    public string SkuCombo { get; set; } = string.Empty;
    public string SkuComponente { get; set; } = string.Empty;
    public string? NombreComponente { get; set; }
    public decimal Cantidad { get; set; } = 1;
    public decimal? CostoInternoComponente { get; set; }
    public decimal? PrecioComponente { get; set; }
    public DateTime ImportedAt { get; set; } = DateTime.UtcNow;
}
