using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-10: "Piso de margen 50% masivo". Recorre todas las publicaciones activas/pausadas y,
/// reusando el motor de precios que YA existe (nada de matemática nueva), decide por publicación:
///   - SUBE:         margen actual &lt; piso → propone el precio para llegar al piso (con envío real).
///   - YA_OK:        margen actual ≥ piso → NO se toca.
///   - NO_CONFIABLE: no se pudo calcular el margen con confianza (comisión/envío sin cachear fresco).
///   - SIN_COSTO / SIN_BASE: falta el dato en el sistema.
/// La VISTA PREVIA (preview) escribe el informe en Cafe_PisoMasivo_Resultado SIN tocar precios.
/// APLICAR reusa ese informe: setea objetivo de ganancia + prende precio/stock y pushea con
/// <see cref="MeliPricePushService.PushPrecioForItemAsync"/> (que trae el candado anti-precio-absurdo
/// y el piso sugerido). Corre en background; el progreso va por <see cref="SyncProgressService"/>.
/// </summary>
public class MeliPisoMasivoService
{
    private readonly AppDbContext _db;
    private readonly MeliPricePushService _pushSvc;
    private readonly MeliItemService _itemSvc;
    private readonly SyncProgressService _progress;
    private readonly ILogger<MeliPisoMasivoService> _logger;

    public MeliPisoMasivoService(
        AppDbContext db,
        MeliPricePushService pushSvc,
        MeliItemService itemSvc,
        SyncProgressService progress,
        ILogger<MeliPisoMasivoService> logger)
    {
        _db = db;
        _pushSvc = pushSvc;
        _itemSvc = itemSvc;
        _progress = progress;
        _logger = logger;
    }

    private const int ThrottleMs = 200;   // entre llamadas a MeLi, para no saturar

    // Tope de precio "razonable": lo mismo que el candado de MeliPricePushService. Al APLICAR, las filas
    // cuyo precio nuevo supere esto NO se intentan (son datos rotos, ej costo trucho $1.500.000 que da
    // ~$4.392.300). Así el aplicar queda limpio y no ensucia con errores. Van a revisión aparte.
    private const decimal TopePrecioSeguro = 2_000_000m;

