using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;

namespace Api.Services;

/// <summary>
/// Background service que polea WhatsApp Web (via Playwright) buscando mensajes nuevos
/// de los teléfonos AUTORIZADOS (tabla WhatsAppPedidosTelefonos). Si encuentra un mensaje
/// que empieza con el trigger (#PED), lo procesa via WhatsAppPedidoService.
///
/// Si la auto-respuesta está activada (whatsapp.pedidos.auto_responder_enabled), tras
/// guardar el pedido manda al remitente:
///   1) "✅ PEDIDO RECIBIDO EN SISTEMA"
///   2) El eco del texto del pedido
///
/// Config:
///   - Tabla WhatsAppPedidosTelefonos: lista de teléfonos autorizados con su cursor propio
///   - whatsapp.pedidos.trigger (AppSetting): palabra clave (default "#PED")
///   - whatsapp.pedidos.poll_enabled (AppSetting): "true" para activar el poll
///   - whatsapp.pedidos.auto_responder_enabled (AppSetting): "true" para mandar confirmación
/// </summary>
public class WhatsAppPedidosPollerService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<WhatsAppPedidosPollerService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);

    public WhatsAppPedidosPollerService(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory, ILogger<WhatsAppPedidosPollerService> logger)
    {
        _scopeFactory = scopeFactory; _httpFactory = httpFactory; _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { _logger.LogError(ex, "[WA pedidos poll] error en ciclo"); }
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<WhatsAppPedidoService>();

        // 1) Verificar config
        var settings = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("whatsapp.pedidos."))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        var pollEnabled = settings.TryGetValue("whatsapp.pedidos.poll_enabled", out var pe) && string.Equals(pe?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        if (!pollEnabled) return;

        var trigger = settings.TryGetValue("whatsapp.pedidos.trigger", out var tr) ? (tr ?? "#PED").Trim() : "#PED";
        var autoRespond = settings.TryGetValue("whatsapp.pedidos.auto_responder_enabled", out var ar) && string.Equals(ar?.Trim(), "true", StringComparison.OrdinalIgnoreCase);

        // 2) Cargar lista de teléfonos autorizados (solo activos)
        var telefonos = await db.WhatsAppPedidosTelefonos.Where(t => t.Activo).ToListAsync(ct);
        if (telefonos.Count == 0)
        {
            _logger.LogDebug("[WA pedidos poll] sin teléfonos autorizados");
            return;
        }

        var playwrightUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_URL") ?? "http://playwright:3001";
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);

        // 3) Iterar cada teléfono. Cada uno con su propio cursor.
        foreach (var tel in telefonos)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ProcesarTelefonoAsync(db, svc, http, playwrightUrl, tel, trigger, autoRespond, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WA pedidos poll] error procesando {Telefono}", tel.Telefono);
            }
        }
    }

    private async Task ProcesarTelefonoAsync(
        AppDbContext db, WhatsAppPedidoService svc, HttpClient http, string playwrightUrl,
        WhatsAppPedidosTelefono tel, string trigger, bool autoRespond, CancellationToken ct)
    {
        var sinceId = tel.LastMessageId ?? "";

        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync($"{playwrightUrl}/whatsapp/messages/list",
                new { phone = tel.Telefono, sinceId }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WA pedidos poll] {Tel}: no se pudo conectar a Playwright", tel.Telefono);
            return;
        }
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("[WA pedidos poll] {Tel}: Playwright respondió {Code}: {Err}", tel.Telefono, (int)resp.StatusCode, err.Substring(0, Math.Min(200, err.Length)));
            return;
        }

        var data = await resp.Content.ReadFromJsonAsync<WaListResponse>(cancellationToken: ct);
        if (data?.Messages is null || data.Messages.Count == 0) return;

        _logger.LogInformation("[WA pedidos poll] {Tel}: {N} mensajes nuevos", tel.Telefono, data.Messages.Count);

        var triggerUpper = trigger.ToUpperInvariant();
        WaMessage? ultimoProcesado = null;
        foreach (var m in data.Messages)
        {
            if (m.FromMe) { ultimoProcesado = m; continue; }
            var texto = (m.Text ?? "").Trim();
            if (string.IsNullOrEmpty(texto)) { ultimoProcesado = m; continue; }
            if (!texto.ToUpperInvariant().StartsWith(triggerUpper)) { ultimoProcesado = m; continue; }

            try
            {
                var pedido = await svc.RecibirPedidoAsync(tel.Telefono, texto, source: "whatsapp_auto", ct);
                _logger.LogInformation("[WA pedidos poll] pedido recibido #{PedidoId} de {Tel}", pedido.Id, tel.Telefono);

                // Auto-respuesta SINCRÓNICA — no usar Task.Run porque Playwright
                // tiene un solo browser y la navegación de send-bulk choca con la
                // del próximo messages/list (Execution context was destroyed).
                if (autoRespond)
                {
                    try { await EnviarAutoRespuestaAsync(http, playwrightUrl, tel.Telefono, pedido.Id, texto, ct); }
                    catch (Exception ex) { _logger.LogError(ex, "[WA pedidos poll] error auto-respuesta a {Tel}", tel.Telefono); }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WA pedidos poll] error procesando mensaje {Id}", m.Id);
            }
            ultimoProcesado = m;
        }

        if (ultimoProcesado is not null)
        {
            var entity = await db.WhatsAppPedidosTelefonos.FirstOrDefaultAsync(t => t.Id == tel.Id, ct);
            if (entity is not null)
            {
                entity.LastMessageId = ultimoProcesado.Id;
                entity.LastReadAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private async Task EnviarAutoRespuestaAsync(HttpClient http, string playwrightUrl, string telefono, int pedidoId, string textoOriginal, CancellationToken ct)
    {
        // Mensaje 1: confirmación
        var msg1 = $"✅ PEDIDO RECIBIDO EN SISTEMA (#{pedidoId})";
        // Mensaje 2: eco del pedido
        var msg2 = $"Detalle de tu pedido:\n\n{textoOriginal.Trim()}";

        await EnviarMensajeAsync(http, playwrightUrl, telefono, msg1, ct);
        await Task.Delay(1500, ct);
        await EnviarMensajeAsync(http, playwrightUrl, telefono, msg2, ct);
    }

    private async Task EnviarMensajeAsync(HttpClient http, string playwrightUrl, string telefono, string mensaje, CancellationToken ct)
    {
        try
        {
            var body = new { recipients = new[] { new { phone = telefono, message = mensaje } } };
            using var resp = await http.PostAsJsonAsync($"{playwrightUrl}/whatsapp/send-bulk", body, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("[WA pedidos auto-respuesta] Playwright {Code}: {Err}", (int)resp.StatusCode, err.Substring(0, Math.Min(200, err.Length)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WA pedidos auto-respuesta] error enviando a {Tel}", telefono);
        }
    }

    private class WaListResponse
    {
        public List<WaMessage>? Messages { get; set; }
        public int Total { get; set; }
        public string? Phone { get; set; }
    }
    private class WaMessage
    {
        public string Id { get; set; } = "";
        public string? Text { get; set; }
        public bool FromMe { get; set; }
        public string? Meta { get; set; }
    }
}
