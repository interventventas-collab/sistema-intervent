namespace Web.Models;

// 2026-11-04: "Asignar vencimiento" (ver Api/Controllers/CafeVencimientosController.cs)
public class VencFilaDto
{
    public int Id { get; set; }
    public decimal Cantidad { get; set; }
    public DateTime FechaVence { get; set; }
    public decimal Quedan { get; set; }
}

public class VencProductoDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public string? Marca { get; set; }
    public string? Ubicacion { get; set; }
    public bool EsCafe { get; set; }
    public decimal Stock { get; set; }
    public List<VencFilaDto> Filas { get; set; } = new();
    public DateTime? ProximoVence { get; set; }
    public decimal ProximoQuedan { get; set; }
    public DateTime? SiguienteVence { get; set; }
    public decimal SinVencimiento { get; set; }
    public decimal Asignado { get; set; }
}

public class VencResumenDto
{
    public int ProductoId { get; set; }
    public DateTime? ProximoVence { get; set; }
    public decimal ProximoQuedan { get; set; }
    public DateTime? SiguienteVence { get; set; }
    public bool EsCafe { get; set; }
}

public class VencAlertaDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public bool EsCafe { get; set; }
    public DateTime FechaVence { get; set; }
    public decimal Quedan { get; set; }
    public int Dias { get; set; }
}
