using System.Text.RegularExpressions;

namespace Api.Services;

public record DolarBnaDto(
    decimal? Compra,
    decimal? Venta,
    string? Fecha,
    string? Source,
    string? Error
);

/// <summary>
/// Cotizaciones publicas. Por ahora solo dolar BNA scrapeando https://www.bna.com.ar/Personas.
/// </summary>
public class QuotesService
{
    private readonly IHttpClientFactory _httpFactory;

    public QuotesService(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    public async Task<DolarBnaDto> GetDolarBnaAsync()
    {
        try
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(10);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36");

            var response = await http.GetAsync("https://www.bna.com.ar/Personas");
            if (!response.IsSuccessStatusCode)
                return new DolarBnaDto(null, null, null, "bna", $"El BNA respondio {(int)response.StatusCode}.");

            var html = await response.Content.ReadAsStringAsync();

            // Estructura: <td class="tit">Dolar U.S.A</td> <td>COMPRA</td> <td>VENTA</td>
            // Buscamos el primer match (tabla billetes).
            var m = Regex.Match(html,
                @"<td[^>]*class=""tit""[^>]*>\s*Dolar\s+U\.S\.A\s*</td>\s*<td[^>]*>\s*([\d.,]+)\s*</td>\s*<td[^>]*>\s*([\d.,]+)\s*</td>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            if (!m.Success)
                return new DolarBnaDto(null, null, null, "bna",
                    "No se encontro la cotizacion en la pagina del BNA. El sitio puede haber cambiado.");

            var compra = ParseDecimal(m.Groups[1].Value);
            var venta = ParseDecimal(m.Groups[2].Value);

            // Fecha del encabezado: <th class="fechaCot">28/4/2026</th>
            var fechaMatch = Regex.Match(html, @"<th[^>]*class=""fechaCot""[^>]*>\s*([^<]+?)\s*</th>", RegexOptions.IgnoreCase);
            var fecha = fechaMatch.Success ? fechaMatch.Groups[1].Value.Trim() : null;

            return new DolarBnaDto(compra, venta, fecha, "bna", null);
        }
        catch (Exception ex)
        {
            return new DolarBnaDto(null, null, null, "bna", "Error consultando BNA: " + ex.Message);
        }
    }

    private static decimal? ParseDecimal(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // El BNA usa coma o punto como separador decimal segun la tabla. Normalizamos:
        // - Si tiene una sola coma o punto, lo tratamos como decimal.
        // - Si tiene puntos como miles + coma decimal (ej: 1.385,00), reemplazamos.
        var cleaned = raw.Trim();
        // Si el ultimo separador es coma => formato es-AR
        var lastComma = cleaned.LastIndexOf(',');
        var lastDot = cleaned.LastIndexOf('.');
        string normalized;
        if (lastComma > lastDot)
        {
            // 1.385,00 -> 1385.00
            normalized = cleaned.Replace(".", "").Replace(",", ".");
        }
        else
        {
            // 1408.0000 -> 1408.0000 (ya en invariant) o 1,385.00 -> 1385.00
            normalized = cleaned.Replace(",", "");
        }
        return decimal.TryParse(normalized, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
