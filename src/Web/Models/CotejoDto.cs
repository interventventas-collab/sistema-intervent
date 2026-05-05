namespace Web.Models;

public class CotejoImportResultDto
{
    public int Productos { get; set; }
    public int Combos { get; set; }
    public int ComboItems { get; set; }
    public string? ProductosFile { get; set; }
    public string? CombosFile { get; set; }
    public List<string> Warnings { get; set; } = new();
}

public class CotejoResumenDto
{
    public int TotalPublicaciones { get; set; }
    public int SinSku { get; set; }
    public int Producto { get; set; }
    public int Combo { get; set; }
    public int Huerfano { get; set; }
    public int CombosConComponentesFaltantes { get; set; }
    public int ProductosCargados { get; set; }
    public int CombosCargados { get; set; }
}

public class CotejoFilaDto
{
    public int MeliRowId { get; set; }
    public string MeliItemId { get; set; } = "";
    public string? VariationId { get; set; }
    public string? VariationAttributes { get; set; }
    public string Title { get; set; } = "";
    public string? Sku { get; set; }
    public string Categoria { get; set; } = ""; // producto / combo / huerfano / sin_sku
    public decimal Price { get; set; }
    public int AvailableQuantity { get; set; }
    public string? ContabNombre { get; set; }
    public decimal? ContabPrecioFinal { get; set; }
    public decimal? ContabStock { get; set; }
    public bool ComboTieneFaltantes { get; set; }
    public int? ProductIdVinculado { get; set; }
    public int? ComboIdVinculado { get; set; }
}

public class ComboComponenteDto
{
    public string SkuComponente { get; set; } = "";
    public string? NombreComponente { get; set; }
    public decimal Cantidad { get; set; }
    public bool ExisteEnContabilium { get; set; }
    public decimal? StockComponente { get; set; }
}

public class ComboDetalleDto
{
    public string SkuCombo { get; set; } = "";
    public string? Nombre { get; set; }
    public decimal? PrecioFinal { get; set; }
    public List<ComboComponenteDto> Componentes { get; set; } = new();
    public int FaltantesCount { get; set; }
}
