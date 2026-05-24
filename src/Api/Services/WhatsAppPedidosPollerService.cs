using Api.Data;
using Microsoft.EntityFrameworkCore;
using System.Net.Http.Json;

namespace Api.Services;

/// <summary>
/// Background service que polea WhatsApp Web (via Playwright) buscando mensajes nuevos
/// del vendedor configurado. Si encuentra un mensaje que empieza con el trigger (#PED),
/// lo procesa via WhatsAppPedidoService (parsea con IA + guarda).
///
/// Config en AppSettings:
///   - whatsapp.pedidos.vendedor_telefono: número del vendedor
///   - whatsapp.pedidos.trigger: palabra clave (default "#PED")
///   - whatsapp.pedidos.last_message_id: cursor del último mensaje leído (lo manejamos acá)
///   - whatsapp.pedidos.poll_enabled: "true" para activar el poll (default "false" hasta que el usuario lo prenda)
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

        var telefono = settings.TryGetValue("whatsapp.pedidos.vendedor_telefono", out var t) ? (t ?? "").Trim() : "";
        var trigger = settings.TryGetValue("whatsapp.pedidos.trigger", out var tr) ? (tr ?? "#PED").Trim() : "#PED";
        if (string.IsNullOrEmpty(telefono)) { _logger.LogDebug("[WA pedidos poll] sin telefono configurado"); return; }

        var lastIdKey = "whatsapp.pedidos.last_message_id";
        var lastId = settings.TryGetValue(lastIdKey, out var li) ? li ?? "" : "";

        // 2) Pedir mensajes a Playwright
        var playwrightUrl = Environment.GetEnvironmentVariable("PLAYWRIGHT_URL")
            ?? "http://playwright:3001";
        var http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMinutes(2);

        HttpResponseMessage resp;
        try
        {
            resp = await http.PostAsJsonAsync($"{playwrightUrl}/whatsapp/messages/list",
                new { phone = telefono, sinceId = lastId }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[WA pedidos poll] no se pudo conectar a Playwright");
            return;
        }

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("[WA pedidos poll] Playwright respondio {Code}: {Err}", (int)resp.StatusCode, err.Substring(0, Math.Min(200, err.Length)));
            return;
        }

        var data = await resp.Content.ReadFromJsonAsync<WaListResponse>(cancellationToken: ct);
        if (data?.Messages is null || data.Messages.Count == 0)
        {
            _logger.LogDebug("[WA pedidos poll] sin mensajes nuevos");
            return;
        }

        _logger.LogInformation("[WA pedidos poll] {N} mensajes nuevos", data.Messages.Count);

        // 3) Procesar cada mensaje: si es de OTRO (no fromMe) Y empieza con trigger, recibir como pedido
        var triggerUpper = trigger.ToUpperInvariant();
        WaMessage? ultimoProcesado = null;
        foreach (var m in data.Messages)
        {
            if (m.FromMe) { ultimoProcesado = m; continue; }
            var texto = (m.Text ?? "").Trim();
            if (string.IsNullOrEmpty(texto)) { ultimoProcesado = m; continue; }
            if (!texto.ToUpperInvariant().StartsWith(triggerUpper))
            {
                // Mensaje del vendedor pero sin trigger — lo ignoramos por ahora
                ultimoProcesado = m;
                continue;
            }
            try
            {
                await svc.RecibirPedidoAsync(telefono, texto, source: "whatsapp_auto", ct);
                _logger.LogInformation("[WA pedidos poll] pedido recibido: {Id}", m.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[WA pedidos poll] error procesando mensaje {Id}", m.Id);
            }
            ultimoProcesado = m;
        }

        // 4) Actualizar cursor con el último id procesado
        if (ultimoProcesado is not null)
        {
            var existing = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == lastIdKey, ct);
            if (existing is null)
                db.AppSettings.Add(new Models.AppSetting { Key = lastIdKey, Value = ultimoProcesado.Id, UpdatedAt = DateTime.UtcNow });
            else { existing.Value = ultimoProcesado.Id; existing.UpdatedAt = DateTime.UtcNow; }
            await db.SaveChangesAsync(ct);
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
