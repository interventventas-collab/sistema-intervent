using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Parte B: trae los MOVIMIENTOS de la cuenta de Mercado Pago vía el Reporte oficial
/// "Dinero en la cuenta" (settlement report). Flujo asincrónico:
///   1) POST crea el reporte para un rango de fechas → MP lo genera (tarda).
///   2) Se pollea la lista hasta que aparece el archivo listo.
///   3) GET baja el CSV y se parsea a Mp_Movimientos (dedup por hash).
/// La columna SETTLEMENT_NET_AMOUNT es el impacto real en el saldo.
///
/// Como la API de reportes tiene algunas variantes de URL según la cuenta, se prueban
/// varias y se loguea todo para poder depurar en producción. Pedido de Osmar 2026-07-05.
/// </summary>
public class MpReportesService
{
    private readonly MpAccountService _accounts;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AppDbContext _db;
    private readonly ILogger<MpReportesService> _logger;

    private const string ApiBase = "https://api.mercadopago.com";
    private const string ReportBase = ApiBase + "/v1/account/settlement_report";

    public MpReportesService(MpAccountService accounts, IHttpClientFactory httpFactory,
        AppDbContext db, ILogger<MpReportesService> logger)
    {
        _accounts = accounts;
        _httpFactory = httpFactory;
        _db = db;
        _logger = logger;
    }

    public record SyncResult(bool Ok, int Nuevos, int TotalFilas, string? Error, bool EnProceso);
    private record ReporteInfo(string File, string? Status, string? Created);

    public async Task<SyncResult> SincronizarAsync(int dias = 30)
    {
        if (dias < 1) dias = 1;
        if (dias > 60) dias = 60; // los reportes de rangos grandes tardan/pesan mucho

        var token = await _accounts.GetTokenAsync();
        if (string.IsNullOrEmpty(token))
            return new SyncResult(false, 0, 0, "No hay Access Token de Mercado Pago cargado", false);

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(60);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var end = DateTime.UtcNow;
        var begin = end.AddDays(-dias);
        string beginIso = begin.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        string endIso = end.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        try
        {
            // 1) Listar reportes que MP ya tenga generados (listos, con archivo).
            var reportes = (await ListarReportesAsync(http))
                .Where(r => EstaListo(r.Status))
                .OrderByDescending(r => ParseDate(r.Created ?? "") ?? DateTime.MinValue)
                .ToList();

            // 2) Buscar el más reciente que TODAVÍA no procesamos (dedup por ReporteArchivo).
            string? pendiente = null;
            foreach (var r in reportes)
            {
                var yaProcesado = await _db.MpMovimientos.AnyAsync(m => m.ReporteArchivo == r.File);
                if (!yaProcesado) { pendiente = r.File; break; }
            }

            if (pendiente is not null)
            {
                // 3a) Hay un reporte nuevo listo: bajarlo y procesarlo (rápido, sin esperar).
                var csv = await DescargarAsync(http, pendiente);
                if (string.IsNullOrWhiteSpace(csv))
                    return new SyncResult(false, 0, 0, "El reporte está listo pero no se pudo descargar el archivo.", false);

                var (nuevos, total) = await ProcesarCsvAsync(csv, pendiente);
                _logger.LogInformation("[MP reportes] {File}: {Nuevos} nuevos de {Total} filas", pendiente, nuevos, total);
                // Pedimos uno fresco para la próxima vez (no esperamos).
                await CrearReporteAsync(http, beginIso, endIso);
                return new SyncResult(true, nuevos, total, null, false);
            }

            // 3b) No hay ninguno nuevo listo: pedimos uno y avisamos que se está generando.
            var (okCreate, errCreate) = await CrearReporteAsync(http, beginIso, endIso);
            if (!okCreate) return new SyncResult(false, 0, 0, errCreate, false);
            return new SyncResult(true, 0, 0,
                "Le pedí el reporte a Mercado Pago y se está generando (puede tardar 1-3 minutos por el volumen de tu cuenta). Volvé a tocar 'Actualizar movimientos' en un ratito y lo traigo.", true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MP reportes] error");
            return new SyncResult(false, 0, 0, "No se pudo traer los movimientos: " + ex.Message, false);
        }
    }

