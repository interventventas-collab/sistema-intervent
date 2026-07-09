using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// 2026-07-08: Robot del modulo "Contadora". Mantiene al dia, SOLO en el servidor (sin depender
/// de ninguna pestaña abierta), los datos de las ventas de MercadoLibre:
///   - Provincia de destino (para el cuadro por jurisdiccion / IIBB).
///   - Factura de venta emitida por MeLi (para el Libro IVA Ventas).
///
/// Corre 15 min despues de arrancar y despues cada 4 horas. Solo toca las ventas que todavia
/// les falta el dato (cuando no hay nada pendiente, termina en un ciclo y no consume nada).
/// Usa un scope fresco por tanda para no acumular memoria en corridas largas.
/// </summary>
public class ContadoraAutoBackfillService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ContadoraAutoBackfillService> _logger;
    private static bool _corriendo; // evita solapamiento entre el timer y un disparo manual

    public ContadoraAutoBackfillService(IServiceScopeFactory scopeFactory, ILogger<ContadoraAutoBackfillService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); } catch { return; }
        await RunOnce(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromHours(4), stoppingToken); }
            catch (OperationCanceledException) { break; }
            await RunOnce(stoppingToken);
        }
    }

    /// <summary>Dispara una corrida completa a demanda (para el boton "actualizar ahora"). No espera al timer.</summary>
    public Task RunOnceManualAsync(CancellationToken ct = default) => RunOnce(ct);

    private async Task RunOnce(CancellationToken ct)
    {
        if (_corriendo) { _logger.LogInformation("[Contadora robot] Ya hay una corrida en curso — skip"); return; }
        _corriendo = true;
        try
        {
            _logger.LogInformation("[Contadora robot] Inicio backfill provincias + facturas");
            int prov = await LoopBackfill(ct, esFacturas: false);
            int fact = await LoopBackfill(ct, esFacturas: true);
            // Sincroniza las facturas propias del sistema (AFIP) hacia el Libro IVA unificado.
            using (var scope = _scopeFactory.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<ContadoraService>();
                var r = await svc.SincronizarSistemaAsync();
                _logger.LogInformation("[Contadora robot] Sistema: {Msg}", r.Mensaje);
            }
            _logger.LogInformation("[Contadora robot] Fin. Provincias resueltas: {P}, facturas resueltas: {F}", prov, fact);
        }
        catch (Exception ex) { _logger.LogError(ex, "[Contadora robot] Error en la corrida"); }
        finally { _corriendo = false; }
    }

    /// <summary>Repite tandas (scope fresco por tanda) hasta que no queden pendientes o no haya avance.</summary>
    private async Task<int> LoopBackfill(CancellationToken ct, bool esFacturas)
    {
        int totalResueltos = 0;
        for (int i = 0; i < 500 && !ct.IsCancellationRequested; i++)
        {
            using var scope = _scopeFactory.CreateScope();
            var svc = scope.ServiceProvider.GetRequiredService<ContadoraService>();
            var r = esFacturas ? await svc.BackfillFacturasAsync(120) : await svc.BackfillProvinciasAsync(150);
            totalResueltos += r.Resueltos;
            if (r.Pendientes == 0) break;
            if (r.Resueltos == 0) // sin avance (sin token valido o solo errores) → cortar
            {
                _logger.LogWarning("[Contadora robot] Sin avance ({Tipo}): {Msg}", esFacturas ? "facturas" : "provincias", r.Mensaje);
                break;
            }
        }
        return totalResueltos;
    }
}
