namespace Web.Models;

public class MpAccountDto
{
    public int Id { get; set; }
    public string? Alias { get; set; }
    public bool HasToken { get; set; }
    public bool IsActive { get; set; }
    public long? MpUserId { get; set; }
    public string? Nickname { get; set; }
    public string? SiteId { get; set; }
    public decimal? LastSaldoDisponible { get; set; }
    public decimal? LastSaldoTotal { get; set; }
    public DateTime? LastSaldoAt { get; set; }
    public bool LastSyncOk { get; set; }
    public string? LastError { get; set; }
    public bool AutoSyncEnabled { get; set; }
    public string? AutoSyncTimes { get; set; }
    public DateTime? LastAutoSyncAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class SaveMpAccountRequest
{
    public string? AccessToken { get; set; }
    public string? Alias { get; set; }
    public bool IsActive { get; set; } = true;
    public bool AutoSyncEnabled { get; set; } = false;
    public string? AutoSyncTimes { get; set; }
}

public class MpSincronizarResultDto
{
    public bool Ok { get; set; }
    public decimal? Disponible { get; set; }
    public decimal? Total { get; set; }
    public string? Error { get; set; }
}

// --- Cobros recibidos (Parte A) ---
public class MpSyncPagosResultDto
{
    public bool Ok { get; set; }
    public int Nuevos { get; set; }
    public int Actualizados { get; set; }
    public int TotalTraidos { get; set; }
    public string? Error { get; set; }
    public bool Truncado { get; set; }
}

public class MpPagoDto
{
    public int Id { get; set; }
    public long MpPaymentId { get; set; }
    public DateTime Fecha { get; set; }
    public string? Estado { get; set; }
    public string? EstadoDetalle { get; set; }
    public decimal Monto { get; set; }
    public decimal? MontoNeto { get; set; }
    public string? Descripcion { get; set; }
    public string? PayerEmail { get; set; }
    public string? PayerNombre { get; set; }
    public string? MedioPago { get; set; }
    public string? ReferenciaExterna { get; set; }
    public int? VentaIdAsociada { get; set; }
}

public class MpPagosResumenDto
{
    public int Cantidad { get; set; }
    public decimal TotalBruto { get; set; }
    public decimal TotalNeto { get; set; }
    public DateTime? UltimoCobroAt { get; set; }
    public decimal Liberado { get; set; }
    public decimal Pendiente { get; set; }
}

// --- Movimientos por reportes (Parte B) ---
public class MpSyncMovResultDto
{
    public bool Ok { get; set; }
    public int Nuevos { get; set; }
    public int TotalFilas { get; set; }
    public string? Error { get; set; }
    public bool EnProceso { get; set; }
}

public class MpMovimientoDto
{
    public int Id { get; set; }
    public string? SourceId { get; set; }
    public DateTime Fecha { get; set; }
    public string? TipoTransaccion { get; set; }
    public string? Descripcion { get; set; }
    public decimal MontoBruto { get; set; }
    public decimal Comision { get; set; }
    public decimal MontoNeto { get; set; }
    public string? Moneda { get; set; }
    public string? MedioPago { get; set; }
    public string? ReferenciaExterna { get; set; }
    public int? VentaIdAsociada { get; set; }
}

public class MpMovResumenDto
{
    public int Cantidad { get; set; }
    public decimal TotalIngresos { get; set; }
    public decimal TotalEgresos { get; set; }
    public decimal TotalComisiones { get; set; }
    public decimal NetoPeriodo { get; set; }
    public DateTime? Desde { get; set; }
    public DateTime? Hasta { get; set; }
}

public class MpDashboardDto
{
    public bool Conectada { get; set; }
    public decimal CobradoNeto30 { get; set; }
    public decimal CobradoBruto30 { get; set; }
    public int CantCobros30 { get; set; }
    public decimal NetoMov30 { get; set; }
    public DateTime? UltimoDato { get; set; }
    public decimal Liberado30 { get; set; }
    public decimal Pendiente30 { get; set; }
}
