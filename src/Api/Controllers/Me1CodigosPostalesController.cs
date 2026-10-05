using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Api.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-10-05: /meli/me1/codigos-postales — sacar / volver a ofrecer CPs de me1, ubicarlos en el
/// mapa y mandarle el tarifario a MeLi por API (POST /shipping/me1/v1/tariff/update) para no
/// tener que subir el Excel a mano. La lista con los CPs sale de MeliMe1Controller
/// (GET api/meli/me1/codigos-postales-activos) y usa los mismos rangos (TARIFAS).
/// </summary>
[ApiController]
[Route("api/meli/me1/codigos-postales")]
[Authorize]
public class Me1CodigosPostalesController : ControllerBase
{
    private const string SettingUltimoCambio = "me1.cps.ultimo_cambio";
    private const string SettingServicio = "me1.tarifa.service";
    private const string ServicioDefault = "transportadora-17";

    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly GoogleMapsLinkResolverService _geo;
    private readonly ILogger<Me1CodigosPostalesController> _logger;

    public Me1CodigosPostalesController(AppDbContext db, AuditLogService audit, IHttpClientFactory httpFactory,
        MeliAccountService accountService, GoogleMapsLinkResolverService geo, ILogger<Me1CodigosPostalesController> logger)
    {
        _db = db; _audit = audit; _httpFactory = httpFactory;
        _accountService = accountService; _geo = geo; _logger = logger;
    }

    // Localidades que Google no ubica bien (devuelve una calle homónima o el partido entero).
    // Coordenadas aproximadas del pueblo, puestas a mano el 2026-10-05.
    private static readonly Dictionary<string, (decimal Lat, decimal Lng)> UBICACIONES_A_MANO = new()
    {
        ["Buenos Aires|Otamendi"] = (-34.2306m, -58.8714m),
        ["Buenos Aires|El Pino"] = (-34.9700m, -58.0700m),
        ["Buenos Aires|Punta Indio"] = (-35.2733m, -57.2479m),
        ["Buenos Aires|Alto Verde"] = (-34.0800m, -59.0600m),
        ["Buenos Aires|El Durazno"] = (-34.6986m, -58.9034m),
        ["Buenos Aires|Villa Numancia"] = (-34.9667m, -58.4333m),
    };

    private static bool EsCpDeLaTabla(int cp) => MeliMe1Controller.TARIFAS.Any(t => cp >= t.CpFrom && cp <= t.CpTo);

    // ============================================================
    // Sacar / volver a ofrecer
    // ============================================================

    public record CambiarEstadoRequest(List<int> Cps, bool Ofrecer);

