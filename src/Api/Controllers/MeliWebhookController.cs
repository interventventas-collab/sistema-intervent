using System.Text.Json;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Endpoint publico que recibe webhooks de MercadoLibre. Lo llama MeLi cada vez que pasa
/// algo relevante (orden nueva, item modificado, question nueva, etc).
///
/// Requisitos de MeLi (importantes):
///   - Responder 200 OK en MENOS de 5 segundos (sino reintenta).
///   - No requiere firma — usamos user_id en el payload para validar contra MeliAccounts.
///   - Procesar duplicados (MeLi entrega "at least once").
///
/// Patron: el endpoint loggea + responde 200 instantaneamente, y dispara el procesamiento
/// real en Task.Run con scope nuevo (asi no bloqueamos el request HTTP).
/// </summary>
[ApiController]
[Route("api/meli/webhook")]
[AllowAnonymous]
public class MeliWebhookController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliWebhookController> _logger;

    public MeliWebhookController(AppDbContext db, IServiceScopeFactory scopeFactory,
        ILogger<MeliWebhookController> logger)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>POST principal — lo llama MeLi cada vez que hay un evento.</summary>
    [HttpPost("")]
    public async Task<IActionResult> Receive()
    {
        // Leer el body crudo (MeLi manda JSON simple).
        string raw;
        using (var reader = new StreamReader(Request.Body))
        {
            raw = await reader.ReadToEndAsync();
        }

        // Parsear lo que podamos. Si el JSON viene roto, igual respondemos 200 + logueamos
        // para no incentivar reintentos infinitos.
        string? topic = null, resource = null;
        long? userId = null;
        int? attempts = null;
        DateTime? sentAt = null;

        try
        {
            var doc = JsonDocument.Parse(raw).RootElement;
            if (doc.TryGetProperty("topic", out var t) && t.ValueKind == JsonValueKind.String)
                topic = t.GetString();
            if (doc.TryGetProperty("resource", out var r) && r.ValueKind == JsonValueKind.String)
                resource = r.GetString();
            if (doc.TryGetProperty("user_id", out var u) && u.ValueKind == JsonValueKind.Number)
                userId = u.GetInt64();
            if (doc.TryGetProperty("attempts", out var a) && a.ValueKind == JsonValueKind.Number)
                attempts = a.GetInt32();
            if (doc.TryGetProperty("sent", out var s) && s.ValueKind == JsonValueKind.String
                && DateTime.TryParse(s.GetString(), out var sdt))
                sentAt = sdt.ToUniversalTime();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MeLi webhook] JSON invalido: {Raw}", Trim(raw, 300));
        }

        // Guardar log de inmediato (para auditoria).
        var log = new MeliWebhookLog
        {
            Topic = topic,
            Resource = resource,
            UserId = userId,
            Attempts = attempts,
            SentAt = sentAt,
            ReceivedAt = DateTime.UtcNow,
            RawBody = Trim(raw, 4000)
        };
        _db.MeliWebhookLogs.Add(log);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[MeLi webhook] No pude guardar el log inicial");
        }

        var logId = log.Id;

        // Procesar en background (responder 200 ya). Usamos scope nuevo porque _db esta scoped al request.
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                var db = sp.GetRequiredService<AppDbContext>();
                var orderSvc = sp.GetRequiredService<MeliOrderService>();
                var stockSync = sp.GetRequiredService<MeliStockSyncService>();
                var stockPush = sp.GetRequiredService<MeliStockPushService>();
                var bgLogger = sp.GetRequiredService<ILogger<MeliWebhookController>>();

                var logEntity = await db.MeliWebhookLogs.FindAsync(logId);
                if (logEntity is null) return;

                string? processError = null;
                bool ok = false;
                try
                {
                    // Validacion: el user_id tiene que existir en MeliAccounts.
                    MeliAccount? account = null;
                    if (userId.HasValue)
                    {
                        account = await db.MeliAccounts.FirstOrDefaultAsync(a => a.MeliUserId == userId.Value);
                    }
                    if (account is null)
                    {
                        // No matchea ningun seller — ignorar (no es nuestro).
                        ok = true;
                        processError = $"user_id {userId} no matchea ninguna MeliAccount";
                    }
                    else
                    {
                        switch (topic)
                        {
                            case "orders_v2":
                            case "orders":
                                await HandleOrderWebhookAsync(db, orderSvc, stockSync, stockPush, account, resource, bgLogger);
                                ok = true;
                                break;
                            case "items":
                                // Re-sync del item para detectar cambios (precio / status) y dispararemos
                                // el detector que loguea en MeliCambiosDetectados.
                                if (!string.IsNullOrEmpty(resource))
                                {
                                    var rawId = resource.Replace("/items/", "").Trim();
                                    try
                                    {
                                        var itemSvc = sp.GetRequiredService<MeliItemService>();
                                        await itemSvc.SyncItemsByIdAsync(rawId);
                                        bgLogger.LogInformation("[MeLi webhook] items: {Resource} re-sincronizado (account={Acc})",
                                            resource, account.Nickname);
                                    }
                                    catch (Exception ex)
                                    {
                                        bgLogger.LogWarning(ex, "[MeLi webhook] items: error re-sincronizando {Resource}", resource);
                                    }
                                }
                                ok = true;
                                break;
                            case "shipments":
                                {
                                    // 2026-07-04: al toque de que el chofer marca entregado en la app oficial de
                                    // Flex (o cambia cualquier estado del envio), MeLi nos avisa por aca y
                                    // re-sincronizamos ese envio para reflejarlo sin esperar al refresh periodico.
                                    var shipmentSvc = sp.GetRequiredService<MeliShipmentService>();
                                    await HandleShipmentWebhookAsync(shipmentSvc, resource, bgLogger);
                                    ok = true;
                                    break;
                                }
                            default:
                                bgLogger.LogInformation("[MeLi webhook] topic ignorado: {Topic} {Resource}", topic, resource);
                                ok = true;
                                break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    processError = ex.Message;
                    bgLogger.LogError(ex, "[MeLi webhook] Error procesando {Topic} {Resource}", topic, resource);
                }

                logEntity.ProcessedAt = DateTime.UtcNow;
                // ok=true significa que TERMINAMOS de procesarlo correctamente, aunque incluya
                // skipped (ej user_id no matchea, topic ignorado). Solo es false si hubo excepcion.
                logEntity.ProcessedOk = ok;
                logEntity.ErrorMessage = processError;
                try { await db.SaveChangesAsync(); }
                catch (Exception ex) { bgLogger.LogWarning(ex, "[MeLi webhook] No pude actualizar log {Id}", logId); }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MeLi webhook] Error fatal en task background");
            }
        });

        // Responder rapido. MeLi corta a los 5s.
        return Ok(new { received = true });
    }

    /// <summary>Handler para topic orders_v2 / orders: extrae order_id, sincroniza, descuenta stock, pushea a MeLi.</summary>
    private static async Task HandleOrderWebhookAsync(
        AppDbContext db,
        MeliOrderService orderSvc,
        MeliStockSyncService stockSync,
        MeliStockPushService stockPush,
        MeliAccount account,
        string? resource,
        ILogger<MeliWebhookController> logger)
    {
        if (string.IsNullOrEmpty(resource))
        {
            logger.LogWarning("[MeLi webhook] orders sin resource");
            return;
        }

        // resource = "/orders/2000000000" → extraer el id final.
        var parts = resource.Trim('/').Split('/');
        if (parts.Length < 2 || !long.TryParse(parts[parts.Length - 1], out var orderId))
        {
            logger.LogWarning("[MeLi webhook] resource invalido: {Resource}", resource);
            return;
        }

        // Sincronizar la orden puntual.
        var n = await orderSvc.SyncSingleOrderAsync(orderId, account);
        logger.LogInformation("[MeLi webhook] orden {OrderId} sincronizada ({N} items)", orderId, n);

        // Descontar stock de pendientes (incluyendo la recien sincronizada).
        var stockResult = await stockSync.ProcessPendingAsync(maxBatch: 100);
        if (stockResult.Procesadas > 0)
        {
            logger.LogInformation("[MeLi webhook] stock descontado: {P} ordenes (cafe={C} otros={O} sinLink={S})",
                stockResult.Procesadas, stockResult.DescontadasCafe, stockResult.DescontadasOtros, stockResult.SinLinkear);
        }

        // Identificar productos cuyo stock cambio (los de los items de esta orden) y pushearlos a MeLi
        // para mantener consistencia (mostrar stock real ya descontado, no el viejo).
        // En el flujo normal MeLi ya descuenta solo cuando crea la orden — pero pushear de nuevo
        // garantiza que coincida con la realidad del sistema.
        var orderItems = await db.MeliOrders
            .Where(o => o.MeliOrderId == orderId)
            .Select(o => o.ItemId)
            .Distinct()
            .ToListAsync();

        if (orderItems.Count > 0)
        {
            try
            {
                var pushResult = await stockPush.PushStockForMeliItemsAsync(orderItems);
                if (pushResult.Procesadas > 0)
                {
                    logger.LogInformation("[MeLi webhook] push stock: {P} publicaciones (ok={O} skip={S} err={E})",
                        pushResult.Procesadas, pushResult.Ok, pushResult.Skipped, pushResult.Errores);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[MeLi webhook] push stock post-orden fallo (no fatal)");
            }
        }
    }

    /// <summary>Handler para topic shipments: extrae el shipment_id del resource y re-sincroniza ese envio.
    /// Es la base del "reparto en vivo": cuando el chofer marca entregado en la app oficial de Flex,
    /// MeLi dispara este webhook y actualizamos el MeliShipment local (status/substatus/DateDelivered)
    /// al instante, en vez de esperar la barrida periodica. Reusa SyncSingleShipmentAsync (aditivo).</summary>
    private static async Task HandleShipmentWebhookAsync(
        MeliShipmentService shipmentSvc,
        string? resource,
        ILogger<MeliWebhookController> logger)
    {
        if (string.IsNullOrEmpty(resource))
        {
            logger.LogWarning("[MeLi webhook] shipments sin resource");
            return;
        }

        // resource = "/shipments/47446540758" → extraer el id final.
        var parts = resource.Trim('/').Split('/');
        if (parts.Length < 2 || !long.TryParse(parts[parts.Length - 1], out var shipmentId))
        {
            logger.LogWarning("[MeLi webhook] shipments resource invalido: {Resource}", resource);
            return;
        }

        var ok = await shipmentSvc.SyncSingleShipmentAsync(shipmentId);
        logger.LogInformation("[MeLi webhook] shipment {ShipmentId} re-sincronizado (ok={Ok})", shipmentId, ok);
    }

    private static string Trim(string s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length > max ? s.Substring(0, max) : s);

    /// <summary>GET de prueba — cuando MeLi configura el callback URL hace un GET para verificar
    /// que es reachable. Tambien sirve para que un humano pruebe que el endpoint existe.</summary>
    [HttpGet("")]
    public IActionResult Health()
    {
        return Ok(new
        {
            service = "meli-webhook",
            status = "ok",
            now = DateTime.UtcNow,
            hint = "POST aqui los webhooks de MercadoLibre"
        });
    }

    /// <summary>Lista los ultimos N webhooks recibidos (auditoria, solo admin).</summary>
    [HttpGet("logs")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Logs([FromQuery] int limit = 50)
    {
        if (limit < 1) limit = 1;
        if (limit > 500) limit = 500;
        var logs = await _db.MeliWebhookLogs
            .OrderByDescending(l => l.ReceivedAt)
            .Take(limit)
            .Select(l => new
            {
                l.Id,
                l.Topic,
                l.Resource,
                l.UserId,
                l.Attempts,
                l.SentAt,
                l.ReceivedAt,
                l.ProcessedAt,
                l.ProcessedOk,
                l.ErrorMessage
            })
            .ToListAsync();
        return Ok(new { count = logs.Count, items = logs });
    }
}
