using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Services;

public record VisitasPaginaDto(string Dominio, int Hoy, int Ayer, int Semana);

/// <param name="Conectado">false si todavía no se cargó la conexión en Integraciones.</param>
/// <param name="Problema">Texto para el dueño si Google no devolvió los números.</param>
public record VisitasPaginasDto(bool Conectado, string? Problema, List<VisitasPaginaDto> Paginas);

/// <summary>
/// Visitas de las páginas web para el cuadro del final de Panorama. Los números los lee de
/// Google Analytics con una cuenta de solo lectura: en Integrations, provider "google-analytics",
/// Settings = {propertyId, clientEmail} y AppSecret = el archivo de la llave.
/// Se guardan 15 minutos para no preguntarle a Google cada vez que alguien abre la pantalla.
/// </summary>
public class VisitasPaginasService
{
    private const string Provider = "google-analytics";
    private const string ScopeLectura = "https://www.googleapis.com/auth/analytics.readonly";

    // Frikaf y Café Frikaf salen siempre. Las otras dos aparecen solas cuando empiecen a medirse.
    private static readonly string[] PaginasFijas = { "frikaf.com.ar", "cafefrikaf.com.ar" };
    private static readonly string[] PaginasOpcionales = { "intervent.com.ar", "palanica.com.ar" };

    private readonly IntegrationService _integraciones;
    private readonly IHttpClientFactory _http;
    private readonly IMemoryCache _cache;
    private readonly ILogger<VisitasPaginasService> _log;

    public VisitasPaginasService(IntegrationService integraciones, IHttpClientFactory http,
                                 IMemoryCache cache, ILogger<VisitasPaginasService> log)
    {
        _integraciones = integraciones;
        _http = http;
        _cache = cache;
        _log = log;
    }

    public async Task<VisitasPaginasDto> GetAsync(CancellationToken ct)
    {
        var integracion = await _integraciones.GetByProviderAsync(Provider);
        var propertyId = LeerPropertyId(integracion?.Settings);
        if (integracion is null || !integracion.HasSecret || propertyId is null)
            return new VisitasPaginasDto(false, null, new());

        // La fecha de guardado va en la clave: si cargan otra llave, se vuelve a preguntar enseguida.
        var clave = $"visitas-paginas-{integracion.Id}-{integracion.UpdatedAt?.Ticks}";
        if (_cache.TryGetValue(clave, out VisitasPaginasDto? guardado) && guardado is not null)
            return guardado;

        var resultado = await ConsultarGoogleAsync(propertyId, ct);
        _cache.Set(clave, resultado, TimeSpan.FromMinutes(resultado.Problema is null ? 15 : 2));
        return resultado;
    }

    private async Task<VisitasPaginasDto> ConsultarGoogleAsync(string propertyId, CancellationToken ct)
    {
        try
        {
            var llave = await _integraciones.GetSecretAsync(Provider);
            var credencial = GoogleCredential.FromJson(llave).CreateScoped(ScopeLectura);
            var token = await credencial.UnderlyingCredential.GetAccessTokenForRequestAsync(null, ct);

            var pedido = JsonSerializer.Serialize(new
            {
                dimensions = new[] { new { name = "hostName" } },
                metrics = new[] { new { name = "activeUsers" } },
                dateRanges = new[]
                {
                    new { name = "hoy", startDate = "today", endDate = "today" },
                    new { name = "ayer", startDate = "yesterday", endDate = "yesterday" },
                    new { name = "semana", startDate = "6daysAgo", endDate = "today" }
                },
                limit = 200
            });

            var cliente = _http.CreateClient();
            cliente.Timeout = TimeSpan.FromSeconds(20);
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://analyticsdata.googleapis.com/v1beta/properties/{propertyId}:runReport")
            {
                Content = new StringContent(pedido, Encoding.UTF8, "application/json")
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await cliente.SendAsync(req, ct);
            var cuerpo = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogWarning("Analytics respondió {Status}: {Cuerpo}", (int)resp.StatusCode,
                    cuerpo.Length > 400 ? cuerpo[..400] : cuerpo);
                return new VisitasPaginasDto(true, TextoDeError((int)resp.StatusCode), new());
            }

            return new VisitasPaginasDto(true, null, ArmarPaginas(cuerpo));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Solo el tipo: el mensaje de una llave mal formada podría traer parte del contenido.
            _log.LogWarning("No se pudo consultar Analytics: {Tipo}", ex.GetType().Name);
            return new VisitasPaginasDto(true, "No se pudo consultar a Google. Se vuelve a intentar en unos minutos.", new());
        }
    }

    private static string TextoDeError(int status) => status switch
    {
        400 or 404 => "Google no encuentra esa propiedad. Revisá el ID de propiedad en Integraciones.",
        401 or 403 => "Google no dio permiso. Revisá que la cuenta esté como Lector en Analytics y el servicio activado.",
        429 => "Google frenó las consultas por un rato. Se vuelve a intentar en unos minutos.",
        _ => "Google no devolvió los números. Se vuelve a intentar en unos minutos."
    };

    private static List<VisitasPaginaDto> ArmarPaginas(string cuerpo)
    {
        var totales = new Dictionary<string, (int Hoy, int Ayer, int Semana)>();
        using var doc = JsonDocument.Parse(cuerpo);
        if (doc.RootElement.TryGetProperty("rows", out var filas))
        {
            foreach (var fila in filas.EnumerateArray())
            {
                var dims = fila.GetProperty("dimensionValues");
                var dominio = NormalizarDominio(dims[0].GetProperty("value").GetString());
                var periodo = dims[1].GetProperty("value").GetString();
                if (!int.TryParse(fila.GetProperty("metricValues")[0].GetProperty("value").GetString(), out var n))
                    continue;

                totales.TryGetValue(dominio, out var t);
                totales[dominio] = periodo switch
                {
                    "hoy" => (t.Hoy + n, t.Ayer, t.Semana),
                    "ayer" => (t.Hoy, t.Ayer + n, t.Semana),
                    "semana" => (t.Hoy, t.Ayer, t.Semana + n),
                    _ => t
                };
            }
        }

        // Solo nuestras páginas: a Analytics a veces le llegan visitas falsas con otros dominios.
        return PaginasFijas
            .Concat(PaginasOpcionales.Where(totales.ContainsKey))
            .Select(d =>
            {
                totales.TryGetValue(d, out var t);
                return new VisitasPaginaDto(d, t.Hoy, t.Ayer, t.Semana);
            })
            .ToList();
    }

    private static string NormalizarDominio(string? dominio)
    {
        var d = (dominio ?? "").Trim().ToLowerInvariant();
        return d.StartsWith("www.") ? d[4..] : d;
    }

    private static string? LeerPropertyId(string? settings)
    {
        if (string.IsNullOrWhiteSpace(settings)) return null;
        try
        {
            using var doc = JsonDocument.Parse(settings);
            if (!doc.RootElement.TryGetProperty("propertyId", out var p)) return null;
            var id = p.GetString()?.Trim();
            return !string.IsNullOrEmpty(id) && id.All(char.IsDigit) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
