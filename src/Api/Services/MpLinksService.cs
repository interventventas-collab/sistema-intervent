using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Links de pago de Mercado Pago (Checkout Pro). Crea la "preferencia" con el monto y las formas
/// de pago permitidas, y después busca el pago por external_reference para saber si pagaron,
/// cómo y cuánto quedó neto. Usa el mismo Access Token de la cuenta de MP ya conectada.
/// Pedido 2026-10-13.
/// </summary>
public class MpLinksService
{
    private readonly MpAccountService _accounts;
    private readonly IHttpClientFactory _httpFactory;
    private readonly AppDbContext _db;
    private readonly ILogger<MpLinksService> _logger;

    private const string ApiBase = "https://api.mercadopago.com";

    public MpLinksService(MpAccountService accounts, IHttpClientFactory httpFactory,
        AppDbContext db, ILogger<MpLinksService> logger)
    {
        _accounts = accounts;
        _httpFactory = httpFactory;
        _db = db;
        _logger = logger;
    }

    public record CrearLinkRequest(decimal Monto, string? Concepto, string? Cliente, string? Opcion);

    public record LinkDto(int Id, DateTime CreatedAt, decimal Monto, string Concepto, string? Cliente,
        string Opcion, string? Link, string Estado, DateTime? PagadoAt, decimal? MontoNeto,
        decimal? Comision, string? MedioPago, string? TipoMedio, int? Cuotas, string? PagadorNombre,
        string? UltimoIntento, string? CreadoPor);

    /// <summary>Datos para la estimación: % cargado a mano y % real de los links ya pagados.</summary>
    public record ComisionInfoDto(bool HayCuenta, string? Cuenta, decimal? PctManual,
        decimal? PctReal, int LinksPagados);

    private static LinkDto Map(MpLinkPago l) => new(l.Id, l.CreatedAt, l.Monto, l.Concepto, l.Cliente,
        l.Opcion, l.Link, l.Estado, l.PagadoAt, l.MontoNeto,
        l.MontoNeto.HasValue ? l.Monto - l.MontoNeto.Value : null,
        l.MedioPago, l.TipoMedio, l.Cuotas, l.PagadorNombre, l.UltimoIntento, l.CreadoPor);