    // ─────────── Crear reporte ───────────
    private async Task<(bool ok, string? error)> CrearReporteAsync(HttpClient http, string beginIso, string endIso)
    {
        var payload = JsonSerializer.Serialize(new { begin_date = beginIso, end_date = endIso });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await http.PostAsync(ReportBase, content);
        var body = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode) return (true, null);
        _logger.LogWarning("[MP reportes] crear falló {Status}: {Body}", (int)resp.StatusCode, Snippet(body));
        return (false, InterpretarError((int)resp.StatusCode, body, "generar el reporte"));
    }

    // ─────────── Listar reportes (prueba variantes de URL) ───────────
    private async Task<List<ReporteInfo>> ListarReportesAsync(HttpClient http)
    {
        foreach (var url in new[] { ReportBase + "/list", ReportBase, ReportBase + "/search" })
        {
            try
            {
                var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) continue;
                var body = await resp.Content.ReadAsStringAsync();
                var lista = ParseListaReportes(body);
                if (lista.Count >= 0) return lista; // devolvió JSON válido (aunque sea vacío)
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[MP reportes] listar {Url} falló", url); }
        }
        return new List<ReporteInfo>();
    }

    private static List<ReporteInfo> ParseListaReportes(string body)
    {
        var result = new List<ReporteInfo>();
        if (string.IsNullOrWhiteSpace(body)) return result;
        using var doc = JsonDocument.Parse(body);
        JsonElement arr;
        if (doc.RootElement.ValueKind == JsonValueKind.Array) arr = doc.RootElement;
        else if (doc.RootElement.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array) arr = r;
        else return result;

        foreach (var it in arr.EnumerateArray())
        {
            var file = GetStr(it, "file_name") ?? GetStr(it, "file");
            if (string.IsNullOrEmpty(file)) continue;
            result.Add(new ReporteInfo(file, GetStr(it, "status"), GetStr(it, "date_created") ?? GetStr(it, "created_from")));
        }
        return result;
    }

    private static bool EstaListo(string? status)
    {
        // Si aparece con file_name, casi siempre ya está listo. Igual descartamos estados "en curso".
        if (string.IsNullOrEmpty(status)) return true;
        var s = status.ToLowerInvariant();
        return s is not ("pending" or "processing" or "in_process" or "generating" or "queued" or "started");
    }

    // ─────────── Descargar (prueba variantes) ───────────
    private async Task<string?> DescargarAsync(HttpClient http, string fileName)
    {
        foreach (var url in new[] { $"{ReportBase}/{fileName}", $"{ReportBase}/download/{fileName}" })
        {
            try
            {
                var resp = await http.GetAsync(url);
                if (!resp.IsSuccessStatusCode) continue;
                var txt = await resp.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(txt) && txt.Contains(',') || txt?.Contains(';') == true) return txt;
                if (!string.IsNullOrWhiteSpace(txt)) return txt;
            }
            catch (Exception ex) { _logger.LogDebug(ex, "[MP reportes] descargar {Url} falló", url); }
        }
        return null;
    }

    // ─────────── Parseo CSV → Mp_Movimientos ───────────
    private async Task<(int nuevos, int total)> ProcesarCsvAsync(string csv, string fileName)
    {
        var lineas = csv.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lineas.Length < 2) return (0, 0);

        char sep = lineas[0].Count(c => c == ';') >= lineas[0].Count(c => c == ',') ? ';' : ',';
        var headers = SplitCsv(lineas[0], sep).Select(h => h.Trim().ToUpperInvariant()).ToList();
        int Idx(params string[] names) { foreach (var n in names) { var i = headers.IndexOf(n); if (i >= 0) return i; } return -1; }

        int iSource = Idx("SOURCE_ID", "OPERATION_ID", "PAYMENT_ID");
        int iTxDate = Idx("TRANSACTION_DATE", "DATE");
        int iSetDate = Idx("SETTLEMENT_DATE", "MONEY_RELEASE_DATE");
        int iType = Idx("TRANSACTION_TYPE", "DESCRIPTION", "TYPE");
        int iDesc = Idx("DESCRIPTION", "DETAIL", "TRANSACTION_TYPE");
        int iGross = Idx("TRANSACTION_AMOUNT", "GROSS_AMOUNT", "REAL_AMOUNT");
        int iFee = Idx("FEE_AMOUNT", "MP_FEE_AMOUNT", "MKP_FEE_AMOUNT");
        int iNet = Idx("SETTLEMENT_NET_AMOUNT", "NET_CREDIT_AMOUNT", "NET_AMOUNT");
        int iCur = Idx("SETTLEMENT_CURRENCY", "TRANSACTION_CURRENCY", "CURRENCY");
        int iPm = Idx("PAYMENT_METHOD", "PAYMENT_METHOD_TYPE");
        int iExt = Idx("EXTERNAL_REFERENCE");
        int iOrder = Idx("ORDER_ID", "OPERATION_ID");

        if (iNet < 0 && iGross < 0)
        {
            _logger.LogWarning("[MP reportes] CSV sin columnas de monto reconocidas. Headers: {H}", string.Join("|", headers.Take(20)));
            return (0, 0);
        }

        var existentes = (await _db.MpMovimientos.Select(m => m.HashUnico).ToListAsync()).ToHashSet();
        int nuevos = 0, total = 0;

        for (int li = 1; li < lineas.Length; li++)
        {
            var cols = SplitCsv(lineas[li], sep);
            string Get(int i) => (i >= 0 && i < cols.Count) ? cols[i].Trim() : "";
            total++;

            var fecha = ParseDate(Get(iTxDate)) ?? ParseDate(Get(iSetDate)) ?? DateTime.UtcNow;
            var neto = ParseDec(Get(iNet));
            var bruto = ParseDec(Get(iGross));
            var fee = ParseDec(Get(iFee));
            var source = Trunc(Get(iSource), 60);
            var tipo = Trunc(Get(iType), 60);

            var raw = $"{source}|{tipo}|{fecha:yyyyMMddHHmmss}|{bruto}|{neto}";
            var hash = Sha(raw);
            if (existentes.Contains(hash)) continue;

            _db.MpMovimientos.Add(new MpMovimiento
            {
                SourceId = source,
                Fecha = fecha,
                FechaLiquidacion = ParseDate(Get(iSetDate)),
                TipoTransaccion = tipo,
                Descripcion = Trunc(Get(iDesc), 300),
                MontoBruto = bruto,
                Comision = fee,
                MontoNeto = neto,
                Moneda = Trunc(Get(iCur), 10),
                MedioPago = Trunc(Get(iPm), 60),
                ReferenciaExterna = Trunc(Get(iExt), 200),
                OrderId = Trunc(Get(iOrder), 60),
                HashUnico = hash,
                ReporteArchivo = Trunc(fileName, 200),
                ImportadoAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow
            });
            existentes.Add(hash);
            nuevos++;

            if (nuevos % 500 == 0) await _db.SaveChangesAsync();
        }
        await _db.SaveChangesAsync();
        return (nuevos, total);
    }

    // ─────────── Helpers ───────────
    private static string? GetStr(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Trunc(string? s, int max) => string.IsNullOrEmpty(s) ? null : (s.Length > max ? s.Substring(0, max) : s);

    private static decimal ParseDec(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0m;
        s = s.Trim().Replace("\"", "");
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d)) return d;
        // fallback formato AR (1.234,56)
        var alt = s.Replace(".", "").Replace(",", ".");
        if (decimal.TryParse(alt, NumberStyles.Any, CultureInfo.InvariantCulture, out var d2)) return d2;
        return 0m;
    }

    private static DateTime? ParseDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim().Replace("\"", "");
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto)) return dto.UtcDateTime;
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt)) return dt.ToUniversalTime();
        return null;
    }

    private static List<string> SplitCsv(string line, char sep)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var c in line)
        {
            if (c == '"') inQuotes = !inQuotes;
            else if (c == sep && !inQuotes) { result.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        result.Add(sb.ToString());
        return result;
    }

    private static string Sha(string s)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(Encoding.UTF8.GetBytes(s))).Substring(0, 40);
    }

    private static string Snippet(string? body) => string.IsNullOrEmpty(body) ? "" : (body.Length > 300 ? body.Substring(0, 300) : body);

    private static string InterpretarError(int status, string body, string accion)
    {
        var snip = Snippet(body);
        return status switch
        {
            401 => "El Access Token no es válido o expiró.",
            403 => $"El token no tiene permiso para {accion}. Puede que tu cuenta necesite habilitar los reportes. {snip}",
            _ => $"Mercado Pago respondió error {status} al {accion}. {snip}"
        };
    }
}
