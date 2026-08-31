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
