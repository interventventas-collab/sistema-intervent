using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-25 — Cambia el TIPO DE PUBLICACIÓN de una publicación MeLi ya publicada
/// (Premium `gold_pro` ↔ Clásica `gold_special`).
///
/// Por qué existe: muchas fichas de producto tienen DOS publicaciones (la normal y su gemela
/// de catálogo) con tipos distintos. La Clásica cobra la mitad de comisión (~15% vs ~29%) y en
/// los últimos 30 días se llevó el 94% de las ventas. Osmar decidió (25/08) emparejarlas todas
/// a Clásica, empezando por las Premium que no vendieron nada en 90 días.
///
/// MeLi permite subir y bajar de tipo sin cargo, así que el cambio es REVERSIBLE: para volver
/// atrás se llama de nuevo con `gold_pro`. Cada cambio queda registrado en MeliCambiosDetectados
/// (Tipo=LISTING_TYPE) con el tipo anterior, para poder revertir en masa si hace falta.
///
/// Después de cambiar el tipo, la comisión que MeLi cobra cambia → se recaptura el sale_fee y
/// (si `recalcularPrecio`) se pushea el precio nuevo: mismo objetivo de ganancia, menos comisión,
/// precio más barato (~15% menos).
/// </summary>
public class MeliListingTypeService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly MeliItemService _itemService;
    private readonly MeliPricePushService _pricePush;
    private readonly ILogger<MeliListingTypeService> _logger;

    public const string CLASICA = "gold_special";
    public const string PREMIUM = "gold_pro";

    public MeliListingTypeService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService accountService, MeliItemService itemService,
        MeliPricePushService pricePush, ILogger<MeliListingTypeService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _itemService = itemService;
        _pricePush = pricePush;
        _logger = logger;
    }

    public record CambioResult(string MeliItemId, bool Ok, string? TipoAnterior, string? TipoNuevo,
        decimal? PrecioAnterior, decimal? PrecioNuevo, string Mensaje);

    public record LoteResult(int Procesadas, int Ok, int Saltadas, int Errores, List<CambioResult> Detalle);

    /// <summary>Cambia el tipo de una publicación. Devuelve Ok=false con motivo si no se pudo
    /// (no es error fatal: el lote sigue con la siguiente).</summary>
    public async Task<CambioResult> CambiarTipoAsync(string meliItemId, string nuevoTipo,
        bool recalcularPrecio = true, CancellationToken ct = default)
    {
        if (nuevoTipo != CLASICA && nuevoTipo != PREMIUM)
            return new CambioResult(meliItemId, false, null, null, null, null,
                $"Tipo '{nuevoTipo}' no válido (solo {CLASICA} o {PREMIUM})");

        var item = await _db.MeliItems
            .Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId && i.VariationId == null, ct);
        if (item is null)
            return new CambioResult(meliItemId, false, null, null, null, null, "Publicación no encontrada en el sistema");
        if (item.MeliAccount is null)
            return new CambioResult(meliItemId, false, null, null, null, null, "Sin cuenta MeLi asociada");

        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        if (string.IsNullOrWhiteSpace(token))
            return new CambioResult(meliItemId, false, null, null, null, null, "Token MeLi inválido");

        using var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromSeconds(30);

        // 1) Leer el estado REAL en MeLi (el cacheado puede estar viejo).
        string? tipoActual;
        string? statusActual;
        decimal? precioAntes;
        try
        {
            var getResp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}", ct);
            if (!getResp.IsSuccessStatusCode)
                return new CambioResult(meliItemId, false, null, null, null, null, $"GET item {(int)getResp.StatusCode}");
            var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync(ct)).RootElement;
            tipoActual = doc.TryGetProperty("listing_type_id", out var lt) ? lt.GetString() : null;
            statusActual = doc.TryGetProperty("status", out var st) ? st.GetString() : null;
            precioAntes = doc.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Number
                ? pr.GetDecimal() : (decimal?)null;
        }
        catch (Exception ex)
        {
            return new CambioResult(meliItemId, false, null, null, null, null, "GET item falló: " + ex.Message);
        }

        if (string.Equals(tipoActual, nuevoTipo, StringComparison.OrdinalIgnoreCase))
        {
            // Ya estaba en el tipo pedido: sincronizamos el cache local y listo.
            if (item.ListingTypeId != nuevoTipo) { item.ListingTypeId = nuevoTipo; await _db.SaveChangesAsync(ct); }
            return new CambioResult(meliItemId, false, tipoActual, tipoActual, precioAntes, precioAntes,
                "Ya estaba en ese tipo — no se tocó");
        }
        if (string.Equals(statusActual, "closed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(statusActual, "under_review", StringComparison.OrdinalIgnoreCase))
            return new CambioResult(meliItemId, false, tipoActual, null, precioAntes, null,
                $"Publicación en estado '{statusActual}' — no se toca");

        // 2) Pedirle el cambio a MeLi.
        //    Camino principal: POST /items/{id}/listing_type  body {"id":"gold_special"}
        //    Fallback: PUT /items/{id} body {"listing_type_id":"gold_special"} (algunas categorías
        //    lo aceptan por esta vía). Se prueban en ese orden y se reporta el error REAL de MeLi.
        var (ok, msgMeli) = await PedirCambioAMeliAsync(http, meliItemId, nuevoTipo, ct);
        if (!ok)
            return new CambioResult(meliItemId, false, tipoActual, null, precioAntes, null, msgMeli);

        // 3) Quedó cambiado: actualizar cache local + dejar registro para poder revertir.
        item.ListingTypeId = nuevoTipo;
        item.UpdatedAt = DateTime.UtcNow;
        _db.MeliCambiosDetectados.Add(new MeliCambioDetectado
        {
            MeliItemId = meliItemId,
            MeliAccountId = item.MeliAccountId,
            Sku = item.Sku,
            Title = item.Title,
            Tipo = "LISTING_TYPE",
            ValorAnterior = tipoActual,
            ValorNuevo = nuevoTipo,
            Source = "listing-type",
            DetectedAt = DateTime.UtcNow,
            // Se marca como visto: no es una alerta para revisar, es un cambio que hicimos a propósito.
            SeenAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync(ct);

        // 4) Recalcular el precio — con TRES frenos aprendidos en la prueba del 25/08:
        //
        //    FRENO 1 (precio a mano): si la publicación NO tiene el precio automático prendido
        //    (SyncPrecio=0), el precio lo maneja el dueño → NO se toca. En la prueba se le pisó
        //    el precio a mano de un set de orinales ($19.999 → $11.549). De las 152 candidatas,
        //    50 están en esta situación.
        //
        //    FRENO 2 (comisión todavía vieja): MeLi tarda unos segundos en aplicar el tipo nuevo.
        //    Si se le pregunta la comisión al toque, devuelve la de Premium (en la prueba quedó
        //    34,20% cuando la Clásica de al lado cobra 15%) y el precio sale calculado con el
        //    número equivocado. Se reintenta hasta que la comisión cambie de verdad.
        //
        //    FRENO 3 (más barata que sus hermanas): si el precio nuevo deja la publicación por
        //    debajo del de las otras publicaciones de la MISMA ficha, se deshace el cambio de
        //    precio y se avisa — regalar margen en la misma ficha no tiene sentido.
        decimal? precioNuevo = null;
        string notaPrecio;

        var syncPrecio = await _db.MeliItemSyncConfigs.AsNoTracking()
            .Where(c => c.MeliItemId == meliItemId).Select(c => (bool?)c.SyncPrecio).FirstOrDefaultAsync(ct);

        if (!recalcularPrecio)
        {
            notaPrecio = " (precio sin tocar, a pedido)";
        }
        else if (syncPrecio != true)
        {
            notaPrecio = " · precio A MANO: no se toca";
        }
        else
        {
            var comisionAntes = item.SaleFeePercentageFee;
            bool comisionActualizada = false;
            foreach (var esperaMs in new[] { 3000, 6000, 10000 })
            {
                await Task.Delay(esperaMs, ct);
                await _itemService.RefreshSaleFeeAsync(meliItemId);
                await _db.Entry(item).ReloadAsync(ct);
                if (item.SaleFeePercentageFee > 0 && item.SaleFeePercentageFee != comisionAntes)
                {
                    comisionActualizada = true;
                    break;
                }
            }

            if (!comisionActualizada)
            {
                notaPrecio = $" ⚠ MeLi sigue devolviendo la comisión vieja ({comisionAntes:0.##}%) — precio SIN recalcular";
            }
            else
            {
                var pr = await _pricePush.PushPrecioForItemAsync(item.Id, markAsClaimed: false, ct);
                if (!pr.Ok)
                {
                    notaPrecio = $" ⚠ precio NO recalculado: {pr.Message}";
                }
                else
                {
                    precioNuevo = pr.PushedPrice;
                    notaPrecio = $" · comisión {comisionAntes:0.##}% → {item.SaleFeePercentageFee:0.##}% · precio a ${pr.PushedPrice:N0}";

                    // FRENO 3: ¿quedó más barata que sus hermanas de la misma ficha?
                    var pisoHermanas = await PrecioMinimoHermanasAsync(item, ct);
                    if (pisoHermanas.HasValue && precioNuevo.HasValue
                        && precioNuevo.Value < pisoHermanas.Value * 0.95m)
                    {
                        var volvio = precioAntes.HasValue
                            && await RestaurarPrecioEnMeliAsync(http, meliItemId, precioAntes.Value, ct);
                        if (volvio)
                        {
                            item.Price = precioAntes!.Value;
                            await _db.SaveChangesAsync(ct);
                            precioNuevo = precioAntes;
                            notaPrecio = $" ⚠ el precio nuevo (${pr.PushedPrice:N0}) quedaba MUY por debajo de sus hermanas " +
                                         $"(${pisoHermanas.Value:N0}) — se dejó el de antes (${precioAntes:N0}). Revisar a mano.";
                        }
                        else
                        {
                            notaPrecio += $" ⚠ OJO: quedó por debajo de sus hermanas (${pisoHermanas.Value:N0}) y no se pudo volver atrás";
                        }
                    }
                }
            }
        }

        _logger.LogInformation("[ListingType] {Mla} {Viejo} → {Nuevo}{Nota}", meliItemId, tipoActual, nuevoTipo, notaPrecio);
        return new CambioResult(meliItemId, true, tipoActual, nuevoTipo, precioAntes, precioNuevo,
            $"Cambiada a {(nuevoTipo == CLASICA ? "Clásica" : "Premium")}{notaPrecio}");
    }

    /// <summary>Precio más bajo de las OTRAS publicaciones activas de la misma ficha de producto.
    /// Sirve de piso: una publicación no debería quedar muy por debajo de sus propias hermanas.</summary>
    private async Task<decimal?> PrecioMinimoHermanasAsync(MeliItem item, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.UserProductId)) return null;
        var precios = await _db.MeliItems.AsNoTracking()
            .Where(x => x.UserProductId == item.UserProductId
                        && x.MeliItemId != item.MeliItemId
                        && x.VariationId == null
                        && x.Status == "active"
                        && x.Price > 0)
            .Select(x => x.Price)
            .ToListAsync(ct);
        return precios.Count > 0 ? precios.Min() : (decimal?)null;
    }

    /// <summary>Vuelve a poner un precio en MeLi (marcha atrás del recálculo). Contempla variantes.</summary>
    private async Task<bool> RestaurarPrecioEnMeliAsync(HttpClient http, string meliItemId, decimal precio, CancellationToken ct)
    {
        try
        {
            var variantIds = new List<long>();
            var getResp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}?attributes=variations", ct);
            if (getResp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await getResp.Content.ReadAsStringAsync(ct));
                if (doc.RootElement.TryGetProperty("variations", out var vs) && vs.ValueKind == JsonValueKind.Array)
                    foreach (var v in vs.EnumerateArray()) variantIds.Add(v.GetProperty("id").GetInt64());
            }
            object payload = variantIds.Count > 0
                ? new { variations = variantIds.Select(id => new { id, price = precio }).ToList() }
                : (object)new { price = precio };
            var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", body, ct);
            return resp.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ListingType] {Mla}: no se pudo restaurar el precio ${P}", meliItemId, precio);
            return false;
        }
    }

    /// <summary>Restaura los precios que esta herramienta pisó cuando no debía (publicaciones con
    /// el precio A MANO). Lee los registros PRECIO_PISADO sin marcar y les devuelve el precio viejo.</summary>
    public async Task<LoteResult> RestaurarPreciosPisadosAsync(CancellationToken ct = default)
    {
        var pendientes = await _db.MeliCambiosDetectados
            .Where(c => c.Tipo == "PRECIO_PISADO" && c.Source == "listing-type" && c.SeenAt == null)
            .ToListAsync(ct);

        var detalle = new List<CambioResult>();
        int ok = 0, err = 0;
        foreach (var reg in pendientes)
        {
            if (!decimal.TryParse(reg.ValorAnterior, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var precioViejo) || precioViejo <= 0)
            {
                err++;
                detalle.Add(new CambioResult(reg.MeliItemId, false, null, null, null, null, "Sin precio anterior válido"));
                continue;
            }

            var item = await _db.MeliItems.Include(i => i.MeliAccount)
                .FirstOrDefaultAsync(i => i.MeliItemId == reg.MeliItemId && i.VariationId == null, ct);
            if (item?.MeliAccount is null)
            {
                err++;
                detalle.Add(new CambioResult(reg.MeliItemId, false, null, null, null, null, "Publicación o cuenta no encontrada"));
                continue;
            }
            var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
            if (string.IsNullOrWhiteSpace(token))
            {
                err++;
                detalle.Add(new CambioResult(reg.MeliItemId, false, null, null, null, null, "Token MeLi inválido"));
                continue;
            }

            using var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var precioActual = item.Price;
            var volvio = await RestaurarPrecioEnMeliAsync(http, reg.MeliItemId, precioViejo, ct);
            if (volvio)
            {
                item.Price = precioViejo;
                item.UpdatedAt = DateTime.UtcNow;
                reg.SeenAt = DateTime.UtcNow;
                await _db.SaveChangesAsync(ct);
                ok++;
                _logger.LogWarning("[ListingType] {Mla} precio RESTAURADO ${Viejo} (estaba en ${Actual})",
                    reg.MeliItemId, precioViejo, precioActual);
                detalle.Add(new CambioResult(reg.MeliItemId, true, null, null, precioActual, precioViejo,
                    $"Precio restaurado a ${precioViejo:N0}"));
            }
            else
            {
                err++;
                detalle.Add(new CambioResult(reg.MeliItemId, false, null, null, precioActual, null, "MeLi rechazó el precio"));
            }
            await Task.Delay(300, ct);
        }
        return new LoteResult(pendientes.Count, ok, 0, err, detalle);
    }

    /// <summary>Le pide el cambio a MeLi. Prueba el endpoint dedicado y, si no lo acepta, el PUT del item.</summary>
    private async Task<(bool Ok, string Mensaje)> PedirCambioAMeliAsync(HttpClient http, string meliItemId,
        string nuevoTipo, CancellationToken ct)
    {
        // Camino 1: endpoint dedicado de cambio de tipo.
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(new { id = nuevoTipo }), Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"https://api.mercadolibre.com/items/{meliItemId}/listing_type", body, ct);
            if (resp.IsSuccessStatusCode) return (true, "ok");
            var err = Trim(await resp.Content.ReadAsStringAsync(ct));
            _logger.LogWarning("[ListingType] {Mla} POST listing_type {Code}: {Err} — pruebo el PUT", meliItemId, (int)resp.StatusCode, err);

            // Camino 2 (fallback): PUT del item.
            var body2 = new StringContent(JsonSerializer.Serialize(new { listing_type_id = nuevoTipo }), Encoding.UTF8, "application/json");
            var resp2 = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", body2, ct);
            if (resp2.IsSuccessStatusCode) return (true, "ok (por PUT)");
            var err2 = Trim(await resp2.Content.ReadAsStringAsync(ct));
            return (false, $"MeLi rechazó el cambio. POST {(int)resp.StatusCode}: {err} | PUT {(int)resp2.StatusCode}: {err2}");
        }
        catch (Exception ex)
        {
            return (false, "Error llamando a MeLi: " + ex.Message);
        }
    }

    /// <summary>Cambia el tipo de una lista de publicaciones, de a una, con freno para no
    /// pasarse del límite de MeLi. Nunca corta el lote por un error: lo anota y sigue.</summary>
    public async Task<LoteResult> CambiarTipoLoteAsync(List<string> meliItemIds, string nuevoTipo,
        bool recalcularPrecio = true, CancellationToken ct = default)
    {
        var detalle = new List<CambioResult>();
        int ok = 0, saltadas = 0, errores = 0;

        foreach (var id in meliItemIds)
        {
            if (ct.IsCancellationRequested) break;
            CambioResult r;
            try { r = await CambiarTipoAsync(id, nuevoTipo, recalcularPrecio, ct); }
            catch (Exception ex)
            {
                r = new CambioResult(id, false, null, null, null, null, "Error inesperado: " + ex.Message);
            }
            detalle.Add(r);
            if (r.Ok) ok++;
            else if (r.Mensaje.Contains("Ya estaba") || r.Mensaje.Contains("no se toca")) saltadas++;
            else errores++;

            await Task.Delay(400, ct); // ~2,5 por segundo: el cambio de tipo + precio son varias llamadas
        }

        _logger.LogWarning("[ListingType] LOTE terminado: {Ok} cambiadas, {Skip} saltadas, {Err} con error (de {Total})",
            ok, saltadas, errores, meliItemIds.Count);
        return new LoteResult(meliItemIds.Count, ok, saltadas, errores, detalle);
    }

    /// <summary>Publicaciones candidatas a emparejar: comparten ficha de producto (UserProductId)
    /// con otra publicación de tipo distinto, están ACTIVAS y son Premium.
    /// `soloSinVentas`=true deja solo las que no vendieron nada en los últimos `diasSinVenta` días
    /// (el "lote 1" que definió Osmar: las que no tienen nada que perder).</summary>
    public async Task<List<CandidataDto>> GetCandidatasAsync(bool soloSinVentas = true,
        int diasSinVenta = 90, CancellationToken ct = default)
    {
        var desde = DateTime.UtcNow.AddDays(-diasSinVenta);

        // Fichas (UserProductId) que tienen publicaciones de MÁS DE UN tipo.
        var fichasMixtas = await _db.MeliItems
            .Where(m => m.VariationId == null && m.UserProductId != null
                        && m.Status != "closed" && m.Status != "deleted")
            .GroupBy(m => m.UserProductId!)
            .Where(g => g.Select(x => x.ListingTypeId).Distinct().Count() > 1)
            .Select(g => g.Key)
            .ToListAsync(ct);

        var premium = await _db.MeliItems
            .Where(m => m.VariationId == null && m.Status == "active"
                        && m.ListingTypeId == PREMIUM
                        && m.UserProductId != null && fichasMixtas.Contains(m.UserProductId))
            .Select(m => new
            {
                m.MeliItemId, m.Sku, m.Title, m.Price, m.InstallmentTag, m.SaleFeePercentageFee, m.UserProductId
            })
            .ToListAsync(ct);

        var ids = premium.Select(p => p.MeliItemId).ToList();
        var conVenta = await _db.MeliOrders
            .Where(o => ids.Contains(o.ItemId) && o.DateCreated >= desde)
            .Select(o => o.ItemId)
            .Distinct()
            .ToListAsync(ct);

        return premium
            .Where(p => !soloSinVentas || !conVenta.Contains(p.MeliItemId))
            .Select(p => new CandidataDto(p.MeliItemId, p.Sku, p.Title, p.Price, p.InstallmentTag,
                p.SaleFeePercentageFee, p.UserProductId, conVenta.Contains(p.MeliItemId)))
            .OrderByDescending(p => p.ComisionPct ?? 0)
            .ToList();
    }

    public record CandidataDto(string MeliItemId, string? Sku, string? Title, decimal Precio,
        string? Cuotas, decimal? ComisionPct, string? UserProductId, bool VendioReciente);

    private static string Trim(string s) => s.Length > 300 ? s.Substring(0, 300) : s;
}