    /// <summary>Saca (Ofrecer=false) o vuelve a ofrecer (Ofrecer=true) una lista de CPs.</summary>
    [HttpPost("estado")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> CambiarEstado([FromBody] CambiarEstadoRequest req)
    {
        var cps = (req.Cps ?? new()).Distinct().Where(EsCpDeLaTabla).ToList();
        if (cps.Count == 0) return BadRequest(new { error = "No hay códigos postales válidos para cambiar." });

        var yaExcluidos = await _db.Me1CpExcluidos.Where(e => cps.Contains(e.Cp)).ToListAsync();
        int cambiados;
        if (req.Ofrecer)
        {
            _db.Me1CpExcluidos.RemoveRange(yaExcluidos);
            cambiados = yaExcluidos.Count;
        }
        else
        {
            var set = yaExcluidos.Select(e => e.Cp).ToHashSet();
            var nuevos = cps.Where(cp => !set.Contains(cp)).ToList();
            var usuario = User.Identity?.Name;
            _db.Me1CpExcluidos.AddRange(nuevos.Select(cp => new Me1CpExcluido { Cp = cp, ExcluidoAt = DateTime.UtcNow, ExcluidoPor = usuario }));
            cambiados = nuevos.Count;
        }

        if (cambiados > 0)
        {
            var st = await _db.AppSettings.FindAsync(SettingUltimoCambio);
            var ahora = DateTime.UtcNow.ToString("O");
            if (st is null) _db.AppSettings.Add(new AppSetting { Key = SettingUltimoCambio, Value = ahora, UpdatedAt = DateTime.UtcNow });
            else { st.Value = ahora; st.UpdatedAt = DateTime.UtcNow; }
        }
        await _db.SaveChangesAsync();

        if (cambiados > 0)
            await _audit.LogAsync("Me1.CodigosPostales", req.Ofrecer ? "ofrecer" : "sacar",
                req.Ofrecer ? "volver_a_ofrecer" : "dejar_de_ofrecer",
                $"{cambiados} CPs: {string.Join(", ", cps.OrderBy(c => c).Take(300))}", User.Identity?.Name);

        return Ok(new { ok = true, cambiados });
    }

    // ============================================================
    // Ubicar localidades en el mapa (Google Geocoding, una sola vez)
    // ============================================================

    /// <summary>
    /// Busca la ubicación de las localidades que todavía no se buscaron. Las guarda (también las
    /// que no se encontraron, para no reintentar). Devuelve cuántas buscó y cuántas encontró.
    /// </summary>
    [HttpPost("ubicar")]
    public async Task<IActionResult> Ubicar()
    {
        var yaBuscadas = (await _db.Me1LocalidadUbicaciones.Select(u => u.Clave).ToListAsync()).ToHashSet();
        var pendientes = new List<(string Clave, string Provincia, string Localidad)>();
        foreach (var (cpFrom, cpTo, _, _) in MeliMe1Controller.TARIFAS)
            for (int cp = cpFrom; cp <= cpTo; cp++)
            {
                var (prov, loc) = MeliMe1Controller.LookupCp(cp);
                if (string.IsNullOrWhiteSpace(prov) || string.IsNullOrWhiteSpace(loc)) continue;
                var clave = MeliMe1Controller.ClaveLocalidad(prov, loc);
                if (yaBuscadas.Add(clave)) pendientes.Add((clave, prov, loc));
            }

        int encontradas = 0;
        foreach (var (clave, prov, loc) in pendientes)
        {
            if (UBICACIONES_A_MANO.TryGetValue(clave, out var aMano))
            {
                _db.Me1LocalidadUbicaciones.Add(new Me1LocalidadUbicacion
                    { Clave = clave, Lat = aMano.Lat, Lng = aMano.Lng, Encontrado = true, BuscadoAt = DateTime.UtcNow });
                encontradas++;
                continue;
            }
            var provTexto = prov == "CABA" ? "Ciudad Autónoma de Buenos Aires" : $"Provincia de {prov}";
            var r = await _geo.TryGeocodeAddressAsync($"{loc}, {provTexto}, Argentina");
            // Solo vale si cae en la zona de las tarifas (AMBA + La Plata + norte de BA + sur de
            // Entre Ríos). Si Google no la conoce suele devolver el centro de la provincia, que queda
            // afuera de este recuadro.
            var ok = r is { } p && p.lat is > -35.7m and < -32.4m && p.lng is > -60.3m and < -57.0m;
            _db.Me1LocalidadUbicaciones.Add(new Me1LocalidadUbicacion
            {
                Clave = clave,
                Lat = ok ? r!.Value.lat : null,
                Lng = ok ? r!.Value.lng : null,
                Encontrado = ok,
                BuscadoAt = DateTime.UtcNow
            });
            if (ok) encontradas++;
        }
        await _db.SaveChangesAsync();

        return Ok(new { buscadas = pendientes.Count, encontradas });
    }

    // ============================================================
    // Mandar el tarifario a MeLi
    // ============================================================

    /// <summary>
    /// Arma el tarifario (plantilla oficial de MeLi, hoja "Tabla") con los CPs que se ofrecen y lo
    /// sube a cada cuenta de MeLi que tenga me1 activo. MeLi lo procesa aparte: el estado se ve en
    /// GET meli-estado.
    /// </summary>
    [HttpPost("enviar-a-meli")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> EnviarAMeli()
    {
        var excluidos = (await _db.Me1CpExcluidos.Select(e => e.Cp).ToListAsync()).ToHashSet();
        var filas = ArmarFilas(excluidos);
        var cantidadCps = filas.Sum(f => f.CpFin - f.CpInicio + 1);
        if (cantidadCps == 0) return BadRequest(new { error = "No queda ningún código postal para ofrecer." });

        var baseUrl = (await _db.AppSettings.FindAsync("mapeo.public_base_url"))?.Value;
        if (string.IsNullOrWhiteSpace(baseUrl) || !baseUrl.StartsWith("https://")) baseUrl = "https://app.palanica.com.ar";
        var callbackUrl = $"{baseUrl.TrimEnd('/')}/api/meli/me1/codigos-postales/meli-callback";
        var servicio = (await _db.AppSettings.FindAsync(SettingServicio))?.Value;
        if (string.IsNullOrWhiteSpace(servicio)) servicio = ServicioDefault;

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        var resultados = new List<object>();
        var cuentasConMe1 = 0;

        foreach (var acc in await _accountService.GetAllAccountEntitiesAsync())
        {
            var token = await _accountService.GetValidTokenAsync(acc);
            if (token is null) continue;

            // Solo las cuentas que tienen me1 habilitado
            if (!await TieneMe1Async(http, token, acc.MeliUserId)) continue;
            cuentasConMe1++;

            var envio = new Me1TarifaEnvio
            {
                MeliUserId = acc.MeliUserId,
                Cuenta = acc.Nickname,
                CantidadCps = cantidadCps,
                EnviadoAt = DateTime.UtcNow,
                EnviadoPor = User.Identity?.Name
            };
            try
            {
                var plantilla = await BajarPlantillaAsync(http, token);
                var archivo = LlenarPlantilla(plantilla, filas);

                using var form = new MultipartFormDataContent();
                form.Add(new StringContent("MLA"), "site");
                form.Add(new StringContent(servicio), "service");
                form.Add(new StringContent(callbackUrl), "callback_url");
                var fileContent = new ByteArrayContent(archivo);
                fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
                form.Add(fileContent, "file", "tarifario_me1.xlsx");

                using var rq = new HttpRequestMessage(HttpMethod.Post, "https://api.mercadolibre.com/shipping/me1/v1/tariff/update") { Content = form };
                rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var resp = await http.SendAsync(rq);
                var body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    envio.ResourceId = doc.RootElement.TryGetProperty("resource_id", out var rid) ? rid.GetString() : null;
                    envio.Estado = "enviado";
                }
                else
                {
                    envio.Estado = "rechazado";
                    envio.Detalle = Recortar($"MeLi respondió {(int)resp.StatusCode}: {body}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Me1 tarifario: fallo enviando a la cuenta {Cuenta}", acc.Nickname);
                envio.Estado = "rechazado";
                envio.Detalle = Recortar($"Error: {ex.Message}");
            }
            _db.Me1TarifaEnvios.Add(envio);
            await _db.SaveChangesAsync();
            resultados.Add(new { cuenta = envio.Cuenta, estado = envio.Estado, detalle = envio.Detalle });

            await _audit.LogAsync("Me1.CodigosPostales", envio.Id.ToString(), "enviar_tarifario_meli",
                $"{acc.Nickname}: {cantidadCps} CPs, {envio.Estado} {envio.ResourceId} {envio.Detalle}", User.Identity?.Name);
        }

        if (cuentasConMe1 == 0)
            return BadRequest(new { error = "Ninguna cuenta de MeLi conectada tiene me1 activo." });

        return Ok(new { ok = true, cantidadCps, resultados });
    }

    /// <summary>
    /// Estado de los últimos envíos a MeLi (consulta a MeLi los que siguen procesándose) y si hay
    /// cambios en los CPs que todavía no se mandaron.
    /// </summary>
    [HttpGet("meli-estado")]
    public async Task<IActionResult> MeliEstado()
    {
        var ultimos = await _db.Me1TarifaEnvios.OrderByDescending(e => e.Id).Take(6).ToListAsync();

        var pendientes = ultimos.Where(e => e.Estado == "enviado" && e.ResourceId != null).ToList();
        if (pendientes.Count > 0)
        {
            var http = _httpFactory.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(20);
            var cuentas = await _accountService.GetAllAccountEntitiesAsync();
            foreach (var e in pendientes)
            {
                var acc = cuentas.FirstOrDefault(a => a.MeliUserId == e.MeliUserId);
                if (acc is null) continue;
                try { await RefrescarEstadoAsync(http, acc, e); }
                catch (Exception ex) { _logger.LogWarning(ex, "Me1 tarifario: no se pudo consultar {Rid}", e.ResourceId); }
            }
            await _db.SaveChangesAsync();
        }

        // ¿Hay cambios sin mandar? = el último cambio de CPs es posterior al último envío que MeLi aceptó.
        var ultimoCambioTxt = (await _db.AppSettings.FindAsync(SettingUltimoCambio))?.Value;
        DateTime? ultimoCambio = DateTime.TryParse(ultimoCambioTxt, null, System.Globalization.DateTimeStyles.RoundtripKind, out var uc) ? uc : null;
        var ultimoOk = await _db.Me1TarifaEnvios.Where(e => e.Estado == "active").OrderByDescending(e => e.Id).FirstOrDefaultAsync();
        var hayEnCurso = ultimos.Any(e => e.Estado == "enviado");
        var cambiosSinEnviar = ultimoCambio.HasValue && (ultimoOk is null || ultimoOk.EnviadoAt < ultimoCambio.Value);

        return Ok(new
        {
            cambiosSinEnviar,
            hayEnCurso,
            ultimoCambioAt = ultimoCambio,
            ultimoOkAt = ultimoOk?.EnviadoAt,
            envios = ultimos.Select(e => new
            {
                e.Id, e.Cuenta, e.Estado, e.Detalle, e.CantidadCps, e.EnviadoAt, e.EnviadoPor, e.ActualizadoAt
            })
        });
    }

    /// <summary>
    /// MeLi avisa acá cuando terminó de procesar un tarifario. No se confía en lo que dice el body:
    /// solo se usa el resource_id para volver a preguntarle a MeLi con nuestro token.
    /// MeLi espera respuesta en 1 segundo, así que se contesta enseguida y se consulta aparte.
    /// </summary>
    [HttpPost("meli-callback")]
    [AllowAnonymous]
    public async Task<IActionResult> MeliCallback()
    {
        try
        {
            using var reader = new StreamReader(Request.Body);
            var body = await reader.ReadToEndAsync();
            using var doc = JsonDocument.Parse(body);
            var rid = doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("resource_id", out var r) ? r.GetString() : null;
            _logger.LogInformation("Me1 tarifario: callback de MeLi para {Rid}", rid);
            // El estado real se consulta en GET meli-estado (la pantalla lo pide sola).
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Me1 tarifario: callback de MeLi ilegible");
        }
        return Ok();
    }

    // ============================================================
    // Helpers
    // ============================================================

    private record FilaTarifa(int CpInicio, int CpFin, decimal Precio);

    /// <summary>Junta CPs consecutivos con el mismo precio en un solo renglón (CP inicio - CP fin).</summary>
    private static List<FilaTarifa> ArmarFilas(HashSet<int> excluidos)
    {
        var filas = new List<FilaTarifa>();
        foreach (var (cpFrom, cpTo, precio, _) in MeliMe1Controller.TARIFAS)
            for (int cp = cpFrom; cp <= cpTo; cp++)
            {
                if (excluidos.Contains(cp)) continue;
                var ult = filas.Count > 0 ? filas[^1] : null;
                if (ult != null && ult.CpFin == cp - 1 && ult.Precio == precio)
                    filas[^1] = ult with { CpFin = cp };
                else
                    filas.Add(new FilaTarifa(cp, cp, precio));
            }
        return filas;
    }

    private static async Task<bool> TieneMe1Async(HttpClient http, string token, long meliUserId)
    {
        using var rq = new HttpRequestMessage(HttpMethod.Get, $"https://api.mercadolibre.com/users/{meliUserId}/shipping_preferences");
        rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.SendAsync(rq);
        if (!resp.IsSuccessStatusCode) return false;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("modes", out var modes)
            && modes.ValueKind == JsonValueKind.Array
            && modes.EnumerateArray().Any(m => m.GetString() == "me1");
    }

    private static async Task<byte[]> BajarPlantillaAsync(HttpClient http, string token)
    {
        using var rq = new HttpRequestMessage(HttpMethod.Get, "https://api.mercadolibre.com/shipping/me1/v1/tariff/template?site=MLA");
        rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.SendAsync(rq);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) throw new Exception($"no se pudo bajar la plantilla de MeLi ({(int)resp.StatusCode})");
        using var doc = JsonDocument.Parse(body);
        return Convert.FromBase64String(doc.RootElement.GetProperty("content").GetString() ?? "");
    }

    /// <summary>
    /// Arma el archivo con la estructura de la plantilla oficial: mismas hojas en el mismo orden
    /// ("Tabla ejemplo", "Limitantes", "Tabla"), sin renombrar ni borrar ninguna (MeLi lo rechaza).
    /// La hoja "Tabla" se arma de cero copiando los encabezados (fila 3, desde la columna B): borrar
    /// sus ~55.000 renglones de ejemplo con ClosedXML tarda minutos. Los limitantes opcionales
    /// (aforo, etc.) quedan vacíos. Peso 0-999 kg sin recargo por kg extra => tarifa FIJA por zona.
    /// Plazo 1 día hábil.
    /// </summary>
    private static byte[] LlenarPlantilla(byte[] archivoPlantilla, List<FilaTarifa> filas)
    {
        using var plantilla = new XLWorkbook(new MemoryStream(archivoPlantilla));
        using var wb = new XLWorkbook();
        foreach (var hoja in plantilla.Worksheets)
        {
            if (hoja.Name != "Tabla") { hoja.CopyTo(wb, hoja.Name); continue; }

            var ws = wb.Worksheets.Add("Tabla");
            for (int r = 1; r <= 3; r++)
                for (int c = 1; c <= 8; c++)
                {
                    ws.Cell(r, c).Value = hoja.Cell(r, c).Value;
                    ws.Cell(r, c).Style = hoja.Cell(r, c).Style;
                }
            for (int c = 1; c <= 8; c++) ws.Column(c).Width = hoja.Column(c).Width;

            int row = 4;
            foreach (var f in filas)
            {
                ws.Cell(row, 2).Value = f.CpInicio;
                ws.Cell(row, 3).Value = f.CpFin;
                ws.Cell(row, 4).Value = 0;
                ws.Cell(row, 5).Value = 999;
                ws.Cell(row, 6).Value = f.Precio;
                ws.Cell(row, 7).Value = 0;
                ws.Cell(row, 8).Value = 1;
                row++;
            }
        }
        if (!wb.TryGetWorksheet("Tabla", out _)) throw new Exception("la plantilla de MeLi no trae la hoja \"Tabla\"");

        if (wb.TryGetWorksheet("Limitantes", out var lim))
            lim.Range(2, 2, 9, 2).Clear(XLClearOptions.Contents);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private async Task RefrescarEstadoAsync(HttpClient http, MeliAccount acc, Me1TarifaEnvio e)
    {
        var token = await _accountService.GetValidTokenAsync(acc);
        if (token is null) return;
        using var rq = new HttpRequestMessage(HttpMethod.Get, $"https://api.mercadolibre.com/shipping/me1/v1/tariff/{e.ResourceId}");
        rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp = await http.SendAsync(rq);
        if (!resp.IsSuccessStatusCode) return;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var status = (doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null)?.ToLowerInvariant();
        switch (status)
        {
            case "active":
                e.Estado = "active";
                e.Detalle = null;
                e.ActualizadoAt = DateTime.UtcNow;
                break;
            case "error":
            case "inactive":
                e.Estado = status;
                e.Detalle = Recortar(doc.RootElement.TryGetProperty("errors", out var errs) ? errs.GetRawText() : null);
                e.ActualizadoAt = DateTime.UtcNow;
                break;
            // created / validating: MeLi todavía lo está procesando
        }
    }

    private static string? Recortar(string? s) => s is null ? null : (s.Length > 1900 ? s[..1900] + "…" : s);
}
