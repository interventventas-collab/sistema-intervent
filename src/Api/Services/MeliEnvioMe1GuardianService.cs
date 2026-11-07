using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Api.Data;
using Api.Models;

namespace Api.Services;

/// <summary>
/// 2026-11-06 — Guardián de me1: si MercadoLibre pasa SOLA una publicación de me1 a me2 (envío
/// gratis a cargo nuestro), el sistema le vuelve a pedir me1 al toque y avisa.
///
/// Por qué existe: el 05/11 las sillas Nina (C4321) y la mesita Carolina estaban en me1 (caja real
/// grande, el comprador paga el envío). Al cambiarles el precio, MeLi las re-evaluó y las pasó a me2
/// con envío gratis obligatorio: una caja de 152 cm cotizaba $75.090 de envío a cargo nuestro.
/// Mandándole otra vez me1 volvían (con la caja real que excede los límites de me2).
///
/// Cómo se entera: cada vez que el sistema relee una publicación (aviso de MeLi al instante, refresco
/// de comisión, push de precio, sincronización) y ve que el envío pasó de "default" (me1) a un modo
/// de me2, la encola acá. Espera 20 s a que MeLi termine de acomodarla, la relee EN VIVO y, si sigue
/// en me2, le manda {"shipping":{"mode":"me1","free_shipping":false}} y verifica a los 15 s.
///   • Volvió → queda anotado en Cambios MeLi (ENVIO_ME1_VUELTO), sin Telegram.
///   • No volvió (MeLi no la deja: medidas dentro de me2, categoría que no acepta) → aviso a la
///     campanita y Telegram (ENVIO_ME1_FALLO). No reintenta esa publicación por 12 h.
///
/// Interruptor: AppSettings meli.guardian_me1.enabled = "true" (default apagado: en desarrollo la base
/// es vieja y la cuenta de MeLi es la REAL, no tiene que salir a tocar publicaciones).
/// </summary>
public class MeliEnvioMe1GuardianService : BackgroundService
{
    private static readonly TimeSpan ESPERA_ANTES = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ESPERA_VERIFICAR = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan NO_REINTENTAR = TimeSpan.FromHours(12);

    // Modos de me2 a los que MeLi suele pasarlas (colecta, punto de despacho, Flex).
    private static readonly HashSet<string> LOGISTICA_ME2 = new(StringComparer.OrdinalIgnoreCase)
        { "cross_docking", "drop_off", "xd_drop_off", "self_service" };

    // MLA → desde cuándo se puede revisar (encolado + 20 s).
    private static readonly ConcurrentDictionary<string, DateTime> _pendientes = new();
    // MLA → último intento (para no pelearse con MeLi si no la deja).
    private static readonly ConcurrentDictionary<string, DateTime> _ultimoIntento = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliEnvioMe1GuardianService> _logger;