    private HttpClient Http(string token)
    {
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(30);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    public async Task<ComisionInfoDto> GetComisionInfoAsync()
    {
        var acc = await _accounts.GetEntityAsync();
        var pagados = await _db.MpLinksPago.AsNoTracking()
            .Where(l => l.Estado == "Pagado" && l.MontoNeto != null && l.Monto > 0)
            .OrderByDescending(l => l.PagadoAt).Take(20)
            .Select(l => new { l.Monto, l.MontoNeto }).ToListAsync();
        decimal? real = null;
        if (pagados.Count > 0)
        {
            var bruto = pagados.Sum(p => p.Monto);
            var neto = pagados.Sum(p => p.MontoNeto!.Value);
            real = Math.Round(100m * (bruto - neto) / bruto, 2);
        }
        var hay = acc is not null && !string.IsNullOrEmpty(acc.AccessToken) && acc.IsActive;
        return new ComisionInfoDto(hay, acc?.Nickname ?? acc?.Alias, acc?.LinkComisionPct, real, pagados.Count);
    }

    public async Task<(bool ok, string? error)> GuardarPctManualAsync(decimal? pct)
    {
        if (pct is < 0 or > 30) return (false, "El porcentaje tiene que estar entre 0 y 30.");
        var acc = await _accounts.GetEntityAsync();
        if (acc is null) return (false, "No hay cuenta de Mercado Pago conectada.");
        acc.LinkComisionPct = pct;
        await _db.SaveChangesAsync();
        return (true, null);
    }

    public async Task<List<LinkDto>> ListarAsync(int dias)
    {
        var desde = DateTime.UtcNow.AddDays(-Math.Clamp(dias, 1, 400));
        var lista = await _db.MpLinksPago.AsNoTracking()
            .Where(l => l.CreatedAt >= desde)
            .OrderByDescending(l => l.Id).ToListAsync();
        return lista.Select(Map).ToList();
    }

    public async Task<(bool ok, string? error, LinkDto? link)> CrearAsync(CrearLinkRequest req, string? usuario)
    {
        if (req.Monto <= 0) return (false, "Poné un monto mayor a cero.", null);
        if (req.Monto > 50_000_000) return (false, "El monto es demasiado grande.", null);
        var opcion = req.Opcion is "3" or "todo" ? req.Opcion : "1";
        var concepto = string.IsNullOrWhiteSpace(req.Concepto) ? "Pago" : req.Concepto.Trim();
        if (concepto.Length > 200) concepto = concepto[..200];
        var cliente = string.IsNullOrWhiteSpace(req.Cliente) ? null : req.Cliente.Trim();
        if (cliente is { Length: > 200 }) cliente = cliente[..200];

        var token = await _accounts.GetTokenAsync();
        if (string.IsNullOrEmpty(token))
            return (false, "No hay cuenta de Mercado Pago conectada. Conectala en Integraciones → Mercado Pago.", null);

        // Primero la fila (necesito el Id para la referencia), después la preferencia en MP.
        var link = new MpLinkPago
        {
            Monto = Math.Round(req.Monto, 2),
            Concepto = concepto,
            Cliente = cliente,
            Opcion = opcion,
            CreadoPor = usuario,
            CreatedAt = DateTime.UtcNow
        };
        _db.MpLinksPago.Add(link);
        await _db.SaveChangesAsync();
        link.ReferenciaExterna = $"AIML-LINK-{link.Id}";

        // Formas de pago: "1" y "3" sacan efectivo (Rapipago/Pago Fácil) y cajero, que tardan
        // en acreditarse; "todo" deja lo que Mercado Pago ofrezca por defecto.
        object paymentMethods = opcion switch
        {
            "todo" => new { },
            _ => new
            {
                installments = opcion == "3" ? 3 : 1,
                excluded_payment_types = new[] { new { id = "ticket" }, new { id = "atm" } }
            }
        };
        var body = new
        {
            items = new[] { new { title = concepto, quantity = 1, unit_price = link.Monto, currency_id = "ARS" } },
            external_reference = link.ReferenciaExterna,
            payment_methods = paymentMethods
        };

        try
        {
            using var http = Http(token);
            var resp = await http.PostAsJsonAsync($"{ApiBase}/checkout/preferences", body);
            var txt = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[MP links] crear preferencia {Status}: {Body}", (int)resp.StatusCode, txt);
                _db.MpLinksPago.Remove(link);
                await _db.SaveChangesAsync();
                var msg = (int)resp.StatusCode is 401 or 403
                    ? "Mercado Pago no aceptó la conexión (el Access Token venció o no tiene permiso para crear cobros)."
                    : $"Mercado Pago no pudo crear el link ({(int)resp.StatusCode}).";
                return (false, msg, null);
            }
            using var doc = JsonDocument.Parse(txt);
            var root = doc.RootElement;
            link.MpPreferenceId = Str(root, "id");
            link.Link = Str(root, "init_point");
            await _db.SaveChangesAsync();
            return (true, null, Map(link));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MP links] error creando link");
            _db.MpLinksPago.Remove(link);
            await _db.SaveChangesAsync();
            return (false, "No me pude comunicar con Mercado Pago. Probá de nuevo en un rato.", null);
        }
    }

    /// <summary>Revisa en MP los links que todavía no figuran pagados (últimos 60 días).</summary>
    public async Task<(bool ok, string? error, int cambiados)> RevisarPendientesAsync()
    {
        var token = await _accounts.GetTokenAsync();
        if (string.IsNullOrEmpty(token)) return (false, "No hay cuenta de Mercado Pago conectada.", 0);
        var desde = DateTime.UtcNow.AddDays(-60);
        var pendientes = await _db.MpLinksPago
            .Where(l => (l.Estado == "Pendiente" || l.Estado == "En proceso") && l.CreatedAt >= desde)
            .OrderByDescending(l => l.Id).Take(50).ToListAsync();
        if (pendientes.Count == 0) return (true, null, 0);

        int cambiados = 0;
        using var http = Http(token);
        foreach (var l in pendientes)
        {
            try
            {
                var url = $"{ApiBase}/v1/payments/search?external_reference={Uri.EscapeDataString(l.ReferenciaExterna)}&sort=date_created&criteria=desc";
                var resp = await http.GetAsync(url);
                var txt = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode is 401 or 403)
                        return (false, "Mercado Pago no aceptó la conexión (revisá el Access Token).", cambiados);
                    continue;
                }
                using var doc = JsonDocument.Parse(txt);
                if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                    continue;
                var pagos = results.EnumerateArray().ToList();
                var antes = l.Estado;
                var aprobado = pagos.FirstOrDefault(p => Str(p, "status") == "approved");
                var enCurso = pagos.FirstOrDefault(p => Str(p, "status") is "pending" or "in_process" or "authorized");
                var devuelto = pagos.FirstOrDefault(p => Str(p, "status") is "refunded" or "charged_back");
                var elegido = aprobado.ValueKind == JsonValueKind.Object ? aprobado
                    : devuelto.ValueKind == JsonValueKind.Object ? devuelto
                    : enCurso.ValueKind == JsonValueKind.Object ? enCurso : default;

                if (elegido.ValueKind == JsonValueKind.Object)
                {
                    l.Estado = Str(elegido, "status") switch
                    {
                        "approved" => "Pagado",
                        "refunded" or "charged_back" => "Devuelto",
                        _ => "En proceso"
                    };
                    l.MpPaymentId = Long(elegido, "id");
                    l.PagadoAt = l.Estado == "Pagado" ? (Date(elegido, "date_approved") ?? DateTime.UtcNow) : null;
                    l.MontoNeto = elegido.TryGetProperty("transaction_details", out var td) && td.ValueKind == JsonValueKind.Object
                        ? Dec(td, "net_received_amount") : null;
                    l.MedioPago = Trunc(Str(elegido, "payment_method_id"), 50);
                    l.TipoMedio = Trunc(Str(elegido, "payment_type_id"), 30);
                    l.Cuotas = (int?)Long(elegido, "installments");
                    l.PagadorNombre = Trunc(Pagador(elegido), 200);
                }
                else if (pagos.Count > 0)
                {
                    // Solo intentos rechazados: sigue pendiente, pero dejo dicho qué pasó.
                    var ult = pagos[0];
                    l.UltimoIntento = Trunc($"Intento rechazado ({Str(ult, "status_detail") ?? Str(ult, "status")})", 200);
                }
                l.RevisadoAt = DateTime.UtcNow;
                if (l.Estado != antes) cambiados++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MP links] error revisando link {Id}", l.Id);
            }
        }
        await _db.SaveChangesAsync();
        return (true, null, cambiados);
    }

    // ─── Helpers de lectura defensiva (mismo criterio que MpPagosService) ───
    private static string? Trunc(string? s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length > max ? s[..max] : s);

    private static string? Str(JsonElement e, string prop)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string prop)
    {
        if (!e.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out var ns)) return ns;
        return null;
    }

    private static decimal? Dec(JsonElement e, string prop)
        => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static DateTime? Date(JsonElement e, string prop)
    {
        var s = Str(e, prop);
        return !string.IsNullOrEmpty(s) && DateTimeOffset.TryParse(s, out var dto) ? dto.UtcDateTime : null;
    }

    private static string? Pagador(JsonElement e)
    {
        if (!e.TryGetProperty("payer", out var p) || p.ValueKind != JsonValueKind.Object) return null;
        var nombre = string.Join(" ", new[] { Str(p, "first_name"), Str(p, "last_name") }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.IsNullOrWhiteSpace(nombre) ? Str(p, "email") : nombre;
    }
}