    /// <summary>Vista previa: calcula y guarda el informe SIN tocar precios ni MeLi (salvo refrescos de costo).</summary>
    public async Task RunPreviewAsync(string runId, string progressId, decimal gananciaPct,
        string[] estados, bool refrescarEnVivo, CancellationToken ct)
    {
        // Borrar cualquier resultado previo de ESTE runId (re-corridas)
        await _db.MeliPisoMasivoResultados.Where(r => r.RunId == runId).ExecuteDeleteAsync(ct);

        var items = await _db.MeliItems
            .Where(i => estados.Contains(i.Status))
            .OrderBy(i => i.Id)
            .ToListAsync(ct);

        _progress.Update(progressId, p => { p.TotalItemsFound = items.Count; p.CurrentStep = "Calculando…"; });
        _logger.LogWarning("[PisoMasivo] Preview {Run}: {N} publicaciones, piso {Pct}%, refrescar={R}",
            runId, items.Count, gananciaPct, refrescarEnVivo);

        int hechos = 0, suben = 0, yaOk = 0, noConf = 0;
        var buffer = new List<MeliPisoMasivoResultado>();

        foreach (var it in items)
        {
            if (ct.IsCancellationRequested) break;
            var row = new MeliPisoMasivoResultado
            {
                RunId = runId, GananciaPct = gananciaPct, MeliItemId = it.MeliItemId,
                ItemDbId = it.Id, Titulo = it.Title, Sku = it.Sku, Status = it.Status,
                PrecioActual = it.Price, CreatedAt = DateTime.UtcNow
            };

            try
            {
                var (precioBase, hasBase) = await _pushSvc.CalcularPrecioBaseAsync(it, ct);
                row.PrecioBase = hasBase ? precioBase : (decimal?)null;

                var costo = await _pushSvc.CalcularCostoTotalAsync(it, ct);
                row.Costo = costo;

                if (!hasBase) { row.Accion = "SIN_BASE"; row.Mensaje = "Sin precio base del sistema"; }
                else if (costo is null || costo.Value <= 0) { row.Accion = "SIN_COSTO"; row.Mensaje = "Sin costo cargado en el sistema"; }
                else
                {
                    // Margen actual con envío (confiable solo si comisión/envío cacheado fresco).
                    var (margen, confiable) = await _pushSvc.CalcularMargenActualAsync(it, null, ct);

                    // Si no es confiable y se pidió refrescar, traemos costos en vivo de MeLi y reintentamos.
                    if (!confiable && refrescarEnVivo)
                    {
                        try
                        {
                            await _itemSvc.GetListingCostsAsync(it.MeliItemId);
                            await _db.Entry(it).ReloadAsync(ct);       // ver comisión/envío recién cacheados
                            (margen, confiable) = await _pushSvc.CalcularMargenActualAsync(it, null, ct);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "[PisoMasivo] refresco costos falló {Mla}", it.MeliItemId);
                        }
                        await Task.Delay(ThrottleMs, ct);
                    }

                    row.Confiable = confiable;
                    row.MargenActual = margen.HasValue ? Math.Round(margen.Value, 2) : (decimal?)null;

                    if (!confiable)
                    {
                        row.Accion = "NO_CONFIABLE";
                        row.Mensaje = refrescarEnVivo
                            ? "No se pudo calcular el margen con confianza ni refrescando"
                            : "Comisión/envío sin refrescar — margen no confiable";
                        noConf++;
                    }
                    else if (margen.HasValue && margen.Value >= gananciaPct)
                    {
                        row.Accion = "YA_OK";
                        row.PrecioNuevo = it.Price;
                        row.MargenNuevo = row.MargenActual;
                        row.Mensaje = $"Ya en {margen.Value:0.#}% (≥ piso) — no se toca";
                        yaOk++;
                    }
                    else
                    {
                        // Precio para llegar al piso, con envío real; nunca por debajo del sugerido.
                        var objetivo = await _pushSvc.CalcularPrecioParaGananciaAsync(it, gananciaPct, ct);
                        await Task.Delay(ThrottleMs, ct);   // esta cuenta consulta MeLi
                        if (objetivo is null)
                        {
                            row.Accion = "NO_CONFIABLE";
                            row.Confiable = false;
                            row.Mensaje = "No se pudo calcular el precio para el piso (costos en vivo)";
                            noConf++;
                        }
                        else
                        {
                            var elegido = objetivo.Value > precioBase ? objetivo.Value : precioBase;
                            row.PrecioNuevo = Math.Round(elegido, 2);
                            row.MargenNuevo = elegido > precioBase ? gananciaPct : (decimal?)null;
                            row.Accion = "SUBE";
                            row.Mensaje = elegido > precioBase
                                ? null
                                : "Queda en el precio sugerido (piso), el margen queda por encima";
                            suben++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                row.Accion = "ERROR";
                row.Mensaje = ex.Message;
                _logger.LogWarning(ex, "[PisoMasivo] error evaluando {Mla}", it.MeliItemId);
            }

            buffer.Add(row);
            hechos++;
            if (buffer.Count >= 50)
            {
                _db.MeliPisoMasivoResultados.AddRange(buffer);
                await _db.SaveChangesAsync(ct);
                buffer.Clear();
            }
            if (hechos % 10 == 0)
            {
                _progress.Update(progressId, p =>
                {
                    p.ItemsSynced = hechos;
                    p.Percentage = items.Count > 0 ? (int)(100.0 * hechos / items.Count) : 100;
                    p.CurrentStep = $"{hechos}/{items.Count} · suben {suben}, ok {yaOk}, revisar {noConf}";
                });
            }
        }

        if (buffer.Count > 0) { _db.MeliPisoMasivoResultados.AddRange(buffer); await _db.SaveChangesAsync(ct); }

        var resumen = $"Vista previa lista: {suben} suben, {yaOk} ya en {gananciaPct:0.#}%, {noConf} a revisar (de {items.Count}).";
        _progress.Update(progressId, p => { p.ItemsSynced = hechos; p.Percentage = 100; });
        _progress.Complete(progressId, resumen);
        _logger.LogWarning("[PisoMasivo] Preview {Run} COMPLETA — {Resumen}", runId, resumen);
    }

    /// <summary>Aplica lo previsualizado: solo las filas SUBE + confiables aún no aplicadas.
    /// Setea objetivo de ganancia + prende precio/stock y pushea con el motor real (con candado y piso).</summary>
    public async Task RunApplyAsync(string runId, string progressId, bool incluirPerdida, CancellationToken ct)
    {
        // Solo las sanas: precio nuevo dentro de rango (las "truchas" > tope, por costo roto, se saltean).
        // Y salvo que incluirPerdida=true, se saltean también las que hoy están A PÉRDIDA (margen < 0):
        // esas tienen el salto de precio más brusco y conviene revisarlas aparte antes de tocarlas.
        var pendientes = await _db.MeliPisoMasivoResultados
            .Where(r => r.RunId == runId && r.Accion == "SUBE" && r.Confiable && r.AplicadoOk == null
                     && r.PrecioNuevo != null && r.PrecioNuevo <= TopePrecioSeguro
                     && (incluirPerdida || r.MargenActual == null || r.MargenActual >= 0))
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

        // Cuántas quedan afuera por precio disparatado (para avisar en el resumen).
        int saltadasTruchas = await _db.MeliPisoMasivoResultados
            .CountAsync(r => r.RunId == runId && r.Accion == "SUBE" && r.Confiable && r.AplicadoOk == null
                          && (r.PrecioNuevo == null || r.PrecioNuevo > TopePrecioSeguro), ct);
        // Cuántas se dejan para después por estar a pérdida (solo cuando NO se piden incluir).
        int saltadasPerdida = incluirPerdida ? 0 : await _db.MeliPisoMasivoResultados
            .CountAsync(r => r.RunId == runId && r.Accion == "SUBE" && r.Confiable && r.AplicadoOk == null
                          && r.PrecioNuevo != null && r.PrecioNuevo <= TopePrecioSeguro && r.MargenActual < 0, ct);

        _progress.Update(progressId, p => { p.TotalItemsFound = pendientes.Count; p.CurrentStep = "Aplicando…"; });
        _logger.LogWarning("[PisoMasivo] Aplicar {Run}: {N} publicaciones ({T} truchas, {P} a pérdida salteadas)",
            runId, pendientes.Count, saltadasTruchas, saltadasPerdida);

        int hechos = 0, ok = 0, err = 0;
        foreach (var r in pendientes)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var cfg = await _db.MeliItemSyncConfigs.FindAsync(new object[] { r.MeliItemId }, ct);
                if (cfg is null)
                {
                    cfg = new MeliItemSyncConfig { MeliItemId = r.MeliItemId, CreatedAt = DateTime.UtcNow };
                    _db.MeliItemSyncConfigs.Add(cfg);
                }
                // Objetivo de ganancia = piso; limpia el ajuste viejo; prende precio + stock.
                cfg.GananciaObjetivoPct = r.GananciaPct;
                cfg.GananciaObjetivoAt = DateTime.UtcNow;
                cfg.AjustePct = 0m;
                cfg.AjusteFijo = 0m;
                cfg.SyncPrecio = true;
                cfg.SyncStock = true;
                cfg.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);

                var res = await _pushSvc.PushPrecioForItemAsync(r.ItemDbId, markAsClaimed: true, ct);
                r.AplicadoOk = res.Ok;
                r.AplicadoAt = DateTime.UtcNow;
                if (res.Ok) { ok++; if (res.PushedPrice.HasValue) r.PrecioNuevo = res.PushedPrice.Value; }
                else { err++; r.Mensaje = res.Message; }
                await _db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                err++;
                r.AplicadoOk = false;
                r.AplicadoAt = DateTime.UtcNow;
                r.Mensaje = ex.Message;
                await _db.SaveChangesAsync(ct);
                _logger.LogWarning(ex, "[PisoMasivo] error aplicando {Mla}", r.MeliItemId);
            }

            hechos++;
            if (hechos % 5 == 0)
            {
                _progress.Update(progressId, p =>
                {
                    p.ItemsSynced = hechos;
                    p.Percentage = pendientes.Count > 0 ? (int)(100.0 * hechos / pendientes.Count) : 100;
                    p.CurrentStep = $"{hechos}/{pendientes.Count} · aplicadas {ok}, errores {err}";
                });
            }
            await Task.Delay(ThrottleMs, ct);
        }

        var extra = "";
        if (saltadasPerdida > 0) extra += $" {saltadasPerdida} a pérdida quedaron para después.";
        if (saltadasTruchas > 0) extra += $" {saltadasTruchas} salteadas por precio disparatado.";
        var resumen = $"Aplicado: {ok} publicaciones actualizadas, {err} con error.{extra}";
        _progress.Update(progressId, p => { p.ItemsSynced = hechos; p.Percentage = 100; });
        _progress.Complete(progressId, resumen);
        _logger.LogWarning("[PisoMasivo] Aplicar {Run} COMPLETO — {Resumen}", runId, resumen);
    }
}