    public MeliEnvioMe1GuardianService(IServiceScopeFactory scopeFactory, ILogger<MeliEnvioMe1GuardianService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>¿El envío pasó de me1 ("default") a me2? Lo llaman los lugares que pisan LogisticType.</summary>
    public static bool EsPaseDeMe1AMe2(string? antes, string? ahora)
        => string.Equals(antes, "default", StringComparison.OrdinalIgnoreCase)
           && ahora is not null && LOGISTICA_ME2.Contains(ahora);

    /// <summary>Encola la publicación para revisarla. Barato y sin base: se puede llamar en medio de un guardado.</summary>
    public static void Encolar(string meliItemId)
    {
        if (string.IsNullOrWhiteSpace(meliItemId)) return;
        _pendientes.TryAdd(meliItemId, DateTime.UtcNow + ESPERA_ANTES);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }

            var ahora = DateTime.UtcNow;
            var listas = _pendientes.Where(p => p.Value <= ahora).Select(p => p.Key).ToList();
            foreach (var mla in listas)
            {
                if (stoppingToken.IsCancellationRequested) return;
                _pendientes.TryRemove(mla, out _);
                if (_ultimoIntento.TryGetValue(mla, out var ult) && ahora - ult < NO_REINTENTAR) continue;
                try { await RevisarAsync(mla, stoppingToken); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _logger.LogWarning(ex, "[Guardián me1] {Mla}: falló la revisión", mla); }
            }
        }
    }

    private async Task RevisarAsync(string mla, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var prendido = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key == "meli.guardian_me1.enabled").Select(s => s.Value).FirstOrDefaultAsync(ct);
        if (prendido?.Trim().ToLowerInvariant() is not ("true" or "1" or "on"))
        {
            _logger.LogInformation("[Guardián me1] {Mla} pasó a me2 pero el guardián está apagado (meli.guardian_me1.enabled)", mla);
            return;
        }

        var filas = await db.MeliItems.Include(i => i.MeliAccount).Where(i => i.MeliItemId == mla).ToListAsync(ct);
        var fila = filas.FirstOrDefault();
        if (fila?.MeliAccount is null) return;

        var token = await scope.ServiceProvider.GetRequiredService<MeliAccountService>().GetValidTokenAsync(fila.MeliAccount);
        if (string.IsNullOrEmpty(token)) return;
        var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1) Releer EN VIVO: puede que ya haya vuelto sola o que la hayan pausado/cerrado.
        var antes = await LeerEnvioAsync(http, mla, ct);
        if (antes is null) return;
        if (antes.Value.Status is not ("active" or "paused")) return;
        if (antes.Value.Modo == "me1") return;
        if (antes.Value.Modo != "me2" || !LOGISTICA_ME2.Contains(antes.Value.Logistica ?? "")) return;

        _ultimoIntento[mla] = DateTime.UtcNow;

        // 2) Pedir me1 otra vez.
        var body = JsonSerializer.Serialize(new { shipping = new { mode = "me1", free_shipping = false } });
        using var put = await http.PutAsync($"https://api.mercadolibre.com/items/{mla}",
            new StringContent(body, Encoding.UTF8, "application/json"), ct);
        var putTxt = put.IsSuccessStatusCode ? "" : await put.Content.ReadAsStringAsync(ct);

        // 3) Verificar (MeLi a veces contesta 200 y no lo toma).
        await Task.Delay(ESPERA_VERIFICAR, ct);
        var despues = await LeerEnvioAsync(http, mla, ct);
        var volvio = despues?.Modo == "me1";

        var titulo = fila.Title;
        var envioAPagar = fila.SaleFeeShippingCost;
        var ar = new System.Globalization.CultureInfo("es-AR");
        if (volvio)
        {
            foreach (var f in filas) { f.LogisticType = despues!.Value.Logistica ?? "default"; f.FreeShipping = despues.Value.Gratis; }
            db.MeliCambiosDetectados.Add(new MeliCambioDetectado
            {
                MeliItemId = mla, MeliAccountId = fila.MeliAccountId, Sku = fila.Sku, Title = titulo,
                Tipo = "ENVIO_ME1_VUELTO", ValorAnterior = $"me2 {antes.Value.Logistica}", ValorNuevo = "me1",
                Source = "guardian-me1", DetectedAt = DateTime.UtcNow,
                NotifiedAt = DateTime.UtcNow, // queda anotado, sin Telegram: se arregló solo
                Notes = "MercadoLibre la había pasado a me2 con envío gratis a tu cargo. El sistema la volvió a me1 (el envío lo paga el comprador)."
            });
            await db.SaveChangesAsync(ct);
            _ultimoIntento.TryRemove(mla, out _);
            _logger.LogWarning("[Guardián me1] {Mla} MeLi la pasó a me2 ({Log}) → vuelta a me1 OK", mla, antes.Value.Logistica);
        }
        else
        {
            var costo = envioAPagar is decimal c && c > 0 ? $" Envío a tu cargo: ${c.ToString("N0", ar)}." : "";
            db.MeliCambiosDetectados.Add(new MeliCambioDetectado
            {
                MeliItemId = mla, MeliAccountId = fila.MeliAccountId, Sku = fila.Sku, Title = titulo,
                Tipo = "ENVIO_ME1_FALLO", ValorAnterior = "me1", ValorNuevo = $"me2 {despues?.Logistica ?? antes.Value.Logistica}",
                Delta = envioAPagar, Source = "guardian-me1", DetectedAt = DateTime.UtcNow,
                Notes = Recortar($"MercadoLibre la pasó a me2 con envío gratis y NO acepta volver a me1 (suele ser porque la caja cargada entra en me2).{costo} Revisá precio o medidas."
                    + (putTxt.Length > 0 ? $" MeLi: {putTxt}" : ""), 500)
            });
            await db.SaveChangesAsync(ct);
            _logger.LogWarning("[Guardián me1] {Mla} MeLi la pasó a me2 y NO volvió a me1 (PUT {Code}) {Txt}",
                mla, (int)put.StatusCode, putTxt);
        }
    }

    private async Task<(string? Modo, string? Logistica, bool Gratis, string? Status)?> LeerEnvioAsync(HttpClient http, string mla, CancellationToken ct)
    {
        using var resp = await http.GetAsync($"https://api.mercadolibre.com/items/{mla}?attributes=status,shipping", ct);
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("[Guardián me1] {Mla}: no se pudo leer ({Code})", mla, (int)resp.StatusCode);
            return null;
        }
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        string? status = r.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        if (!r.TryGetProperty("shipping", out var sh) || sh.ValueKind != JsonValueKind.Object) return (null, null, false, status);
        string? S(string p) => sh.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var gratis = sh.TryGetProperty("free_shipping", out var fs) && fs.ValueKind == JsonValueKind.True;
        return (S("mode"), S("logistic_type"), gratis, status);
    }

    private static string Recortar(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
