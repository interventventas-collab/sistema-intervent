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
}
