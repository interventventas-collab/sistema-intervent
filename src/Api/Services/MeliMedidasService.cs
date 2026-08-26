using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-26 — Peso y medidas del paquete de una publicación MeLi.
///
/// Por qué: el envío que MeLi te cobra sale del peso y del volumen del paquete. Cuando esos
/// datos están mal cargados, el envío se dispara y se come el margen — y ahí no hay precio que
/// alcance. Caso real: Mesita Bambini, costo $17.220, precio $100.552 y **$59.180 de envío**;
/// o el Cesto Ratán, mismo producto en tres colores con envíos de $13.050, $20.130 y $31.620.
/// Tres envíos distintos para la misma caja sólo se explican por medidas mal cargadas.
///
/// Los atributos en MeLi son SELLER_PACKAGE_WEIGHT / _LENGTH / _WIDTH / _HEIGHT.
/// El formato que efectivamente persiste (descubierto en pruebas el 09/06 con MLA3402774212)
/// es values[{ name, struct{ number, unit } }] — MeLi a veces ignora value_struct a nivel raíz.
///
/// Este servicio LEE en vivo y ESCRIBE, y además vuelve a preguntar el costo de envío después
/// de guardar, para que se vea al instante si la corrección sirvió.
/// </summary>
public class MeliMedidasService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly MeliItemService _itemService;
    private readonly ILogger<MeliMedidasService> _logger;

    private const string ATTR_PESO = "SELLER_PACKAGE_WEIGHT";
    private const string ATTR_LARGO = "SELLER_PACKAGE_LENGTH";
    private const string ATTR_ANCHO = "SELLER_PACKAGE_WIDTH";
    private const string ATTR_ALTO = "SELLER_PACKAGE_HEIGHT";

    public MeliMedidasService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService accountService, MeliItemService itemService,
        ILogger<MeliMedidasService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _itemService = itemService;
        _logger = logger;
    }

    /// <summary>Peso en gramos y medidas en centímetros. Null = no está cargado en MeLi.</summary>
    public record MedidasDto(
        string MeliItemId, string? Titulo, string? Sku,
        int? PesoGramos, decimal? LargoCm, decimal? AnchoCm, decimal? AltoCm,
        decimal? EnvioActual, decimal Precio, bool EnvioACargoNuestro,
        decimal? VolumenCm3, decimal? PesoVolumetricoKg, string? Mensaje);

    public record GuardarRequest(int? PesoGramos, decimal? LargoCm, decimal? AnchoCm, decimal? AltoCm);

    public record GuardarResultado(bool Ok, string Mensaje, MedidasDto? Medidas,
        decimal? EnvioAntes, decimal? EnvioDespues, decimal? Diferencia);

    public async Task<MedidasDto?> LeerAsync(string meliItemId, CancellationToken ct = default)
    {
        var (item, http) = await PrepararAsync(meliItemId, ct);
        if (item is null || http is null) return null;

        int? peso = null; decimal? largo = null, ancho = null, alto = null;
        string? mensaje = null;

        try
        {
            var resp = await http.GetAsync($"https://api.mercadolibre.com/items/{meliItemId}", ct);
            if (!resp.IsSuccessStatusCode)
                return new MedidasDto(meliItemId, item.Title, item.Sku, null, null, null, null,
                    item.SaleFeeShippingCost, item.Price, item.FreeShipping, null, null,
                    $"No se pudo leer de MeLi ({(int)resp.StatusCode})");

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in attrs.EnumerateArray())
                {
                    var id = a.TryGetProperty("id", out var ai) ? ai.GetString() : null;
                    if (id is null) continue;
                    var numero = LeerNumero(a);
                    if (numero is null) continue;

                    switch (id)
                    {
                        case ATTR_PESO: peso = (int)Math.Round(ConvertirAGramos(a, numero.Value)); break;
                        case ATTR_LARGO: largo = ConvertirACm(a, numero.Value); break;
                        case ATTR_ANCHO: ancho = ConvertirACm(a, numero.Value); break;
                        case ATTR_ALTO: alto = ConvertirACm(a, numero.Value); break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            mensaje = "Error leyendo de MeLi: " + ex.Message;
        }

        if (peso is null && largo is null && ancho is null && alto is null)
            mensaje ??= "Esta publicación no tiene peso ni medidas cargadas — MeLi cobra el envío estimando por su cuenta.";

        decimal? volumen = (largo is > 0 && ancho is > 0 && alto is > 0)
            ? largo!.Value * ancho!.Value * alto!.Value : null;
        // Regla de MeLi: el peso volumétrico es el volumen dividido 6.000 (cm³ → kg).
        decimal? pesoVol = volumen.HasValue ? Math.Round(volumen.Value / 6000m, 2) : null;

        return new MedidasDto(meliItemId, item.Title, item.Sku, peso, largo, ancho, alto,
            item.SaleFeeShippingCost, item.Price, item.FreeShipping, volumen, pesoVol, mensaje);
    }

    public async Task<GuardarResultado> GuardarAsync(string meliItemId, GuardarRequest req, CancellationToken ct = default)
    {
        if (req.PesoGramos is null && req.LargoCm is null && req.AnchoCm is null && req.AltoCm is null)
            return new GuardarResultado(false, "No mandaste ninguna medida para cambiar", null, null, null, null);

        // Rangos sanos: evitan que un cero o un tipeo manden a MeLi un paquete imposible.
        if (req.PesoGramos is { } g && (g <= 0 || g > 2_000_000))
            return new GuardarResultado(false, "El peso tiene que estar entre 1 g y 2.000 kg", null, null, null, null);
        foreach (var (nombre, valor) in new[] { ("largo", req.LargoCm), ("ancho", req.AnchoCm), ("alto", req.AltoCm) })
            if (valor is { } v && (v <= 0 || v > 300))
                return new GuardarResultado(false, $"El {nombre} tiene que estar entre 1 y 300 cm", null, null, null, null);

        var (item, http) = await PrepararAsync(meliItemId, ct);
        if (item is null || http is null)
            return new GuardarResultado(false, "No se encontró la publicación o falta el token de MeLi", null, null, null, null);

        var envioAntes = item.SaleFeeShippingCost;

        var atributos = new List<string>();
        if (req.PesoGramos is { } peso) atributos.Add(Atributo(ATTR_PESO, peso, "g"));
        if (req.LargoCm is { } l) atributos.Add(Atributo(ATTR_LARGO, l, "cm"));
        if (req.AnchoCm is { } a2) atributos.Add(Atributo(ATTR_ANCHO, a2, "cm"));
        if (req.AltoCm is { } h) atributos.Add(Atributo(ATTR_ALTO, h, "cm"));

        var payload = "{\"attributes\":[" + string.Join(",", atributos) + "]}";

        try
        {
            using var reqMsg = new HttpRequestMessage(HttpMethod.Put, $"https://api.mercadolibre.com/items/{meliItemId}")
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json")
            };
            var resp = await http.SendAsync(reqMsg, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[Medidas] {Mla} rechazado por MeLi {Code}: {Body}", meliItemId, (int)resp.StatusCode, Trim(body));
                return new GuardarResultado(false, $"MeLi rechazó el cambio ({(int)resp.StatusCode}): {Trim(body)}",
                    null, envioAntes, null, null);
            }
        }
        catch (Exception ex)
        {
            return new GuardarResultado(false, "Error llamando a MeLi: " + ex.Message, null, envioAntes, null, null);
        }

        // Con las medidas nuevas, MeLi recalcula el envío. Se vuelve a preguntar para mostrar
        // al instante si la corrección sirvió — que es todo el punto de esta pantalla.
        decimal? envioDespues = null;
        try
        {
            await _itemService.RefreshSaleFeeAsync(meliItemId);
            envioDespues = await _db.MeliItems.AsNoTracking()
                .Where(m => m.MeliItemId == meliItemId && m.VariationId == null)
                .Select(m => m.SaleFeeShippingCost)
                .FirstOrDefaultAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Medidas] {Mla}: guardó bien pero no se pudo recalcular el envío", meliItemId);
        }

        var medidas = await LeerAsync(meliItemId, ct);
        var dif = (envioAntes.HasValue && envioDespues.HasValue) ? envioDespues.Value - envioAntes.Value : (decimal?)null;

        var texto = dif switch
        {
            null => "Medidas guardadas en MeLi",
            < 0 => $"Medidas guardadas — el envío bajó ${Math.Abs(dif.Value):N0}",
            > 0 => $"Medidas guardadas — ojo: el envío subió ${dif.Value:N0}",
            _ => "Medidas guardadas — el envío no cambió"
        };

        _logger.LogInformation("[Medidas] {Mla} actualizado. Envío {Antes} → {Despues}", meliItemId, envioAntes, envioDespues);
        return new GuardarResultado(true, texto, medidas, envioAntes, envioDespues, dif);
    }

    // ── auxiliares ──

    private async Task<(Models.MeliItem? item, HttpClient? http)> PrepararAsync(string meliItemId, CancellationToken ct)
    {
        var item = await _db.MeliItems.AsNoTracking().Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId && i.VariationId == null, ct);
        if (item?.MeliAccount is null) return (null, null);
        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        if (string.IsNullOrWhiteSpace(token)) return (item, null);

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromSeconds(30);
        return (item, http);
    }

    private static string Atributo(string id, decimal numero, string unidad)
    {
        var n = numero.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{{\"id\":\"{id}\",\"values\":[{{\"name\":\"{n} {unidad}\",\"struct\":{{\"number\":{n},\"unit\":\"{unidad}\"}}}}]}}";
    }

    /// <summary>El número puede venir en value_struct o dentro de values[0].struct.</summary>
    private static decimal? LeerNumero(JsonElement a)
    {
        if (a.TryGetProperty("value_struct", out var vs) && vs.ValueKind == JsonValueKind.Object
            && vs.TryGetProperty("number", out var n1) && n1.ValueKind == JsonValueKind.Number)
            return n1.GetDecimal();

        if (a.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
            foreach (var v in vals.EnumerateArray())
                if (v.TryGetProperty("struct", out var st) && st.ValueKind == JsonValueKind.Object
                    && st.TryGetProperty("number", out var n2) && n2.ValueKind == JsonValueKind.Number)
                    return n2.GetDecimal();

        return null;
    }

    private static string? LeerUnidad(JsonElement a)
    {
        if (a.TryGetProperty("value_struct", out var vs) && vs.ValueKind == JsonValueKind.Object
            && vs.TryGetProperty("unit", out var u1)) return u1.GetString();
        if (a.TryGetProperty("values", out var vals) && vals.ValueKind == JsonValueKind.Array)
            foreach (var v in vals.EnumerateArray())
                if (v.TryGetProperty("struct", out var st) && st.ValueKind == JsonValueKind.Object
                    && st.TryGetProperty("unit", out var u2)) return u2.GetString();
        return null;
    }

    /// <summary>MeLi devuelve el peso en g o kg según cómo se cargó. Se normaliza a gramos.</summary>
    private static decimal ConvertirAGramos(JsonElement a, decimal numero)
    {
        var u = (LeerUnidad(a) ?? "g").Trim().ToLowerInvariant();
        return u switch { "kg" => numero * 1000m, "mg" => numero / 1000m, _ => numero };
    }

    /// <summary>Las medidas pueden venir en cm, mm o m. Se normalizan a centímetros.</summary>
    private static decimal ConvertirACm(JsonElement a, decimal numero)
    {
        var u = (LeerUnidad(a) ?? "cm").Trim().ToLowerInvariant();
        return u switch { "mm" => numero / 10m, "m" => numero * 100m, _ => numero };
    }

    private static string Trim(string s) => s.Length > 200 ? s[..200] : s;
}
