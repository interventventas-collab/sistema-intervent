using System.Linq;
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

    private const string CuitPalanica = "30717212149"; // el CUIT del Libro IVA
    private static DateTime _ultimoAfip = DateTime.MinValue; // para bajar de AFIP a lo sumo 1x/día

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken); } catch { return; }
        await RunOnce(stoppingToken, incluirAfip: true);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromHours(4), stoppingToken); }
            catch (OperationCanceledException) { break; }
            await RunOnce(stoppingToken, incluirAfip: true);
        }
    }

    /// <summary>Dispara una corrida completa a demanda (para el boton "actualizar ahora"). No espera al timer.
    /// NO incluye la bajada de AFIP (esa entra con la clave fiscal y va sola 1x/día en el ciclo del timer).</summary>
    public Task RunOnceManualAsync(CancellationToken ct = default) => RunOnce(ct, incluirAfip: false);

    private async Task RunOnce(CancellationToken ct, bool incluirAfip)
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
            // Vuelca las facturas de MeLi (ya bajadas por la API) al Libro IVA Ventas → ventas automáticas.
            using (var scope = _scopeFactory.CreateScope())
            {
                var svc = scope.ServiceProvider.GetRequiredService<ContadoraService>();
                var r = await svc.SincronizarMeliApiAsync();
                _logger.LogInformation("[Contadora robot] MeLi API: {Msg}", r.Mensaje);
            }
            // Baja de AFIP (emitidos + recibidos) con la clave fiscal de PALANICA e importa. A lo sumo 1x/día.
            if (incluirAfip) await CorrerAfipAsync(ct);
            _logger.LogInformation("[Contadora robot] Fin. Provincias resueltas: {P}, facturas resueltas: {F}", prov, fact);
        }
        catch (Exception ex) { _logger.LogError(ex, "[Contadora robot] Error en la corrida"); }
        finally { _corriendo = false; }
    }

    /// <summary>Entra a AFIP con la clave fiscal de PALANICA, descarga los comprobantes (emitidos + recibidos)
    /// de los últimos 30 días y los importa al Libro IVA. Corre a lo sumo 1x/día.</summary>
    private async Task CorrerAfipAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _ultimoAfip < TimeSpan.FromHours(20)) return; // ya se bajó hoy

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var cuentas = scope.ServiceProvider.GetRequiredService<ArcaAccountService>();
            var scraper = scope.ServiceProvider.GetRequiredService<ArcaScrapingService>();
            var contadora = scope.ServiceProvider.GetRequiredService<ContadoraService>();

            var todas = await cuentas.GetAllAsync();
            var pal = todas.FirstOrDefault(a => a.IsActive && a.HasPassword
                && new string(a.Cuit.Where(char.IsDigit).ToArray()) == CuitPalanica);
            if (pal is null) { _logger.LogWarning("[Contadora robot] No hay cuenta ARCA de PALANICA activa con clave — salto AFIP"); return; }

            var st = await scraper.GetStatusAsync();
            if (st.Running) { _logger.LogInformation("[Contadora robot] ARCA ocupado (scrape en curso) — salto AFIP"); return; }

            var pass = await cuentas.GetPasswordAsync(pal.Id);
            if (string.IsNullOrEmpty(pass)) return;

            var (ok, err) = await scraper.StartComprobantesAsync(pal.Cuit, pal.CuitLogin, pass, new RangoFechasRequest { Tipo = "30dias" });
            if (!ok) { _logger.LogWarning("[Contadora robot] No se pudo iniciar la bajada de AFIP: {Err}", err); return; }

            // Esperar a que el scrape termine (máx ~3 min).
            for (int i = 0; i < 60 && !ct.IsCancellationRequested; i++)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { break; }
                var s = await scraper.GetStatusAsync();
                if (!s.Running) break;
            }
            var fin = await scraper.GetStatusAsync();
            if (fin.Running) { _logger.LogWarning("[Contadora robot] La bajada de AFIP no terminó a tiempo — salto la importación"); return; }

            var imp = await contadora.ImportarUltimoScrapeAfipAsync();
            _ultimoAfip = DateTime.UtcNow;
            _logger.LogInformation("[Contadora robot] AFIP: {Msg}", imp.Mensaje);
        }
        catch (Exception ex) { _logger.LogError(ex, "[Contadora robot] Error bajando de AFIP"); }
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
