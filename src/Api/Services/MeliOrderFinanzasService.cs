using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-28 — Pedido del dueño: en el listado de órdenes de MeLi ver cuánto deja cada venta, con
/// datos CIERTOS de MercadoLibre para cotejar contra el % que calcula Publicaciones.
///
/// La fuente es el PAGO de Mercado Pago de cada orden (/v1/payments/{id}, se consulta con el mismo
/// token de MeLi): trae charges_details con cada cargo que paga el vendedor (comisión, envío,
/// retenciones) y net_received_amount = lo que deposita. Verificado con 3 ventas reales del 28/09:
/// 45.499 − 6.506,36 comisión − 22.243,27 envío − 501,40 retenciones = 16.247,97 depositado.
///
/// ⚠ En un PACK (varias órdenes, un solo envío) MeLi carga el envío ENTERO en una sola de las
/// órdenes: la pantalla suma el pack y reparte el envío entre sus ventas.
///
/// Solo LEE de MeLi/MP (no cambia nada allá). Se completa en segundo plano
/// (MeliOrderFinanzasBackgroundService) y se re-consulta una vez pasados unos días de la venta,
/// por si MeLi ajusta algún cargo después.
/// </summary>
public class MeliOrderFinanzasService
{
    private readonly AppDbContext _db;
    private readonly MeliAccountService _accounts;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<MeliOrderFinanzasService> _logger;

    /// <summary>Hasta cuántos días atrás se completan las ventas.</summary>
    public const int DiasAtras = 60;
    /// <summary>A los cuántos días de la venta se vuelve a consultar una vez.</summary>
    private const int DiasReconsulta = 3;

    public MeliOrderFinanzasService(AppDbContext db, MeliAccountService accounts,
        IHttpClientFactory httpFactory, ILogger<MeliOrderFinanzasService> logger)
    {
        _db = db; _accounts = accounts; _httpFactory = httpFactory; _logger = logger;
    }

    /// <summary>Completa hasta <paramref name="max"/> órdenes pendientes. Devuelve cuántas quedaron completas.</summary>
    public async Task<int> CompletarPendientesAsync(int max, CancellationToken ct, int diasAtras = DiasAtras)
    {
        var desde = DateTime.UtcNow.AddDays(-diasAtras);
        var limiteReconsulta = DateTime.UtcNow.AddDays(-DiasReconsulta);
        var pendientes = await _db.MeliOrders
            .Include(o => o.MeliAccount)
            .Where(o => o.Status == "paid" && o.DateCreated >= desde
                && (o.FinConsultadoAt == null
                    // una sola re-consulta: se consultó en los primeros días y ya pasaron
                    || (o.FinConsultadoAt < o.DateCreated.AddDays(DiasReconsulta) && o.DateCreated < limiteReconsulta)))
            .OrderByDescending(o => o.DateCreated)
            .Take(max)
            .ToListAsync(ct);
        if (pendientes.Count == 0) return 0;

        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(20);
        int ok = 0;
        foreach (var porCuenta in pendientes.Where(o => o.MeliAccount is not null).GroupBy(o => o.MeliAccountId))
        {
            var token = await _accounts.GetValidTokenAsync(porCuenta.First().MeliAccount!);
            if (string.IsNullOrEmpty(token)) continue;
            foreach (var o in porCuenta)
            {
                if (ct.IsCancellationRequested) break;
                bool listo = false;
                try { listo = await ConsultarAsync(o, token, http, ct); }
                catch (Exception ex) { _logger.LogWarning(ex, "[OrdenFinanzas] orden {Id}: no se pudo consultar", o.MeliOrderId); }
                if (listo) ok++;
                // Si falló se marca igual (sin montos), para no reintentarla en cada vuelta: la
                // re-consulta de los días siguientes le da otra oportunidad.
                else o.FinConsultadoAt = DateTime.UtcNow;
                await Task.Delay(250, ct);   // no apurar a MeLi/MP
            }
        }
        await _db.SaveChangesAsync(ct);
        return ok;
    }

