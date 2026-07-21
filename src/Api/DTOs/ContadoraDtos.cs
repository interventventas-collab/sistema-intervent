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

// ───────── Importacion del reporte Excel oficial de MeLi (etapa 3, con notas de credito) ─────────

/// <summary>Resultado de importar uno o varios archivos de reporte.</summary>
public class ContadoraImportResultDto
{
    public bool Ok { get; set; } = true;
    public string? Mensaje { get; set; }
    public List<ContadoraImportArchivoDto> Archivos { get; set; } = new();

    // Totales sumados de todos los archivos importados en esta corrida.
    public int FilasLeidas { get; set; }
    public int Nuevos { get; set; }
    public int Actualizados { get; set; }
    public int Facturas { get; set; }
    public int NotasCredito { get; set; }
    public int Omitidos { get; set; }   // filas sin comprobante / rechazadas / no aprobadas
}

public class ContadoraImportArchivoDto
{
    public string Archivo { get; set; } = "";
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public string? EmpresaCuit { get; set; }
    public int Facturas { get; set; }
    public int NotasCredito { get; set; }
    public int Nuevos { get; set; }
    public int Actualizados { get; set; }
    public DateTime? PeriodoDesde { get; set; }
    public DateTime? PeriodoHasta { get; set; }
    public decimal NetoNeto { get; set; }  // neto ya restando NC
    public decimal IvaNeto { get; set; }
    public decimal TotalNeto { get; set; }
}

/// <summary>Resumen del Libro IVA Ventas armado desde los comprobantes IMPORTADOS (con NC restando).</summary>
public class ContadoraReporteResumenDto
{
    public List<LibroIvaResumenRowDto> Filas { get; set; } = new();
    public int CantidadFacturas { get; set; }
    public int CantidadNotasCredito { get; set; }
    public decimal NetoTotal { get; set; }   // neto de NC
    public decimal IvaTotal { get; set; }
    public decimal TotalTotal { get; set; }
    /// <summary>True si todavia no se importo ningun reporte (para el mensaje "subi el primero").</summary>
    public bool SinDatos { get; set; }
}

/// <summary>Un mes/empresa ya cargado (para mostrar "lo que tengo importado").</summary>
public class ContadoraCargaDto
{
    public int Anio { get; set; }
    public int Mes { get; set; }
    public string? EmpresaCuit { get; set; }
    public int Facturas { get; set; }
    public int NotasCredito { get; set; }
    public decimal NetoNeto { get; set; }
    public decimal IvaNeto { get; set; }
    public decimal TotalNeto { get; set; }
}

public class ContadoraRetencionDto
{
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal Monto { get; set; }
    public string? Nota { get; set; }
}

public class ContadoraPdfResultDto
{
    public bool Ok { get; set; } = true;
    public string? Mensaje { get; set; }
    public int Total { get; set; }
    public int Adjuntados { get; set; }
    public int SinMatch { get; set; }
    public int SinQr { get; set; }
    public List<string> NoMatch { get; set; } = new();
}

public class ContadoraComprobanteDto
{
    public string IdComprobante { get; set; } = "";
    public string? Origen { get; set; }
    public string? Concepto { get; set; }
    public bool TienePdf { get; set; }
    public string? EmpresaCuit { get; set; }
    public bool EsNotaCredito { get; set; }
    public string? TipoComprobante { get; set; }
    public int? PuntoVenta { get; set; }
    public long? NumeroComprobante { get; set; }
    public DateTime? FechaEmision { get; set; }
    public string? Letra { get; set; }
    public string? ReceptorNombre { get; set; }
    public string? ReceptorDoc { get; set; }
    public string? Provincia { get; set; }
    public decimal Neto { get; set; }   // con signo (NC en negativo)
    public decimal Iva { get; set; }
    public decimal Total { get; set; }

    // ── Seguimiento de pago (solo aplica a facturas de COMPRA) ──
    /// <summary>True si es una factura de compra sobre la que se puede registrar pago (no NC).</summary>
    public bool PuedeRegistrarPago { get; set; }
    /// <summary>Suma de los pagos no anulados registrados sobre esta factura.</summary>
    public decimal Pagado { get; set; }
}

