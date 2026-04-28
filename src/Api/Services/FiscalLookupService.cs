using System.Text.RegularExpressions;

namespace Api.Services;

public record FiscalLookupResult(
    string Cuit,
    string? Name,
    string? Address,
    string? IvaCondition,
    bool Found,
    string? Source,
    string? Error
);

/// <summary>
/// Lookup gratuito de datos fiscales por CUIT.
/// Usa un servicio publico no oficial (scraping de cuitonline.com).
/// Puede romperse si el sitio cambia, en ese caso devuelve Found=false con mensaje.
/// </summary>
public class FiscalLookupService
{
    private readonly IHttpClientFactory _httpFactory;

    public FiscalLookupService(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<FiscalLookupResult> LookupByCuitAsync(string cuit)
    {
        var clean = new string((cuit ?? "").Where(char.IsDigit).ToArray());
        if (clean.Length != 11)
            return new FiscalLookupResult(clean, null, null, null, false, null, "El CUIT/CUIL debe tener 11 digitos.");

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");
        http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("es-AR,es;q=0.9");

        try
        {
            // Paso 1: pagina de busqueda. Devuelve un listado con el primer hit que coincide con el CUIT.
            var searchResp = await http.GetAsync($"https://www.cuitonline.com/search.php?q={clean}");
            if (!searchResp.IsSuccessStatusCode)
                return new FiscalLookupResult(clean, null, null, null, false, "cuitonline",
                    $"El servicio respondio {(int)searchResp.StatusCode} al buscar el CUIT.");

            var searchHtml = await searchResp.Content.ReadAsStringAsync();

            // Nombre: <h2 class="denominacion" ...>PALANICA JAMKOWY GERMAN PABLO</h2>
            var name = MatchOne(searchHtml,
                @"<h2[^>]*class=""[^""]*denominacion[^""]*""[^>]*>\s*([^<]+?)\s*</h2>",
                @"title=""Ver detalles de ([^""]+)""",
                @"<a[^>]*class=""denominacion""[^>]*>\s*([^<]+)\s*</a>");
            name = HtmlDecode(name)?.Trim();

            if (string.IsNullOrWhiteSpace(name) ||
                name!.Equals("CUIT", StringComparison.OrdinalIgnoreCase) ||
                name.Length < 3)
            {
                return new FiscalLookupResult(clean, null, null, null, false, "cuitonline",
                    "No se encontraron resultados para ese CUIT.");
            }

            // IVA: aparece como "IVA:&nbsp;Iva Exento" o "IVA: Responsable Inscripto", etc.
            var ivaCondition = MatchOne(searchHtml,
                @"IVA:(?:&nbsp;|\s)*([A-Za-zÁÉÍÓÚáéíóúÑñ ]+?)\s*<",
                @"IVA[^:]*:[^<]*<[^>]+>\s*([^<]+)\s*<");
            ivaCondition = HtmlDecode(ivaCondition)?.Trim();

            // Buscamos la URL del detalle (tiene el domicilio fiscal).
            string? address = null;
            var detalleUrl = MatchOne(searchHtml,
                @"href=""(detalle/" + clean + @"/[^""]+\.html)""",
                @"href=""(/detalle/" + clean + @"/[^""]+\.html)""");
            if (!string.IsNullOrEmpty(detalleUrl))
            {
                if (!detalleUrl.StartsWith("http"))
                    detalleUrl = "https://www.cuitonline.com/" + detalleUrl.TrimStart('/');
                try
                {
                    var detResp = await http.GetAsync(detalleUrl);
                    if (detResp.IsSuccessStatusCode)
                    {
                        var detHtml = await detResp.Content.ReadAsStringAsync();
                        address = MatchOne(detHtml,
                            @"Domicilio\s+Fiscal[^<]*</[^>]+>\s*<[^>]+>\s*([^<]+?)\s*<",
                            @"Domicilio[^<]*</[^>]+>\s*<[^>]+>\s*([^<]+?)\s*<",
                            @"itemprop=""address""[^>]*>\s*([^<]+?)\s*<");
                        address = HtmlDecode(address)?.Trim();
                        if (string.IsNullOrEmpty(ivaCondition))
                        {
                            ivaCondition = HtmlDecode(MatchOne(detHtml,
                                @"Condici[oó]n\s+frente\s+al\s+IVA[^<]*</[^>]+>\s*<[^>]+>\s*([^<]+?)\s*<",
                                @"IVA:(?:&nbsp;|\s)*([A-Za-zÁÉÍÓÚáéíóúÑñ ]+?)\s*<"))?.Trim();
                        }
                    }
                }
                catch { /* si falla el detalle, devolvemos lo que tenemos */ }
            }

            return new FiscalLookupResult(clean, name, address, ivaCondition, true, "cuitonline", null);
        }
        catch (Exception ex)
        {
            return new FiscalLookupResult(clean, null, null, null, false, "cuitonline",
                "Error consultando el servicio: " + ex.Message);
        }
    }

    private static string? MatchOne(string text, params string[] patterns)
    {
        foreach (var pat in patterns)
        {
            var m = Regex.Match(text, pat, RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (m.Success && m.Groups.Count > 1)
            {
                var val = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        return null;
    }

    private static string? HtmlDecode(string? s) =>
        string.IsNullOrEmpty(s) ? s : System.Net.WebUtility.HtmlDecode(s);
}
