using Microsoft.EntityFrameworkCore;
using Api.Data;
using Api.Models;

namespace Api.Services;

/// <summary>
/// 2026-09-26 — Relee de noche la comisión y el ENVÍO de TODAS las publicaciones activas.
///
/// MeLi cambia el costo de envío cuando re-mide los paquetes y lo avisa SOLO por mail
/// ("5 publicaciones con nuevo costo de envío"): no hay notificación por API. El sistema se
/// enteraba recién cuando alguien abría la publicación (medido el 26/09: de 3.442 activas, 227
/// revisadas en la última semana y 1.659 con datos de hace más de un mes), y el % de ganancia y el
/// "Mantener el N%" de las 04 ARG trabajaban con un envío viejo.
///
/// Corre a las 04 UTC (01 ARG; AppSettings meli.envios_nocturno.hora_utc), antes del vigilante de margen
/// (07 UTC), despacito (una publicación por segundo), y corta a las 2 h 45 min si no terminó (la noche
/// siguiente sigue por las más viejas).
/// NO cambia precios. Si el envío cambió SIN que cambie el precio (o sea, lo cambió MeLi), deja un
/// ENVIO_CAMBIO por publicación en Cambios MeLi y UN aviso resumen (ENVIOS_CAMBIARON) a la campanita
/// y Telegram. Interruptor: AppSettings meli.envios_nocturno.enabled = "true" (default apagado:
/// en desarrollo no tiene que salir a MeLi).
/// </summary>
public class MeliEnviosNocturnoService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliEnviosNocturnoService> _logger;

    private static readonly TimeSpan DURACION_MAX = TimeSpan.FromMinutes(165);   // 04:00 → 06:45 UTC, antes del vigilante
    private const decimal CAMBIO_MINIMO = 50m;   // menos que esto es ruido de redondeo de MeLi

    public MeliEnviosNocturnoService(IServiceScopeFactory scopeFactory, ILogger<MeliEnviosNocturnoService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(6), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ahora = DateTime.UtcNow;
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                if (ahora.Hour == await LeerHoraAsync(db, stoppingToken)
                    && await PrendidoAsync(db, stoppingToken) && !await CorrioHoyAsync(db, ahora, stoppingToken))
                    await RevisarAsync(ahora, stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Envíos nocturno] Falló el ciclo");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RevisarAsync(DateTime inicio, CancellationToken ct)
    {
        // Las más viejas primero: si no llega a todas, la noche siguiente sigue por ahí.
        List<string> ids;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await MarcarCorridaAsync(db, inicio, "arrancó", ct);
            ids = (await db.MeliItems.AsNoTracking()
                    .Where(m => m.Status == "active")
                    .GroupBy(m => m.MeliItemId)
                    .Select(g => new { Id = g.Key, Visto = g.Min(x => x.SaleFeeCapturedAt) })
                    .ToListAsync(ct))
                .OrderBy(x => x.Visto ?? DateTime.MinValue)
                .Select(x => x.Id)
                .ToList();
        }

        int revisadas = 0, errores = 0;
        var cambios = new List<string>();
        foreach (var mla in ids)
        {
            if (ct.IsCancellationRequested) break;
            if (DateTime.UtcNow - inicio >= DURACION_MAX) break;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var items = scope.ServiceProvider.GetRequiredService<MeliItemService>();

                // Misma fila que actualiza GetListingCostsAsync.
                var antes = await db.MeliItems.AsNoTracking().FirstOrDefaultAsync(i => i.MeliItemId == mla, ct);
                if (antes is null) continue;

                var (ok, _) = await items.RefreshSaleFeeConDetalleAsync(mla);
                if (!ok) { errores++; continue; }
                revisadas++;

                var fila = await db.MeliItems.FirstOrDefaultAsync(i => i.Id == antes.Id, ct);
                if (fila is null) continue;
                var envioAntes = antes.SaleFeeShippingCost ?? 0m;
                var envioAhora = fila.SaleFeeShippingCost ?? 0m;

                // Con envío gratis, un $0 es que MeLi no contestó el envío: se deja el que había.
                if (fila.FreeShipping && envioAhora == 0m && envioAntes > 0m)
                {
                    fila.SaleFeeShippingCost = envioAntes;
                    await db.SaveChangesAsync(ct);
                    continue;
                }

                // Sólo cuenta como "lo cambió MeLi" si el precio es el mismo que la vez anterior
                // (si cambió el precio, el envío puede cambiar de escalón por eso).
                var mismoPrecio = antes.SaleFeePriceSnapshot is decimal snap && snap == fila.Price;
                var ar = new System.Globalization.CultureInfo("es-AR");

                // 01/10/2026: cambio de FORMA de entrega (mail de MeLi "pasaron a tener Envíos en
                // Mercado Libre" / "dejaron de tener..."). Antes no se veía: sólo se miraba el costo.
                var logAntes = MeliPublicacionesV2Service.ClaveLogistica(antes.LogisticType);
                var logAhora = MeliPublicacionesV2Service.ClaveLogistica(fila.LogisticType);
                if (mismoPrecio && logAntes is not null && logAhora is not null && logAntes != logAhora)
                {
                    db.MeliCambiosDetectados.Add(new MeliCambioDetectado
                    {
                        MeliItemId = mla, MeliAccountId = fila.MeliAccountId, Sku = fila.Sku, Title = fila.Title,
                        Tipo = "ENVIO_MODO", ValorAnterior = logAntes, ValorNuevo = logAhora,
                        Source = "envios-noche", DetectedAt = DateTime.UtcNow, NotifiedAt = DateTime.UtcNow,
                        Notes = $"La forma de envío pasó de {logAntes} a {logAhora}"
                    });
                    await db.SaveChangesAsync(ct);
                    cambios.Add($"{Recortar(fila.Title ?? mla, 40)}: envío {logAntes} → {logAhora}");
                }

                // 01/10/2026: también de $0 a un costo (pasó a pagar envío), si ya lo habíamos leído antes.
                var yaLeido = antes.SaleFeeCapturedAt.HasValue;
                if (!mismoPrecio || (envioAntes <= 0m && !yaLeido) || Math.Abs(envioAhora - envioAntes) < CAMBIO_MINIMO) continue;

                var texto = $"El envío pasó de ${envioAntes.ToString("N0", ar)} a ${envioAhora.ToString("N0", ar)}";
                db.MeliCambiosDetectados.Add(new MeliCambioDetectado
                {
                    MeliItemId = mla, MeliAccountId = fila.MeliAccountId, Sku = fila.Sku, Title = fila.Title,
                    Tipo = "ENVIO_CAMBIO",
                    ValorAnterior = envioAntes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ValorNuevo = envioAhora.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Delta = envioAhora - envioAntes,
                    DeltaPct = envioAntes > 0m ? Math.Round((envioAhora - envioAntes) / envioAntes * 100m, 1) : null,
                    Source = "envios-noche", DetectedAt = DateTime.UtcNow,
                    NotifiedAt = DateTime.UtcNow,   // se avisa en el resumen, no de a una
                    Notes = texto
                });
                await db.SaveChangesAsync(ct);
                cambios.Add($"{Recortar(fila.Title ?? mla, 40)}: ${envioAntes.ToString("N0", ar)} → ${envioAhora.ToString("N0", ar)}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                errores++;
                _logger.LogWarning(ex, "[Envíos nocturno] {Mla}: no se pudo releer", mla);
            }

            try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { break; }
        }

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            if (cambios.Count > 0)
            {
                // Notes admite 500: las primeras 5 y el resto en Cambios MeLi.
                var detalle = string.Join("\n", cambios.Take(5))
                              + (cambios.Count > 5 ? $"\n… y {cambios.Count - 5} más (ver Cambios MeLi)" : "");
                if (detalle.Length > 500) detalle = detalle[..499] + "…";
                db.MeliCambiosDetectados.Add(new MeliCambioDetectado
                {
                    MeliItemId = "-",
                    Title = $"{cambios.Count} publicaciones con nuevo costo de envío",
                    Tipo = "ENVIOS_CAMBIARON",
                    Delta = cambios.Count,
                    Source = "envios-noche", DetectedAt = DateTime.UtcNow,
                    Notes = detalle
                });
            }
            var resumen = $"{revisadas} de {ids.Count} revisadas, {cambios.Count} con envío nuevo, {errores} con error";
            await MarcarCorridaAsync(db, inicio, resumen, ct);
            _logger.LogWarning("[Envíos nocturno] {Resumen}", resumen);
        }
    }

    private static string Recortar(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static async Task<int> LeerHoraAsync(AppDbContext db, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == "meli.envios_nocturno.hora_utc", ct);
        return s != null && int.TryParse(s.Value, out var h) && h is >= 0 and <= 23 ? h : 4; // 04 UTC = 01 ARG
    }

    private static async Task<bool> PrendidoAsync(AppDbContext db, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == "meli.envios_nocturno.enabled", ct);
        var v = s?.Value?.Trim().ToLowerInvariant();
        return v is "true" or "1" or "on";
    }

    private static async Task<bool> CorrioHoyAsync(AppDbContext db, DateTime ahora, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == "meli.envios_nocturno.ultima_corrida", ct);
        return s?.Value is { Length: >= 10 } v && v[..10] == ahora.ToString("yyyy-MM-dd");
    }

    private static async Task MarcarCorridaAsync(AppDbContext db, DateTime ahora, string detalle, CancellationToken ct)
    {
        var s = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == "meli.envios_nocturno.ultima_corrida", ct);
        var valor = $"{ahora:yyyy-MM-dd HH:mm} UTC · {detalle}";
        if (s is null) db.AppSettings.Add(new AppSetting { Key = "meli.envios_nocturno.ultima_corrida", Value = valor });
        else s.Value = valor;
        await db.SaveChangesAsync(ct);
    }
}
