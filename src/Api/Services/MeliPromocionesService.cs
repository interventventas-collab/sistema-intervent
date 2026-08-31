using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-31 — Promociones de MercadoLibre: saber cuáles publicaciones están vendiendo con
/// descuento y a qué precio.
///
/// **Por qué importa.** `MeliItems.Price` es el precio DE LISTA. Cuando una publicación entra a
/// una campaña, el comprador paga menos y el margen que calcula el sistema queda mintiendo — hacia
/// arriba, que es el lado peligroso. Medido el 31/08 en la cuenta real: el azúcar MLA2048049400
/// figuraba a $23.998,99 y se estaba vendiendo a **$17.999,24** por "CYBER FEST 09.09" (y a
/// $16.799,29 para compradores de nivel 6). El sistema mostraba el margen del precio de lista.
///
/// **Cómo se trae, y por qué así.** Preguntar publicación por publicación
/// (`GET /seller-promotions/items/{id}`) sería UNA llamada por cada una de las 5.900 — imposible
/// de hacer al abrir una pantalla. Se hace al revés y sale baratísimo:
///   1. `GET /seller-promotions/users/{userId}` → las campañas del vendedor (1 llamada).
///   2. Por cada campaña, `GET .../promotions/{id}/items?status=started` → SOLO las que están
///      participando de verdad, con su precio (paginado de a 50).
/// Medido el 31/08: ~15 llamadas para todo el catálogo, porque hay 15 campañas y sólo 2
/// publicaciones participando.
///
/// Este servicio SÓLO LEE de MercadoLibre. No aplica ni saca promociones.
/// </summary>
public class MeliPromocionesService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly ILogger<MeliPromocionesService> _logger;

    private const int PAGINA = 50;
    private const int MAX_PAGINAS = 200;   // techo por campaña: 10.000 publicaciones

    public MeliPromocionesService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService accountService, ILogger<MeliPromocionesService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _logger = logger;
    }

    public record Resultado(int Cuentas, int Campanias, int ConPromo, int Limpiadas, List<string> Detalle);

    private record PromoDeItem(decimal Precio, string Nombre, string Tipo, DateTime? Hasta);

    /// <summary>Relee las promociones de todas las cuentas y las guarda en cada publicación.</summary>
    public async Task<Resultado> RefrescarAsync(CancellationToken ct = default)
    {
        var cuentas = await _db.MeliAccounts.ToListAsync(ct);
        var detalle = new List<string>();
        var encontradas = new Dictionary<string, PromoDeItem>();
        int campanias = 0;

        foreach (var cuenta in cuentas)
        {
            if (ct.IsCancellationRequested) break;

            var token = await _accountService.GetValidTokenAsync(cuenta);
            if (string.IsNullOrWhiteSpace(token))
            {
                detalle.Add($"{cuenta.Nickname}: sin token, salteada");
                continue;
            }

            var http = _httpFactory.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var campanasJson = await LeerAsync(http,
                $"https://api.mercadolibre.com/seller-promotions/users/{cuenta.MeliUserId}?app_version=v2", ct);
            if (campanasJson is null)
            {
                detalle.Add($"{cuenta.Nickname}: MercadoLibre no devolvió las campañas");
                continue;
            }

            using var doc = JsonDocument.Parse(campanasJson);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                detalle.Add($"{cuenta.Nickname}: no hay campañas");
                continue;
            }

            foreach (var camp in results.EnumerateArray())
            {
                if (ct.IsCancellationRequested) break;

                var id = Txt(camp, "id");
                var tipo = Txt(camp, "type");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(tipo)) continue;
                campanias++;

                var nombre = Txt(camp, "name");
                if (string.IsNullOrWhiteSpace(nombre)) nombre = tipo!;
                var hasta = Fecha(camp, "finish_date");

                var enEsta = await LeerItemsDeCampaniaAsync(http, id!, tipo!, nombre!, hasta, encontradas, ct);
                if (enEsta > 0) detalle.Add($"{nombre}: {enEsta} publicación(es)");
            }
        }

        // ── Guardar: poner la promo a las que la tienen y LIMPIAR a las que ya no ──
        // Limpiar es tan importante como poner: una promo vencida que queda pegada hace que el
        // margen siga mostrándose con un descuento que ya no existe.
        var conPromoAntes = await _db.MeliItems
            .Where(i => i.VariationId == null && i.PromoPrecio != null)
            .ToListAsync(ct);

        int limpiadas = 0;
        foreach (var it in conPromoAntes)
        {
            if (encontradas.ContainsKey(it.MeliItemId)) continue;
            it.PromoPrecio = null;
            it.PromoNombre = null;
            it.PromoTipo = null;
            it.PromoHasta = null;
            it.PromoCapturadaAt = DateTime.UtcNow;
            limpiadas++;
        }

        var ids = encontradas.Keys.ToList();
        var aMarcar = await _db.MeliItems
            .Where(i => i.VariationId == null && ids.Contains(i.MeliItemId))
            .ToListAsync(ct);

        foreach (var it in aMarcar)
        {
            var p = encontradas[it.MeliItemId];
            it.PromoPrecio = p.Precio;
            it.PromoNombre = p.Nombre;
            it.PromoTipo = p.Tipo;
            it.PromoHasta = p.Hasta;
            it.PromoCapturadaAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);

        _logger.LogWarning("[Promos] {Camp} campañas · {Con} con promoción · {Limp} limpiadas",
            campanias, aMarcar.Count, limpiadas);

        return new Resultado(cuentas.Count, campanias, aMarcar.Count, limpiadas, detalle);
    }

    // ─── 2026-08-31 · las promociones de UNA publicación, con el margen de cada opción ───

    public record OpcionDto(
        string? Id, string Tipo, string Nombre, string Estado,
        DateTime? Desde, DateTime? Hasta,
        decimal? PrecioSugerido, decimal? PrecioMinimo, decimal? PrecioMaximo, decimal? PrecioActual,
        decimal? MargenSugeridoPct, decimal? MargenMinimoPct, decimal? MargenMaximoPct,
        decimal? PrecioParaElObjetivo,
        decimal? PoneMeliPct, decimal? PonesVosPct);

    public record DeItemDto(string MeliItemId, decimal PrecioLista, decimal? Costo,
        decimal? ObjetivoPct, List<OpcionDto> Opciones, string? Aviso);

    /// <summary>Qué campañas tiene disponibles esta publicación y, en cada una, QUÉ TE QUEDA.
    ///
    /// Esto es lo que no se ve en ningún lado, ni en MercadoLibre: MeLi te dice hasta dónde podés
    /// bajar, pero no sabe tu costo. Acá se juntan las dos cosas, así entrar a una campaña deja de
    /// ser a ciegas. También se calcula al revés: **a qué precio deberías entrar para que te siga
    /// quedando tu objetivo**.
    ///
    /// Sólo LEE. Es una llamada a MeLi, así que va cuando el usuario abre el panel de una fila.</summary>
    public async Task<DeItemDto?> LeerDeItemAsync(string meliItemId, CancellationToken ct = default)
    {
        var item = await _db.MeliItems.Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId && i.VariationId == null, ct);
        if (item?.MeliAccount is null) return null;

        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        if (string.IsNullOrWhiteSpace(token))
            return new DeItemDto(meliItemId, item.Price, null, null, new(), "Sin token de MercadoLibre. Reconectá la cuenta.");

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var json = await LeerAsync(http,
            $"https://api.mercadolibre.com/seller-promotions/items/{meliItemId}?app_version=v2", ct);
        if (json is null)
            return new DeItemDto(meliItemId, item.Price, null, null, new(), "MercadoLibre no contestó. Probá de nuevo.");

        // El costo y el objetivo salen del sistema: son la mitad que MeLi no tiene.
        var costo = await CostoDeAsync(meliItemId, ct);
        var objetivo = await _db.MeliItemSyncConfigs.AsNoTracking()
            .Where(c => c.MeliItemId == meliItemId).Select(c => c.GananciaObjetivoPct).FirstOrDefaultAsync(ct);

        var opciones = new List<OpcionDto>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in doc.RootElement.EnumerateArray())
            {
                var tipo = Txt(p, "type") ?? "";
                var nombre = Txt(p, "name");
                if (string.IsNullOrWhiteSpace(nombre)) nombre = LindoTipo(tipo);

                var sugerido = Dec(p, "suggested_discounted_price");
                var minimo = Dec(p, "min_discounted_price");
                var maximo = Dec(p, "max_discounted_price");
                var actual = Dec(p, "price");
                if (actual is <= 0) actual = null;

                opciones.Add(new OpcionDto(
                    Txt(p, "id"), tipo, nombre!, Txt(p, "status") ?? "",
                    Fecha(p, "start_date"), Fecha(p, "finish_date"),
                    sugerido, minimo, maximo, actual,
                    Margen(sugerido, item, costo), Margen(minimo, item, costo), Margen(maximo, item, costo),
                    PrecioParaObjetivo(item, costo, objetivo),
                    Dec(p, "meli_percentage"), Dec(p, "seller_percentage")));
            }
        }

        // Las que ya están en marcha primero: son las que están afectando la plata hoy.
        opciones = opciones
            .OrderByDescending(o => o.Estado == "started")
            .ThenBy(o => o.Nombre)
            .ToList();

        return new DeItemDto(meliItemId, item.Price, costo, objetivo, opciones,
            costo is null or <= 0 ? "Esta publicación no tiene costo cargado, así que no se puede saber qué te deja cada promoción." : null);
    }

    /// <summary>Qué te queda, sobre el costo, si la publicación se vendiera a ese precio.
    /// La comisión se calcula con el porcentaje y el cargo fijo que MeLi ya nos dijo; el envío se
    /// suma entero si lo pagás vos (no cambia con el precio: la caja pesa lo mismo).</summary>
    private static decimal? Margen(decimal? precio, Models.MeliItem item, decimal? costo)
    {
        if (precio is null or <= 0 || costo is null or <= 0) return null;

        decimal comision;
        if (item.SaleFeePercentageFee is > 0)
            comision = precio.Value * (item.SaleFeePercentageFee.Value / 100m) + (item.SaleFeeFixedFee ?? 0m);
        else if (item.SaleFeeAmount is > 0 && item.Price > 0)
            comision = item.SaleFeeAmount.Value / item.Price * precio.Value;   // sin desglose, se escala
        else
            return null;

        var neto = (precio.Value - comision - (item.SaleFeeShippingCost ?? 0m)) / IVA_;
        return Math.Round((neto - costo.Value) / costo.Value * 100m, 1);
    }

    /// <summary>La cuenta al revés: a qué precio habría que entrar a la campaña para que te siga
    /// quedando tu objetivo. Es el número que hace falta para decidir sin probar a mano.</summary>
    private static decimal? PrecioParaObjetivo(Models.MeliItem item, decimal? costo, decimal? objetivoPct)
    {
        if (costo is null or <= 0) return null;
        var obj = objetivoPct is > 0 ? objetivoPct.Value : 50m;
        if (item.SaleFeePercentageFee is not > 0) return null;

        // neto = (p − p·pct − fijo − envío) / IVA  y  neto = costo · (1 + obj/100)
        var netoBuscado = costo.Value * (1m + obj / 100m);
        var pct = item.SaleFeePercentageFee.Value / 100m;
        var resto = (item.SaleFeeFixedFee ?? 0m) + (item.SaleFeeShippingCost ?? 0m);
        var precio = (netoBuscado * IVA_ + resto) / (1m - pct);
        return precio > 0 ? Math.Round(precio, 2) : null;
    }

    private const decimal IVA_ = 1.21m;

    private async Task<decimal?> CostoDeAsync(string meliItemId, CancellationToken ct)
    {
        var porReceta = await (
            from c in _db.MeliItemComponentes.AsNoTracking()
            join p in _db.CafeProductos.AsNoTracking() on c.CafeProductoId equals p.Id
            where c.MeliItemId == meliItemId
            select new { p.Costo, c.Cantidad, p.Sku }
        ).ToListAsync(ct);

        if (porReceta.Count > 0)
            return porReceta.GroupBy(x => x.Sku).Select(g => g.First()).Sum(x => x.Costo * x.Cantidad);

        return await (
            from i in _db.MeliItems.AsNoTracking()
            join p in _db.CafeProductos.AsNoTracking() on i.CafeProductoId equals p.Id
            where i.MeliItemId == meliItemId && i.VariationId == null
            select (decimal?)p.Costo
        ).FirstOrDefaultAsync(ct);
    }

    private static string LindoTipo(string tipo) => tipo switch
    {
        "PRICE_DISCOUNT" => "Descuento tuyo",
        "DEAL" => "Campaña de MercadoLibre",
        "SMART" => "Promoción inteligente",
        "LIGHTNING" => "Oferta relámpago",
        "PRICE_MATCHING" => "Ganarle a la competencia",
        "PRE_NEGOTIATED" => "Acordada con MercadoLibre",
        "UNHEALTHY_STOCK" => "Stock que no rota",
        "" => "Promoción",
        _ => tipo
    };

    /// <summary>Trae las publicaciones que están participando de verdad (status=started) de una
    /// campaña, paginando. Las que están "candidate" NO cuentan: son las que PODRÍAN entrar.</summary>
    private async Task<int> LeerItemsDeCampaniaAsync(HttpClient http, string promoId, string tipo,
        string nombre, DateTime? hasta, Dictionary<string, PromoDeItem> acumulador, CancellationToken ct)
    {
        var encontradas = 0;
        string? searchAfter = null;

        for (var pagina = 0; pagina < MAX_PAGINAS; pagina++)
        {
            if (ct.IsCancellationRequested) break;

            var url = $"https://api.mercadolibre.com/seller-promotions/promotions/{promoId}/items"
                    + $"?promotion_type={tipo}&app_version=v2&status=started&limit={PAGINA}"
                    + (searchAfter is null ? "" : $"&search_after={Uri.EscapeDataString(searchAfter)}");

            var json = await LeerAsync(http, url, ct);
            if (json is null) break;

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0) break;

            foreach (var it in results.EnumerateArray())
            {
                var mla = Txt(it, "id");
                if (string.IsNullOrWhiteSpace(mla)) continue;
                var precio = Dec(it, "price");
                if (precio is null or <= 0) continue;

                // Si una publicación está en más de una campaña a la vez, gana la más barata:
                // es la que el comprador va a pagar.
                if (acumulador.TryGetValue(mla!, out var previa) && previa.Precio <= precio.Value) continue;
                acumulador[mla!] = new PromoDeItem(precio.Value, nombre, tipo, hasta);
                encontradas++;
            }

            searchAfter = doc.RootElement.TryGetProperty("paging", out var pg)
                          && pg.TryGetProperty("searchAfter", out var sa) ? sa.GetString() : null;
            if (string.IsNullOrWhiteSpace(searchAfter)) break;
        }

        return encontradas;
    }

    private async Task<string?> LeerAsync(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Promos] {Code} en {Url}", (int)resp.StatusCode, url);
                return null;
            }
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Promos] falló {Url}", url);
            return null;
        }
    }

    private static string? Txt(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? Dec(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : null;

    private static DateTime? Fecha(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
           && DateTime.TryParse(v.GetString(), out var d) ? d.ToUniversalTime() : null;
}
