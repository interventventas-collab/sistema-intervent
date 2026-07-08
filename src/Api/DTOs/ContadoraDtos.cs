namespace Api.DTOs;

/// <summary>Una fila del cuadro de ventas por jurisdiccion (una provincia).</summary>
public class ContadoraJurisdiccionRowDto
{
    public string Provincia { get; set; } = "";   // nombre para mostrar (normalizado)
    public int Cantidad { get; set; }              // cantidad de ventas
    public decimal Neto { get; set; }              // total sin IVA
    public decimal Total { get; set; }             // total con IVA
}

/// <summary>Cuadro completo de ventas por jurisdiccion para un rango de fechas.</summary>
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
    /// <summary>Ventas pagas del rango que todavia NO tienen provincia resuelta (hay que correr el backfill).</summary>
    public int SinProvincia { get; set; }
    /// <summary>Rango real de fechas de las ventas de MeLi que hay en el sistema (para mostrar de guia).</summary>
    public DateTime? VentasDesde { get; set; }
    public DateTime? VentasHasta { get; set; }
}

/// <summary>Resultado de una corrida (por lote) del backfill de provincias.</summary>
public class ContadoraBackfillResultDto
{
    public int Resueltos { get; set; }       // envios resueltos en este lote
    public int PendientesAntes { get; set; } // cuantos faltaban al empezar (para la barra de progreso)
    public int Pendientes { get; set; }      // cuantos quedan sin resolver ahora
    public int Errores { get; set; }
    public string? Mensaje { get; set; }
}

// ───────── Libro IVA Ventas (etapa 2) ─────────

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
    public int SinFactura { get; set; }   // ordenes procesadas que no tenian factura en MeLi
    public int Pendientes { get; set; }   // ordenes pagas cuya factura todavia no se bajo
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
