using System.Net.Http.Json;
using Web.Models.Mobile;

namespace Web.Services.Mobile;

/// <summary>
/// Singleton: catálogo completo en memoria + búsqueda rápida.
/// Se carga UNA sola vez al iniciar la app (lazy: en la primera llamada a EnsureLoadedAsync).
/// El ranking implementa el orden del spec:
///   1. exact-sku   (score 1000)
///   2. prefix-sku  (score 800)
///   3. contains-sku (score 600)
///   4. contains-nombre (score 400)
///   5. fuzzy (Levenshtein dist=1) (score 200)
/// Boost: cargado hoy (+50), esta semana (+20), favorito (+30), frecuencia uso (+10/uso)
/// </summary>
public class CatalogIndex
{
    private readonly HttpClient _http;
    private readonly KeyboardPrefs _prefs;
    private List<CatalogItem>? _items;
    private bool _loading = false;
    public DateTime? LoadedAt { get; private set; }
    public int Count => _items?.Count ?? 0;

    public CatalogIndex(HttpClient http, KeyboardPrefs prefs)
    {
        _http = http;
        _prefs = prefs;
    }

    public async Task EnsureLoadedAsync()
    {
        if (_items != null || _loading) return;
        _loading = true;
        try
        {
            var resp = await _http.GetFromJsonAsync<CatalogoResp>("/api/catalogo-buscador/all");
            if (resp?.Items != null)
            {
                foreach (var it in resp.Items)
                {
                    it.SkuUpper = (it.Sku ?? "").ToUpperInvariant();
                    it.NombreUpper = (it.Nombre ?? "").ToUpperInvariant();
                }
                _items = resp.Items;
                LoadedAt = DateTime.UtcNow;
            }
        }
        finally { _loading = false; }
    }

    private class CatalogoResp { public int Count { get; set; } public List<CatalogItem> Items { get; set; } = new(); }

    /// <summary>Búsqueda con ranking. Devuelve hasta `limit` resultados.</summary>
    public List<SearchHit> Search(string query, int limit = 10, Func<CatalogItem, bool>? filter = null)
    {
        if (_items == null || string.IsNullOrWhiteSpace(query)) return new();
        var q = query.Trim().ToUpperInvariant();
        if (q.Length == 0) return new();

        var hits = new List<SearchHit>(64);
        var hoy = DateTime.UtcNow.Date;
        var hace7d = hoy.AddDays(-7);

        foreach (var item in _items)
        {
            if (filter != null && !filter(item)) continue;

            int score = 0;
            string matchType = "";

            if (item.SkuUpper == q) { score = 1000; matchType = "exact-sku"; }
            else if (item.SkuUpper.StartsWith(q)) { score = 800; matchType = "prefix-sku"; }
            else if (item.SkuUpper.Contains(q)) { score = 600; matchType = "contains-sku"; }
            else if (item.NombreUpper.Contains(q)) { score = 400; matchType = "contains-nombre"; }
            else if (q.Length >= 3 && LevenshteinUnder2(item.SkuUpper, q)) { score = 200; matchType = "fuzzy"; }
            else continue;

            // Boost por recencia
            if (item.StockChangedAt.HasValue)
            {
                if (item.StockChangedAt.Value.Date >= hoy) score += 50;
                else if (item.StockChangedAt.Value.Date >= hace7d) score += 20;
            }
            // Boost favorito
            if (_prefs.IsFavorito(item.Sku)) score += 30;
            // Boost frecuencia de uso (max +50 para no aplastar el ranking)
            var usos = _prefs.GetUsosFrecuentes(item.Sku);
            score += Math.Min(usos * 10, 50);

            hits.Add(new SearchHit { Item = item, Score = score, MatchType = matchType });
        }

        return hits
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.Item.SkuUpper.Length)   // a igual score, SKU más corto primero
            .Take(limit)
            .ToList();
    }

    /// <summary>Items "estrella" para el estado vacío (sin tipear): favoritos + cargados hoy + frecuentes.</summary>
    public (List<CatalogItem> favoritos, List<CatalogItem> cargadosHoy, List<CatalogItem> frecuentes) EmptyState(int max = 8)
    {
        if (_items == null) return (new(), new(), new());
        var hoy = DateTime.UtcNow.Date;

        var favSkus = _prefs.GetFavoritos();
        var favoritos = _items.Where(i => favSkus.Contains(i.Sku)).Take(max).ToList();

        var cargadosHoy = _items
            .Where(i => i.StockChangedAt.HasValue && i.StockChangedAt.Value.Date >= hoy)
            .OrderByDescending(i => i.StockChangedAt)
            .Take(max).ToList();

        var frecuentes = _prefs.GetTopUsados(max)
            .Select(sku => _items.FirstOrDefault(i => i.Sku == sku))
            .Where(i => i != null).Cast<CatalogItem>()
            .ToList();

        return (favoritos, cargadosHoy, frecuentes);
    }

    /// <summary>Levenshtein simple: true si la distancia es 0, 1 o 2 (caro: solo en strings cortos).</summary>
    private static bool LevenshteinUnder2(string s, string t)
    {
        if (Math.Abs(s.Length - t.Length) > 2) return false;
        if (s.Length > 30 || t.Length > 30) return false;  // límite de seguridad
        int[,] d = new int[s.Length + 1, t.Length + 1];
        for (int i = 0; i <= s.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= t.Length; j++) d[0, j] = j;
        for (int i = 1; i <= s.Length; i++)
        {
            for (int j = 1; j <= t.Length; j++)
            {
                int cost = s[i - 1] == t[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return d[s.Length, t.Length] <= 2;
    }
}
