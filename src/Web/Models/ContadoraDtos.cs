namespace Web.Models;

public class ContadoraJurisdiccionRowDto
{
    public string Provincia { get; set; } = "";
    public int Cantidad { get; set; }
    public decimal Neto { get; set; }
    public decimal Total { get; set; }
}

public class ContadoraJurisdiccionDto
{
    public DateTime? Desde { get; set; }
    public DateTime? Hasta { get; set; }
    public List<ContadoraJurisdiccionRowDto> Filas { get; set; } = new();
    public int CantidadTotal { get; set; }
    public decimal NetoTotal { get; set; }
    public decimal IvaTotal { get; set; }
    public decimal TotalConIva { get; set; }
    public decimal IvaAlicuota { get; set; } = 0.21m;
    public int SinProvincia { get; set; }
    public DateTime? VentasDesde { get; set; }
    public DateTime? VentasHasta { get; set; }
}

public class ContadoraBackfillResultDto
{
    public int Resueltos { get; set; }
    public int PendientesAntes { get; set; }
    public int Pendientes { get; set; }
    public int Errores { get; set; }
    public string? Mensaje { get; set; }
}
