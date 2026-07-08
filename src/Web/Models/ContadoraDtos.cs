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

public class ContadoraEmpresaDto
{
    public string Cuit { get; set; } = "";
    public string Nombre { get; set; } = "";
}

public class LibroIvaResumenRowDto
{
    public string? EmpresaCuit { get; set; }
    public string? EmpresaNombre { get; set; }
    public int? PuntoVenta { get; set; }
    public string? Letra { get; set; }
    public int Cantidad { get; set; }
    public decimal Neto { get; set; }
    public decimal Iva { get; set; }
    public decimal Total { get; set; }
}

public class ContadoraLibroIvaDto
{
    public List<LibroIvaResumenRowDto> Filas { get; set; } = new();
    public int CantidadTotal { get; set; }
    public decimal NetoTotal { get; set; }
    public decimal IvaTotal { get; set; }
    public decimal TotalTotal { get; set; }
    public int SinFactura { get; set; }
    public int Pendientes { get; set; }
}

public class ContadoraFacturaDto
{
    public long MeliOrderId { get; set; }
    public string? EmpresaCuit { get; set; }
    public string? EmpresaNombre { get; set; }
    public int? PuntoVenta { get; set; }
    public long? NumeroComprobante { get; set; }
    public DateTime? FechaEmision { get; set; }
    public string? Letra { get; set; }
    public string? ReceptorNombre { get; set; }
    public string? ReceptorDoc { get; set; }
    public string? Provincia { get; set; }
    public decimal Neto { get; set; }
    public decimal Iva { get; set; }
    public decimal Total { get; set; }
}

public class ContadoraFacturasPageDto
{
    public List<ContadoraFacturaDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}
