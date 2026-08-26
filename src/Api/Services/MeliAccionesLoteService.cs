using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-26 — Acciones sobre las publicaciones TILDADAS en la pantalla nueva.
///
/// La idea del rediseño (Osmar, 25/08): en vez de botones que actúan sobre TODO el catálogo,
/// se filtra, se tilda y se aplica. Así se ve exactamente sobre qué se está actuando y se
/// puede hacer de a pocas — igual que en MercadoLibre, Real Trends o Shopify.
///
/// Este servicio NO trae lógica nueva de precios ni de stock: reusa los motores que ya existen
/// y que venimos probando (MeliItemService, MeliPricePushService, MeliStockPushService,
/// MeliListingTypeService). Sólo se encarga de aplicarlos a una lista elegida a mano.
///
/// Las acciones están separadas en dos grupos a propósito:
///   • SEGURAS: no cambian nada en MeLi (actualizar comisiones, prender/apagar sincro, objetivo).
///   • QUE TOCAN MELI: precios, stock, tipo de publicación, pausar. Esas piden confirmación
///     en la pantalla y van con freno entre llamadas.
/// </summary>
public class MeliAccionesLoteService
{
    private readonly AppDbContext _db;
    private readonly MeliItemService _itemService;
    private readonly MeliPricePushService _pricePush;
    private readonly MeliStockPushService _stockPush;
    private readonly ILogger<MeliAccionesLoteService> _logger;

    // Tope por tanda: si se pide más, la pantalla lo parte. Evita procesos eternos sin control.
    private const int MAX_POR_TANDA = 300;
    private const int THROTTLE_MS = 250;

    public MeliAccionesLoteService(AppDbContext db, MeliItemService itemService,
        MeliPricePushService pricePush, MeliStockPushService stockPush,
        ILogger<MeliAccionesLoteService> logger)
    {
        _db = db;
        _itemService = itemService;
        _pricePush = pricePush;
        _stockPush = stockPush;
        _logger = logger;
    }

    public record FilaResultado(string MeliItemId, bool Ok, string Mensaje);
    public record Resultado(int Pedidas, int Ok, int Errores, List<FilaResultado> Detalle);

