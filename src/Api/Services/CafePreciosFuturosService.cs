using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-06-17: Migra precios futuros vencidos al campo principal.
/// Para cada producto con FechaAplicaPreciosFuturos en el pasado y al menos un *Futuro seteado:
///   - PrecioBar ← PrecioBarFuturo (si el futuro está seteado)
///   - PrecioOtro ← PrecioOtroFuturo (si está seteado)
///   - PrecioPorKg ← PrecioPorKgFuturo
///   - PrecioBulto ← PrecioBultoFuturo
///   - PrecioBultoOtro ← PrecioBultoOtroFuturo
/// Después limpia los *Futuro y la fecha. El cambio queda como cualquier otro update del producto
/// (StockChangedAt no se toca porque esto es precio, no stock).
/// </summary>
public class CafePreciosFuturosService
{
    private readonly AppDbContext _db;
    private readonly ILogger<CafePreciosFuturosService> _logger;

    public CafePreciosFuturosService(AppDbContext db, ILogger<CafePreciosFuturosService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public record MigracionResult(int ProductosMigrados, List<string> Detalles);

    public async Task<MigracionResult> MigrarVencidosAsync(CancellationToken ct = default)
    {
        var hoy = DateTime.UtcNow.Date;
        var afectados = await _db.CafeProductos
            .Where(p => p.FechaAplicaPreciosFuturos != null
                     && p.FechaAplicaPreciosFuturos <= hoy
                     && (p.PrecioBarFuturo != null || p.PrecioOtroFuturo != null
                         || p.PrecioPorKgFuturo != null || p.PrecioBultoFuturo != null
                         || p.PrecioBultoOtroFuturo != null))
            .ToListAsync(ct);

        var detalles = new List<string>();
        foreach (var p in afectados)
        {
            var cambios = new List<string>();
            if (p.PrecioBarFuturo.HasValue) { cambios.Add($"Bar:{p.PrecioBar}→{p.PrecioBarFuturo}"); p.PrecioBar = p.PrecioBarFuturo; p.PrecioBarFuturo = null; }
            if (p.PrecioOtroFuturo.HasValue) { cambios.Add($"Otro:{p.PrecioOtro}→{p.PrecioOtroFuturo}"); p.PrecioOtro = p.PrecioOtroFuturo; p.PrecioOtroFuturo = null; }
            if (p.PrecioPorKgFuturo.HasValue) { cambios.Add($"Kg:{p.PrecioPorKg}→{p.PrecioPorKgFuturo}"); p.PrecioPorKg = p.PrecioPorKgFuturo; p.PrecioPorKgFuturo = null; }
            if (p.PrecioBultoFuturo.HasValue) { cambios.Add($"Bulto:{p.PrecioBulto}→{p.PrecioBultoFuturo}"); p.PrecioBulto = p.PrecioBultoFuturo; p.PrecioBultoFuturo = null; }
            if (p.PrecioBultoOtroFuturo.HasValue) { cambios.Add($"BultoOtro:{p.PrecioBultoOtro}→{p.PrecioBultoOtroFuturo}"); p.PrecioBultoOtro = p.PrecioBultoOtroFuturo; p.PrecioBultoOtroFuturo = null; }
            p.FechaAplicaPreciosFuturos = null;
            var detalle = $"{p.Sku ?? "#" + p.Id} ({p.Nombre}): {string.Join(", ", cambios)}";
            detalles.Add(detalle);
            _logger.LogInformation("[PreciosFuturos] Migrado {Detalle}", detalle);
        }
        if (afectados.Count > 0) await _db.SaveChangesAsync(ct);
        return new MigracionResult(afectados.Count, detalles);
    }
}

/// <summary>2026-06-17: corre la migracion al arrancar la app + cada 24 hs.</summary>
public class CafePreciosFuturosBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CafePreciosFuturosBackgroundService> _logger;
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan Period = TimeSpan.FromHours(24);

    public CafePreciosFuturosBackgroundService(IServiceScopeFactory scopeFactory, ILogger<CafePreciosFuturosBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, stoppingToken); }
        catch (TaskCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<CafePreciosFuturosService>();
                var r = await svc.MigrarVencidosAsync(stoppingToken);
                if (r.ProductosMigrados > 0)
                    _logger.LogInformation("[PreciosFuturos] {N} productos migrados", r.ProductosMigrados);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[PreciosFuturos] error en background");
            }
            try { await Task.Delay(Period, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
