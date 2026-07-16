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

    /// <summary>2026-07-16: registra que una publicación PAUSADA tiene stock para vender pero el robot
    /// NO la despertó (política nueva tras el incidente de las cápsulas vendidas a precio viejo: el push
    /// de stock ya no reactiva publicaciones pausadas — el usuario revisa el precio y la activa él).
    /// ValorNuevo = precio actual en MeLi (el sospechoso), Delta = stock calculado disponible.
    /// Dedup: si ya hay un aviso SIN VER para la misma publicación, no crea otro (el push corre cada
    /// 15 min y sería un aviso repetido por vuelta).</summary>
    public async Task LogPausadaConStockAsync(
        string meliItemId, int? meliAccountId, string? sku, string? title,
        decimal? precioActual, int stockDisponible, string source = "push",
        bool saveChanges = false, CancellationToken ct = default)
    {
        try
        {
            var yaAvisado = await _db.MeliCambiosDetectados
                .AnyAsync(c => c.MeliItemId == meliItemId && c.Tipo == "PAUSADA_CON_STOCK" && c.SeenAt == null, ct);
            if (yaAvisado) return;

            _db.MeliCambiosDetectados.Add(new MeliCambioDetectado
            {
                MeliItemId = meliItemId,
                MeliAccountId = meliAccountId,
                Sku = sku,
                Title = title,
                Tipo = "PAUSADA_CON_STOCK",
                ValorAnterior = "paused",
                ValorNuevo = precioActual?.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                Delta = stockDisponible,
                Source = source,
                DetectedAt = DateTime.UtcNow
            });
            if (saveChanges) await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Error logging pausada-con-stock for {MeliItemId}", meliItemId);
        }
    }
}
