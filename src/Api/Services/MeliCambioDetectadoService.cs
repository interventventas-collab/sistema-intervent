using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Detecta y registra cambios en publicaciones MeLi (precio + status).
/// Se llama desde el flujo de sync — cada vez que se actualiza un MeliItem, comparamos
/// los valores anteriores vs los nuevos y, si hay diferencia material, insertamos un registro
/// en MeliCambiosDetectados.
///
/// El usuario los ve en /cafe/cambios-meli + badge en topbar (cantidad sin ver).
/// </summary>
public class MeliCambioDetectadoService
{
    private readonly AppDbContext _db;
    private readonly ILogger<MeliCambioDetectadoService> _log;

    public MeliCambioDetectadoService(AppDbContext db, ILogger<MeliCambioDetectadoService> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>Registra un cambio de PRECIO si hay diferencia. Llama esto cuando actualizás un MeliItem.</summary>
    public async Task LogPriceChangeAsync(
        string meliItemId, int? meliAccountId, string? sku, string? title,
        decimal oldPrice, decimal newPrice, string source = "sync",
        bool saveChanges = false, CancellationToken ct = default)
    {
        // Solo si hay cambio real (>1 centavo de diferencia para evitar ruido por rounding)
        if (Math.Abs(newPrice - oldPrice) < 0.01m) return;

        var tipo = newPrice < oldPrice ? "PRECIO_BAJA" : "PRECIO_SUBE";
        var delta = newPrice - oldPrice;
        var deltaPct = oldPrice > 0 ? Math.Round(delta / oldPrice * 100m, 2) : (decimal?)null;

        try
        {
            _db.MeliCambiosDetectados.Add(new MeliCambioDetectado
            {
                MeliItemId = meliItemId,
                MeliAccountId = meliAccountId,
                Sku = sku,
                Title = title,
                Tipo = tipo,
                ValorAnterior = oldPrice.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                ValorNuevo = newPrice.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                Delta = delta,
                DeltaPct = deltaPct,
                Source = source,
                DetectedAt = DateTime.UtcNow
            });
            if (saveChanges) await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error logging price change for {MeliItemId}", meliItemId);
        }
    }

    /// <summary>Registra un cambio de STATUS si hay transición de paused↔active.
    /// Solo nos interesan esas dos transiciones (paused↔active) — closed/deleted no son sospechosas.</summary>
    public async Task LogStatusChangeAsync(
        string meliItemId, int? meliAccountId, string? sku, string? title,
        string? oldStatus, string? newStatus, string source = "sync",
        bool saveChanges = false, CancellationToken ct = default)
    {
        if (string.Equals(oldStatus, newStatus, StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrEmpty(oldStatus) || string.IsNullOrEmpty(newStatus)) return;

        string? tipo = null;
        if (string.Equals(oldStatus, "active", StringComparison.OrdinalIgnoreCase)
            && string.Equals(newStatus, "paused", StringComparison.OrdinalIgnoreCase))
        {
            tipo = "STATUS_PAUSED";
        }
        else if (string.Equals(oldStatus, "paused", StringComparison.OrdinalIgnoreCase)
            && string.Equals(newStatus, "active", StringComparison.OrdinalIgnoreCase))
        {
            tipo = "STATUS_ACTIVE";
        }
        // Ignoramos: paused↔under_review, active↔closed, etc. (no son alertas críticas)
        if (tipo is null) return;

        try
        {
            _db.MeliCambiosDetectados.Add(new MeliCambioDetectado
            {
                MeliItemId = meliItemId,
                MeliAccountId = meliAccountId,
                Sku = sku,
                Title = title,
                Tipo = tipo,
                ValorAnterior = oldStatus,
                ValorNuevo = newStatus,
                Source = source,
                DetectedAt = DateTime.UtcNow
            });
            if (saveChanges) await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error logging status change for {MeliItemId}", meliItemId);
        }
    }

    /// <summary>Cuenta cambios sin ver (para el badge del topbar).</summary>
    public Task<int> CountUnseenAsync(CancellationToken ct = default)
        => _db.MeliCambiosDetectados.AsNoTracking().Where(c => c.SeenAt == null).CountAsync(ct);
}