    /// <summary>Le vuelve a preguntar a MeLi cuánto cobra por cada una. No cambia nada.</summary>
    public async Task<Resultado> ActualizarComisionesAsync(List<string> mlas, CancellationToken ct = default)
    {
        var (lista, detalle) = Preparar(mlas);
        int ok = 0, err = 0;
        foreach (var mla in lista)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                if (await _itemService.RefreshSaleFeeAsync(mla)) { ok++; detalle.Add(new(mla, true, "comisión actualizada")); }
                else { err++; detalle.Add(new(mla, false, "MeLi no devolvió la comisión")); }
            }
            catch (Exception ex) { err++; detalle.Add(new(mla, false, ex.Message)); }
            await Esperar(ct);
        }
        _logger.LogInformation("[AccionesLote] Comisiones: {Ok} ok, {Err} error (de {N})", ok, err, lista.Count);
        return new Resultado(mlas.Count, ok, err, detalle);
    }

    /// <summary>Prende o apaga el sincro de precio y/o de stock. Sólo toca la configuración
    /// del sistema: no pushea nada a MeLi en el momento.</summary>
    public async Task<Resultado> CambiarSincroAsync(List<string> mlas, bool? precio, bool? stock,
        CancellationToken ct = default)
    {
        if (precio is null && stock is null)
            return new Resultado(0, 0, 0, new() { new("", false, "No pediste cambiar ni precio ni stock") });

        var (lista, detalle) = Preparar(mlas);
        int ok = 0, err = 0;
        foreach (var mla in lista)
        {
            try
            {
                var cfg = await _db.MeliItemSyncConfigs.FirstOrDefaultAsync(c => c.MeliItemId == mla, ct);
                if (cfg is null)
                {
                    cfg = new MeliItemSyncConfig { MeliItemId = mla, CreatedAt = DateTime.UtcNow };
                    _db.MeliItemSyncConfigs.Add(cfg);
                }
                if (precio.HasValue) cfg.SyncPrecio = precio.Value;
                if (stock.HasValue) cfg.SyncStock = stock.Value;
                cfg.UpdatedAt = DateTime.UtcNow;
                ok++;
                detalle.Add(new(mla, true, Describir(precio, stock)));
            }
            catch (Exception ex) { err++; detalle.Add(new(mla, false, ex.Message)); }
        }
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("[AccionesLote] Sincro ({Que}): {Ok} ok, {Err} error", Describir(precio, stock), ok, err);
        return new Resultado(mlas.Count, ok, err, detalle);
    }

    /// <summary>Guarda el objetivo de ganancia. Si `aplicarAhora`, además pushea el precio nuevo
    /// (nunca por debajo del piso sugerido, que es lo que ya hace el motor de precios).</summary>
    public async Task<Resultado> PonerObjetivoAsync(List<string> mlas, decimal objetivoPct,
        bool aplicarAhora, CancellationToken ct = default)
    {
        if (objetivoPct is <= 0 or > 500)
            return new Resultado(0, 0, 0, new() { new("", false, "El objetivo tiene que estar entre 1% y 500%") });

        var (lista, detalle) = Preparar(mlas);
        int ok = 0, err = 0;
        foreach (var mla in lista)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var cfg = await _db.MeliItemSyncConfigs.FirstOrDefaultAsync(c => c.MeliItemId == mla, ct);
                if (cfg is null)
                {
                    cfg = new MeliItemSyncConfig { MeliItemId = mla, CreatedAt = DateTime.UtcNow };
                    _db.MeliItemSyncConfigs.Add(cfg);
                }
                cfg.GananciaObjetivoPct = objetivoPct;
                cfg.GananciaObjetivoAt = DateTime.UtcNow;
                cfg.UpdatedAt = DateTime.UtcNow;
                if (aplicarAhora) cfg.SyncPrecio = true;   // sin esto el objetivo queda guardado pero nadie lo mantiene
                await _db.SaveChangesAsync(ct);

                if (!aplicarAhora)
                {
                    ok++;
                    detalle.Add(new(mla, true, $"objetivo {objetivoPct:0.#}% guardado"));
                    continue;
                }

                var dbId = await _db.MeliItems.AsNoTracking()
                    .Where(m => m.MeliItemId == mla && m.VariationId == null)
                    .Select(m => m.Id).FirstOrDefaultAsync(ct);
                if (dbId == 0) { err++; detalle.Add(new(mla, false, "publicación no encontrada")); continue; }

                var pr = await _pricePush.PushPrecioForItemAsync(dbId, markAsClaimed: true, ct);
                if (pr.Ok) { ok++; detalle.Add(new(mla, true, $"objetivo {objetivoPct:0.#}% → ${pr.PushedPrice:N0}")); }
                else { err++; detalle.Add(new(mla, false, pr.Message)); }
            }
            catch (Exception ex) { err++; detalle.Add(new(mla, false, ex.Message)); }
            await Esperar(ct);
        }
        _logger.LogWarning("[AccionesLote] Objetivo {Pct}% (aplicar={Ap}): {Ok} ok, {Err} error",
            objetivoPct, aplicarAhora, ok, err);
        return new Resultado(mlas.Count, ok, err, detalle);
    }

    /// <summary>Pushea a MeLi el precio que el sistema calcula hoy para cada una.</summary>
    public async Task<Resultado> PushearPrecioAsync(List<string> mlas, CancellationToken ct = default)
    {
        var (lista, detalle) = Preparar(mlas);
        int ok = 0, err = 0;
        foreach (var mla in lista)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var dbId = await _db.MeliItems.AsNoTracking()
                    .Where(m => m.MeliItemId == mla && m.VariationId == null)
                    .Select(m => m.Id).FirstOrDefaultAsync(ct);
                if (dbId == 0) { err++; detalle.Add(new(mla, false, "publicación no encontrada")); continue; }

                var pr = await _pricePush.PushPrecioForItemAsync(dbId, markAsClaimed: false, ct);
                if (pr.Ok) { ok++; detalle.Add(new(mla, true, $"precio → ${pr.PushedPrice:N0}")); }
                else { err++; detalle.Add(new(mla, false, pr.Message)); }
            }
            catch (Exception ex) { err++; detalle.Add(new(mla, false, ex.Message)); }
            await Esperar(ct);
        }
        _logger.LogWarning("[AccionesLote] Precio: {Ok} ok, {Err} error (de {N})", ok, err, lista.Count);
        return new Resultado(mlas.Count, ok, err, detalle);
    }

    /// <summary>Manda a MeLi el stock del sistema. Usa el motor de siempre, con todas sus reglas
    /// (Full desenlazado, packs por el componente más escaso, precio primero si está pausada).</summary>
    public async Task<Resultado> PushearStockAsync(List<string> mlas, CancellationToken ct = default)
    {
        var (lista, detalle) = Preparar(mlas);
        var r = await _stockPush.PushStockForMeliItemsAsync(lista, ct);
        foreach (var m in r.Mensajes.Take(MAX_POR_TANDA))
        {
            var partes = m.Split(':', 2);
            detalle.Add(new(partes[0].Trim(), !m.Contains("Error", StringComparison.OrdinalIgnoreCase),
                partes.Length > 1 ? partes[1].Trim() : m));
        }
        _logger.LogWarning("[AccionesLote] Stock: {Ok} ok, {Skip} salteadas, {Err} error", r.Ok, r.Skipped, r.Errores);
        return new Resultado(mlas.Count, r.Ok, r.Errores, detalle);
    }

    // ── auxiliares ──

    private (List<string>, List<FilaResultado>) Preparar(List<string> mlas)
    {
        var lista = mlas.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(MAX_POR_TANDA).ToList();
        return (lista, new List<FilaResultado>());
    }

    private static async Task Esperar(CancellationToken ct)
    {
        try { await Task.Delay(THROTTLE_MS, ct); } catch (OperationCanceledException) { }
    }

    private static string Describir(bool? precio, bool? stock)
    {
        var p = new List<string>();
        if (precio.HasValue) p.Add(precio.Value ? "precio sincronizado" : "precio a mano");
        if (stock.HasValue) p.Add(stock.Value ? "stock sincronizado" : "stock a mano");
        return string.Join(" · ", p);
    }
}
