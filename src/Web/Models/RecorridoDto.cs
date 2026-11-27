namespace Web.Models;

// 2026-11-27: "🧭 Recorrer estantes" del celu del depósito (/depo/recorrer).

public class RecorridoInicioDto
{
    public List<RecorridoOperadorDto> Operadores { get; set; } = new();
    public List<RecorridoCalleDto> Calles { get; set; } = new();
    public DateTime? Desde { get; set; }
}

public class RecorridoOperadorDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
}

public class RecorridoCalleDto
{
    public int Calle { get; set; }
    public int Racks { get; set; }
    public int Estantes { get; set; }
    public int Revisados { get; set; }
    public int Productos { get; set; }
    public string? Planta { get; set; }
    public string? Seguir { get; set; }
}

public class RecorridoEstanteDto
{
    public string Codigo { get; set; } = "";
    public string? Desglose { get; set; }
    public string? Planta { get; set; }
    public int RackId { get; set; }
    public string? Rack { get; set; }
    public int? Calle { get; set; }
    public int? Nro { get; set; }
    public int Cara { get; set; }
    public int Posicion { get; set; }
    public int Nivel { get; set; }
    public int Niveles { get; set; }
    public bool Revisado { get; set; }
    public List<RecorridoAlturaDto> Alturas { get; set; } = new();
    public string? Siguiente { get; set; }
    public bool CambiaRack { get; set; }
    public List<RecorridoProductoDto> Productos { get; set; } = new();
}

public class RecorridoAlturaDto
{
    public int Nivel { get; set; }
    public string Codigo { get; set; } = "";
    public bool Revisado { get; set; }
    public int Productos { get; set; }
}

public class RecorridoProductoDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public int Stock { get; set; }
    public int? Contado { get; set; }
    public string? ContadoPor { get; set; }
    public string? CantidadTexto { get; set; }
    public List<RecorridoOtroLugarDto> Otros { get; set; } = new();
    public List<RecorridoOtroLugarDto> Lugares { get; set; } = new();
}

public class RecorridoOtroLugarDto
{
    public string Codigo { get; set; } = "";
    public int? Contado { get; set; }
}

public class RecorridoContarResultDto
{
    public bool Ok { get; set; }
    public bool Nuevo { get; set; }
    public string? Codigo { get; set; }
    public int StockAntes { get; set; }
    public int StockDespues { get; set; }
    public bool Ajustado { get; set; }
    public string? Aviso { get; set; }
}
