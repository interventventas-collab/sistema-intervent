namespace Web.Models;

/// <summary>2026-11-12: cartel de "dar de baja" — qué publicaciones de MeLi se pausan.</summary>
public class ProductoBajaPubliDto
{
    public string MeliItemId { get; set; } = "";
    public string Titulo { get; set; } = "";
    public string Estado { get; set; } = "";
    public decimal Cantidad { get; set; }
    public bool EsPack { get; set; }
    public bool CompartidaConOtrosColores { get; set; }
}

public class ProductoBajaPreviewDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public int StockUnidades { get; set; }
    public decimal StockGramos { get; set; }
    public int StockEnFull { get; set; }
    public bool YaInactivo { get; set; }
    public List<ProductoBajaPubliDto> Publicaciones { get; set; } = new();
}

public class ProductoBajaPubliResultadoDto
{
    public string MeliItemId { get; set; } = "";
    public string Titulo { get; set; } = "";
    public bool Ok { get; set; }
    public string Mensaje { get; set; } = "";
}

public class ProductoBajaResultadoDto
{
    public bool Ok { get; set; }
    public string Mensaje { get; set; } = "";
    public int Pausadas { get; set; }
    public int YaPausadas { get; set; }
    public int Fallaron { get; set; }
    public List<ProductoBajaPubliResultadoDto> Detalle { get; set; } = new();
}