    private async Task<bool> ConsultarAsync(MeliOrder o, string token, HttpClient http, CancellationToken ct)
    {
        // 1) El id del pago sale de la orden (el aprobado; si no hay, el primero).
        if (o.MpPaymentId is null)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.mercadolibre.com/orders/{o.MeliOrderId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return false;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (!doc.RootElement.TryGetProperty("payments", out var pays) || pays.ValueKind != JsonValueKind.Array) return false;
            long? elegido = null;
            foreach (var p in pays.EnumerateArray())
            {
                if (!p.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number) continue;
                var st = p.TryGetProperty("status", out var s) ? s.GetString() : null;
                if (st == "approved") { elegido = idEl.GetInt64(); break; }
                elegido ??= idEl.GetInt64();
            }
            if (elegido is null) return false;
            o.MpPaymentId = elegido;
        }

        // 2) El pago en Mercado Pago: cargos al vendedor + neto depositado.
        using var req2 = new HttpRequestMessage(HttpMethod.Get, $"https://api.mercadopago.com/v1/payments/{o.MpPaymentId}");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var resp2 = await http.SendAsync(req2, ct);
        if (!resp2.IsSuccessStatusCode) return false;
        using var pdoc = JsonDocument.Parse(await resp2.Content.ReadAsStringAsync(ct));
        var root = pdoc.RootElement;

        decimal comision = 0, envio = 0, retenciones = 0, otros = 0;
        if (root.TryGetProperty("charges_details", out var charges) && charges.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in charges.EnumerateArray())
            {
                // Solo lo que paga el vendedor (el "collector").
                var from = c.TryGetProperty("accounts", out var acc) && acc.TryGetProperty("from", out var f) ? f.GetString() : null;
                if (from != "collector") continue;
                decimal monto = 0;
                if (c.TryGetProperty("amounts", out var am))
                {
                    if (am.TryGetProperty("original", out var orig) && orig.ValueKind == JsonValueKind.Number) monto = orig.GetDecimal();
                    if (am.TryGetProperty("refunded", out var reff) && reff.ValueKind == JsonValueKind.Number) monto -= reff.GetDecimal();
                }
                var tipo = c.TryGetProperty("type", out var t) ? t.GetString() : null;
                switch (tipo)
                {
                    case "fee": comision += monto; break;
                    case "shipping": envio += monto; break;
                    case "tax": retenciones += monto; break;
                    default: otros += monto; break;
                }
            }
        }
        decimal? neto = null;
        if (root.TryGetProperty("transaction_details", out var td) && td.TryGetProperty("net_received_amount", out var nr)
            && nr.ValueKind == JsonValueKind.Number)
            neto = nr.GetDecimal();

        o.FinComision = comision;
        o.FinEnvio = envio;
        o.FinRetenciones = retenciones;
        o.FinOtros = otros;
        o.FinNeto = neto;
        o.FinConsultadoAt = DateTime.UtcNow;
        return true;
    }
}

/// <summary>2026-09-28: completa en segundo plano cuánto deja cada venta de MeLi (ver MeliOrderFinanzasService).</summary>
public class MeliOrderFinanzasBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliOrderFinanzasBackgroundService> _logger;

    public MeliOrderFinanzasBackgroundService(IServiceScopeFactory scopeFactory, ILogger<MeliOrderFinanzasBackgroundService> logger)
    {
        _scopeFactory = scopeFactory; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            int hechas = 0;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<MeliOrderFinanzasService>();
                hechas = await svc.CompletarPendientesAsync(60, stoppingToken);
                if (hechas > 0) _logger.LogInformation("[OrdenFinanzas] {N} ventas completadas", hechas);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[OrdenFinanzas] error en el ciclo (no crítico)"); }
            // Mientras hay atraso (primera vez: 60 días de ventas) sigue rápido; al día, cada 10 minutos.
            var espera = hechas >= 50 ? TimeSpan.FromSeconds(20) : TimeSpan.FromMinutes(10);
            try { await Task.Delay(espera, stoppingToken); } catch (OperationCanceledException) { return; }
        }
    }
}