public class ContadoraComprobantesPageDto
{
    public List<ContadoraComprobanteDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

// ───────── Balanza de IVA (ventas vs compras) ─────────

public class ContadoraBalanzaMesDto
{
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal IvaVentas { get; set; }   // IVA débito
    public decimal IvaCompras { get; set; }  // IVA crédito
    public decimal Saldo { get; set; }        // saldo técnico = ventas - compras (>0 a favor de ARCA)
    // Posición real (como el F2051): saldo técnico - retenciones - saldo a favor arrastrado.
    public decimal Retenciones { get; set; }          // retenciones/percepciones de IVA sufridas
    public decimal SaldoFavorAnterior { get; set; }   // saldo a favor de meses anteriores aplicado
    public decimal Posicion { get; set; }             // >0 = A PAGAR, <0 = a favor
}

public class ContadoraBalanzaDto
{
    public List<ContadoraBalanzaMesDto> Filas { get; set; } = new();
    public decimal IvaVentasTotal { get; set; }
    public decimal IvaComprasTotal { get; set; }
    public decimal SaldoTotal { get; set; }
    public decimal RetencionesTotal { get; set; }
    public decimal APagarTotal { get; set; }      // suma de los meses que dieron a pagar
    public decimal SaldoFavorActual { get; set; } // saldo a favor arrastrado al final (disponible)
}

// ───────── Control / doble-check (AFIP vs MeLi/sistema) ─────────

public class ContadoraControlDto
{
    public int CoincidenCant { get; set; }   // en AFIP y en MeLi/sistema, montos iguales
    public decimal CoincidenIva { get; set; }
    public int SoloAfipCant { get; set; }     // en AFIP, no en MeLi (mostrador / meses sin reporte MeLi)
    public decimal SoloAfipIva { get; set; }
    public int SoloMeliCant { get; set; }     // en MeLi/sistema, no en AFIP (⚠️ a revisar)
    public decimal SoloMeliIva { get; set; }
    public int DifierenCant { get; set; }     // misma factura, IVA distinto (⚠️ a revisar)
    public decimal DifierenIva { get; set; }
    public List<ContadoraControlItemDto> Revisar { get; set; } = new();  // solo-MeLi + montos que difieren
    public bool SinAfip { get; set; }         // true si todavia no se importaron ventas de AFIP
}

public class ContadoraControlItemDto
{
    public string Tipo { get; set; } = "";    // "Solo en MeLi", "Difiere el monto"
    public DateTime? Fecha { get; set; }
    public int? PuntoVenta { get; set; }
    public string? Letra { get; set; }
    public long? Numero { get; set; }
    public string? Cliente { get; set; }
    public string? Fuente { get; set; }        // MercadoLibre / Sistema
    public decimal IvaAfip { get; set; }
    public decimal IvaOtro { get; set; }
}

// ───────── Pagos de facturas de COMPRA (cuenta corriente de proveedores con CAE) ─────────

public class ContadoraPagoDto
{
    public int Id { get; set; }
    public DateTime Fecha { get; set; }
    public string Medio { get; set; } = "Transferencia";
    public string? Referencia { get; set; }
    public decimal Importe { get; set; }
    public string? Operador { get; set; }
    public string? Observaciones { get; set; }
}

/// <summary>Estado de pago de una factura de compra + su historial de pagos.</summary>
public class ContadoraFacturaPagosDto
{
    public string IdComprobante { get; set; } = "";
    public string? ProveedorNombre { get; set; }
    public string? ProveedorCuit { get; set; }
    public decimal Total { get; set; }
    public decimal Pagado { get; set; }
    public decimal Saldo { get; set; }
    public List<ContadoraPagoDto> Pagos { get; set; } = new();
}

public class RegistrarPagoCompraRequest
{
    public string IdComprobante { get; set; } = "";
    public DateTime? Fecha { get; set; }
    public string Medio { get; set; } = "Transferencia";
    public string? Referencia { get; set; }
    public decimal Importe { get; set; }
    public string? Observaciones { get; set; }
}

/// <summary>Cuánto se le debe a cada proveedor (facturas de compra − pagos), en un período.</summary>
public class ContadoraDeudaProveedorDto
{
    public string? Cuit { get; set; }
    public string? Nombre { get; set; }
    public int Facturas { get; set; }
    public decimal Total { get; set; }
    public decimal Pagado { get; set; }
    public decimal Saldo { get; set; }
}

public class ContadoraDeudaProveedoresDto
{
    public List<ContadoraDeudaProveedorDto> Items { get; set; } = new();
    public decimal SaldoTotal { get; set; }
}

public class RegistrarPagoResultDto
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public ContadoraFacturaPagosDto? Factura { get; set; }
}

// ── Cruce banco (Galicia) ↔ facturas de compra ──

/// <summary>Una factura de compra con saldo pendiente, para elegir al pagar desde el banco.</summary>
public class FacturaCompraImpagaDto
{
    public string IdComprobante { get; set; } = "";
    public string? TipoComprobante { get; set; }
    public int? PuntoVenta { get; set; }
    public long? NumeroComprobante { get; set; }
    public DateTime? FechaEmision { get; set; }
    public decimal Total { get; set; }
    public decimal Pagado { get; set; }
    public decimal Saldo { get; set; }
}

public class PagarComprasDesdeBancoRequest
{
    public int ExtractoMovId { get; set; }
    public List<string> IdComprobantes { get; set; } = new();
}

public class PagoBancoResultDto
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public int PagosCreados { get; set; }
}

/// <summary>Para el listado del extracto: qué facturas quedaron pagadas por un movimiento del banco.</summary>
public class PagoBancoMovDto
{
    public int MovId { get; set; }
    public List<string> Facturas { get; set; } = new();
}
