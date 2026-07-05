using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Trae los cobros recibidos por Mercado Pago desde la API oficial /v1/payments/search y los
/// guarda en Mp_Pagos (dedup por MpPaymentId). Este endpoint SI funciona con el Access Token
/// de la app (a diferencia del saldo directo, que MP deprecó). Pedido de Osmar 2026-07-05.
/// </summary>
public class MpPagosService
{
    private readonly MpAccountService _accounts;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AppDbContext _db;
    private readonly ILogger<MpPagosService> _logger;

    private const string ApiBase = "https://api.mercadopago.com";

    public MpPagosService(MpAccountService accounts, IHttpClientFactory httpFactory,
        AppDbContext db, ILogger<MpPagosService> logger)
    {
        _accounts = accounts;
        _httpFactory = httpFactory;
        _db = db;
        _logger = logger;
    }

    public record SyncPagosResult(bool Ok, int Nuevos, int Actualizados, int TotalTraidos, string? Error);

    /// <summary>Sincroniza los pagos de los últimos <paramref name="dias"/> días (máx 365).</summary>
    public async Task<SyncPagosResult> SincronizarAsync(int dias = 30)
    {
        if (dias < 1) dias = 1;
        if (dias > 365) dias = 365;

        var token = await _accounts.GetTokenAsync();
        if (string.IsNullOrEmpty(token))
            return new SyncPagosResult(false, 0, 0, 0, "No hay Access Token de Mercado Pago cargado");

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(40);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        int nuevos = 0, actualizados = 0, total = 0;
        int offset = 0;
        const int limit = 50;
        const int maxPaginas = 40; // tope de seguridad (2000 pagos)

        // IDs ya existentes para no consultar 1x1.
        var existentes = await _db.MpPagos.ToDictionaryAsync(p => p.MpPaymentId, p => p);

        try
        {
            for (int pagina = 0; pagina < maxPaginas; pagina++)
            {
                var url = $"{ApiBase}/v1/payments/search?sort=date_created&criteria=desc" +
                          $"&range=date_created&begin_date=NOW-{dias}DAYS&end_date=NOW" +
                          $"&limit={limit}&offset={offset}";
                var resp = await http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return new SyncPagosResult(false, nuevos, actualizados, total,
                        InterpretarError((int)resp.StatusCode, body));

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    break;

                int enPagina = 0;
                foreach (var r in results.EnumerateArray())
                {
                    enPagina++;
                    total++;
                    var mpId = ReadLong(r, "id");
                    if (mpId is null) continue;

                    var fecha = ReadDate(r, "date_approved") ?? ReadDate(r, "date_created") ?? DateTime.UtcNow;
                    var estado = ReadString(r, "status");
                    var estadoDet = ReadString(r, "status_detail");
                    var monto = ReadDecimal(r, "transaction_amount") ?? 0m;
                    var neto = ReadNetReceived(r);
                    var desc = ReadString(r, "description");
                    var (payerEmail, payerNombre) = ReadPayer(r);
                    var medio = ReadString(r, "payment_method_id");
                    var tipoOp = ReadString(r, "operation_type");
                    var extRef = ReadString(r, "external_reference");

                    if (existentes.TryGetValue(mpId.Value, out var ya))
                    {
                        // Actualizar campos que cambian (estado, neto).
                        ya.Estado = estado; ya.EstadoDetalle = estadoDet;
                        ya.Monto = monto; ya.MontoNeto = neto;
                        ya.Fecha = fecha;
                        actualizados++;
                    }
                    else
                    {
                        var p = new MpPago
                        {
                            MpPaymentId = mpId.Value,
                            Fecha = fecha,
                            Estado = estado,
                            EstadoDetalle = estadoDet,
                            Monto = monto,
                            MontoNeto = neto,
                            Descripcion = Trunc(desc, 300),
                            PayerEmail = Trunc(payerEmail, 200),
                            PayerNombre = Trunc(payerNombre, 200),
                            MedioPago = Trunc(medio, 50),
                            TipoOperacion = Trunc(tipoOp, 50),
                            ReferenciaExterna = Trunc(extRef, 200),
                            ImportadoAt = DateTime.UtcNow,
                            CreatedAt = DateTime.UtcNow
                        };
                        _db.MpPagos.Add(p);
                        existentes[mpId.Value] = p;
                        nuevos++;
                    }
                }

                await _db.SaveChangesAsync();

                var totalDisponible = ReadPagingTotal(root);
                offset += limit;
                if (enPagina < limit) break;                 // última página
                if (totalDisponible.HasValue && offset >= totalDisponible.Value) break;
            }

            return new SyncPagosResult(true, nuevos, actualizados, total, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MP pagos] error sincronizando");
            return new SyncPagosResult(false, nuevos, actualizados, total, "No se pudo traer los cobros: " + ex.Message);
        }
    }

    // ─── Helpers de parseo defensivo ───
    private static string? Trunc(string? s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length > max ? s.Substring(0, max) : s);

    private static long? ReadLong(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var ns)) return ns;
        return null;
    }

    private static string? ReadString(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static decimal? ReadDecimal(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String && decimal.TryParse(v.GetString(),
            System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var ds)) return ds;
        return null;
    }

    private static DateTime? ReadDate(JsonElement e, string prop)
    {
        var s = ReadString(e, prop);
        if (string.IsNullOrEmpty(s)) return null;
        return DateTimeOffset.TryParse(s, out var dto) ? dto.UtcDateTime : null;
    }

    private static decimal? ReadNetReceived(JsonElement e)
    {
        if (e.TryGetProperty("transaction_details", out var td) && td.ValueKind == JsonValueKind.Object)
            return ReadDecimal(td, "net_received_amount");
        return null;
    }

    private static (string? email, string? nombre) ReadPayer(JsonElement e)
    {
        if (!e.TryGetProperty("payer", out var p) || p.ValueKind != JsonValueKind.Object) return (null, null);
        var email = ReadString(p, "email");
        string? nombre = null;
        var first = ReadString(p, "first_name");
        var last = ReadString(p, "last_name");
        if (!string.IsNullOrWhiteSpace(first) || !string.IsNullOrWhiteSpace(last))
            nombre = string.Join(" ", new[] { first, last }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return (email, nombre);
    }

    private static int? ReadPagingTotal(JsonElement root)
    {
        if (root.TryGetProperty("paging", out var pg) && pg.ValueKind == JsonValueKind.Object)
        {
            if (pg.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt32(out var n))
                return n;
        }
        return null;
    }

    private static string InterpretarError(int status, string body)
    {
        var snippet = string.IsNullOrEmpty(body) ? "" : (body.Length > 300 ? body.Substring(0, 300) : body);
        return status switch
        {
            401 => "El Access Token no es válido o expiró. Revisalo en Mercado Pago Developers.",
            403 => "El token no tiene permiso para leer los pagos de esta cuenta. " + snippet,
            _ => $"Mercado Pago respondió error {status}. {snippet}"
        };
    }
}
