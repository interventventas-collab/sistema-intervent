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

        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

            var response = await http.GetAsync($"https://www.cuitonline.com/detalle/{clean}");
            if (!response.IsSuccessStatusCode)
                return new FiscalLookupResult(clean, null, null, null, false, "cuitonline",
                    $"El servicio respondio {(int)response.StatusCode}.");

            var html = await response.Content.ReadAsStringAsync();
            if (html.Contains("No se encontraron resultados", StringComparison.OrdinalIgnoreCase))
                return new FiscalLookupResult(clean, null, null, null, false, "cuitonline", "CUIT no encontrado.");

            // Parseo de campos clave (regex tolerantes a cambios menores).
            var name = MatchOne(html,
                @"<h1[^>]*class=""[^""]*denominacion[^""]*""[^>]*>([^<]+)</h1>",
                @"<title>([^<\|]+)\|") ?? "";
            var address = MatchOne(html,
                @"Domicilio[^:]*:\s*</[^>]+>\s*<[^>]+>([^<]+)<",
                @"Domicilio[^:]*:[^<]*<[^>]+>\s*([^<]+)\s*<");
            var ivaCondition = MatchOne(html,
                @"Condici[oó]n\s+frente\s+al\s+IVA[^:]*:\s*</[^>]+>\s*<[^>]+>([^<]+)<",
                @"IVA[^:]*:[^<]*<[^>]+>\s*([^<]+)\s*<");

            name = HtmlDecode(name);
            address = HtmlDecode(address);
            ivaCondition = HtmlDecode(ivaCondition);

            if (string.IsNullOrWhiteSpace(name))
                return new FiscalLookupResult(clean, null, address, ivaCondition, false, "cuitonline",
                    "No se pudo extraer el nombre. El servicio puede haber cambiado.");

            return new FiscalLookupResult(clean, name.Trim(), address?.Trim(), ivaCondition?.Trim(), true, "cuitonline", null);
        }
        catch (Exception ex)
        {
            return new FiscalLookupResult(clean, null, null, null, false, null, "Error consultando: " + ex.Message);
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
