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

// ───────── Importacion del reporte oficial de MeLi (con notas de credito) ─────────

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
    public decimal NetoNeto { get; set; }
    public decimal IvaNeto { get; set; }
    public decimal TotalNeto { get; set; }
}

public class ContadoraImportResultDto
{
    public bool Ok { get; set; } = true;
    public string? Mensaje { get; set; }
    public List<ContadoraImportArchivoDto> Archivos { get; set; } = new();
    public int FilasLeidas { get; set; }
    public int Nuevos { get; set; }
    public int Actualizados { get; set; }
    public int Facturas { get; set; }
    public int NotasCredito { get; set; }
    public int Omitidos { get; set; }
}

public class ContadoraReporteResumenDto
{
    public List<LibroIvaResumenRowDto> Filas { get; set; } = new();
    public int CantidadFacturas { get; set; }
    public int CantidadNotasCredito { get; set; }
    public decimal NetoTotal { get; set; }
    public decimal IvaTotal { get; set; }
    public decimal TotalTotal { get; set; }
    public bool SinDatos { get; set; }
}

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
    public decimal Neto { get; set; }
    public decimal Iva { get; set; }
    public decimal Total { get; set; }
    // Seguimiento de pago (solo facturas de COMPRA)
    public bool PuedeRegistrarPago { get; set; }
    public decimal Pagado { get; set; }
    public decimal Saldo => Total - Pagado;
}

public class ContadoraComprobantesPageDto
{
    public List<ContadoraComprobanteDto> Items { get; set; } = new();
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
}

public class ContadoraBalanzaMesDto
{
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal IvaVentas { get; set; }
    public decimal IvaCompras { get; set; }
    public decimal Saldo { get; set; }
    public decimal Retenciones { get; set; }
    public decimal SaldoFavorAnterior { get; set; }
    public decimal Posicion { get; set; }
}

public class ContadoraBalanzaDto
{
    public List<ContadoraBalanzaMesDto> Filas { get; set; } = new();
    public decimal IvaVentasTotal { get; set; }
    public decimal IvaComprasTotal { get; set; }
    public decimal SaldoTotal { get; set; }
    public decimal RetencionesTotal { get; set; }
    public decimal APagarTotal { get; set; }
    public decimal SaldoFavorActual { get; set; }
}

public class ContadoraRetencionDto
{
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal Monto { get; set; }
    public string? Nota { get; set; }
}

public class ConfigCorreoDto
{
    public string Host { get; set; } = "imap.gmail.com";
    public int Port { get; set; } = 993;
    public string Usuario { get; set; } = "";
    public string? Carpeta { get; set; }
    public bool Activo { get; set; } = true;
    public bool TienePassword { get; set; }
    public DateTime? UltimaCorrida { get; set; }
}

public class ContadoraControlDto
{
    public int CoincidenCant { get; set; }
    public decimal CoincidenIva { get; set; }
    public int SoloAfipCant { get; set; }
    public decimal SoloAfipIva { get; set; }
    public int SoloMeliCant { get; set; }
    public decimal SoloMeliIva { get; set; }
    public int DifierenCant { get; set; }
    public decimal DifierenIva { get; set; }
    public List<ContadoraControlItemDto> Revisar { get; set; } = new();
    public bool SinAfip { get; set; }
}

public class ContadoraControlItemDto
{
    public string Tipo { get; set; } = "";
    public DateTime? Fecha { get; set; }
    public int? PuntoVenta { get; set; }
    public string? Letra { get; set; }
    public long? Numero { get; set; }
    public string? Cliente { get; set; }
    public string? Fuente { get; set; }
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

public class RegistrarPagoResultDto
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public ContadoraFacturaPagosDto? Factura { get; set; }
}

// ── Cruce banco (Galicia) ↔ facturas de compra ──

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

public class PagoBancoMovDto
{
    public int MovId { get; set; }
    public List<string> Facturas { get; set; } = new();
}

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
