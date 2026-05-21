using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Api.Data;
using Api.Models;

namespace Api.Services;

/// <summary>
/// Job nocturno (a las 4 AM ART = 7 AM UTC) que consulta la API de Contabilium
/// y guarda un snapshot del stock actual para CADA producto que tenemos en sistema
/// con SKU coincidente. Eso alimenta la pantalla /cafe/stock-comparado.
///
/// Tambien al arrancar la app, hace el primer snapshot a los 10 minutos.
/// </summary>
public class ContabiliumNightlySnapshotService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContabiliumNightlySnapshotService> _logger;

    public ContabiliumNightlySnapshotService(IServiceScopeFactory scopeFactory, ILogger<ContabiliumNightlySnapshotService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Primer ejecución 10 min después de arrancar (no se ejecuta inmediato).
        try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); } catch { return; }
        await RunOnce(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            // Calcular próxima 4 AM ART (UTC-3) = 7 AM UTC.
            var nowUtc = DateTime.UtcNow;
            var nextUtc = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 7, 0, 0, DateTimeKind.Utc);
            if (nextUtc <= nowUtc) nextUtc = nextUtc.AddDays(1);
            var delay = nextUtc - nowUtc;
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }
            await RunOnce(stoppingToken);
        }
    }

    private async Task RunOnce(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var contab = scope.ServiceProvider.GetRequiredService<ContabiliumService>();

            var acc = await contab.GetAccountAsync();
            if (acc is null)
            {
                _logger.LogInformation("[Contab snapshot] Sin cuenta Contabilium — skip");
                return;
            }

            // Bajar todos los conceptos paginados.
            var todosBySku = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            int page = 1;
            while (true)
            {
                var pg = await contab.ListConceptosAsync(page, 50);
                if (pg is null) break;
                foreach (var c in pg.Items)
                {
                    var sku = (c.Codigo ?? "").Trim().ToUpperInvariant();
                    if (!string.IsNullOrEmpty(sku))
                        todosBySku[sku] = c.Stock ?? 0m;
                }
                if (pg.Items.Count < 50) break;
                page++;
                if (page > 200) break;
                await Task.Delay(80, ct);
                if (ct.IsCancellationRequested) return;
            }
            _logger.LogInformation("[Contab snapshot] Bajados {N} SKUs", todosBySku.Count);

            var hoy = DateTime.UtcNow.Date;
            // Borrar snapshot del día si existe (sobreescribir).
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM StockSnapshots WHERE Fecha = {0}", hoy);

            int guardados = 0;
            foreach (var (sku, stock) in todosBySku)
            {
                db.StockSnapshots.Add(new StockSnapshot
                {
                    Sku = sku,
                    Fecha = hoy,
                    StockContabilium = stock,
                    Source = "contabilium-api",
                    CreatedAt = DateTime.UtcNow
                });
                guardados++;
                if (guardados % 500 == 0)
                {
                    await db.SaveChangesAsync(ct);
                }
            }
            await db.SaveChangesAsync(ct);
            _logger.LogInformation("[Contab snapshot] Snapshot guardado: {N} SKUs", guardados);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Contab snapshot] Error en snapshot nocturno");
        }
    }
}
