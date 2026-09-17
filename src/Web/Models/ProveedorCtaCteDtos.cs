namespace Web.Models;

// 17/09/2026 — Cuenta corriente de proveedores (espejo de CafeProveedoresCtaCteController).

public class CtaCteResumenDto
{
    public int ProveedorId { get; set; }
    public string Nombre { get; set; } = "";
    public string? Cuit { get; set; }
    public DateTime? Desde { get; set; }
    public decimal Oficial { get; set; }
    public decimal NoOficial { get; set; }
    public decimal ACuenta { get; set; }
    public decimal Total { get; set; }
    public int Pendientes { get; set; }
}

public class CtaCteDocDto
{
    /// <summary>"AFIP:..." (factura de AFIP) o "DEU:..." (cotización / saldo inicial).</summary>
    public string Clave { get; set; } = "";
    public string Tipo { get; set; } = "";
    public string Numero { get; set; } = "";
    public bool Oficial { get; set; }
    public bool EsNotaCredito { get; set; }
    public bool EsSaldoInicial { get; set; }
    /// <summary>Día argentino, sin hora: se muestra tal cual (no pasarlo por ToArTime).</summary>
    public DateTime Fecha { get; set; }
    public decimal Total { get; set; }
    public decimal Pagado { get; set; }
    public decimal Saldo { get; set; }
    public int? DeudaId { get; set; }
    public string? AfipIdComprobante { get; set; }
    public bool TieneArchivo { get; set; }
    public string? Observaciones { get; set; }
    public string Etiqueta => string.IsNullOrWhiteSpace(Numero) ? Tipo : $"{Tipo} {Numero}";
}

public class CtaCteMovDto
{
    /// <summary>Día argentino, sin hora.</summary>
    public DateTime Fecha { get; set; }
    public string Que { get; set; } = "";
    public decimal Suma { get; set; }
    public decimal Resta { get; set; }
    public decimal Saldo { get; set; }
    public string? Detalle { get; set; }
    public int? PagoId { get; set; }
}

public class CtaCteCuentaDto
{
    public int ProveedorId { get; set; }
    public string Nombre { get; set; } = "";
    public string? Cuit { get; set; }
    public bool CuentaCorriente { get; set; }
    public DateTime? Desde { get; set; }
    public decimal Oficial { get; set; }
    public decimal NoOficial { get; set; }
    public decimal ACuenta { get; set; }
    public decimal Total { get; set; }
    public decimal SaldoInicialOficial { get; set; }
    public decimal SaldoInicialNoOficial { get; set; }
    public List<CtaCteDocDto> Pendientes { get; set; } = new();
    public List<CtaCteMovDto> Movimientos { get; set; } = new();
}

public class CtaCteCandidatoDto
{
    public int? ProveedorId { get; set; }
    public string Nombre { get; set; } = "";
    public string? Cuit { get; set; }
    public bool YaLleva { get; set; }
    public int FacturasAfip { get; set; }
    public DateTime? UltimaFactura { get; set; }
}

/// <summary>Lo que se le puede pagar a un proveedor (pantalla de Tesorería → Pagos).</summary>
public class PendientesProveedorDto
{
    public bool CuentaCorriente { get; set; }
    public DateTime? Desde { get; set; }
    public decimal Oficial { get; set; }
    public decimal NoOficial { get; set; }
    public decimal ACuenta { get; set; }
    public decimal Total { get; set; }
    public List<CtaCteDocDto> Documentos { get; set; } = new();
}
