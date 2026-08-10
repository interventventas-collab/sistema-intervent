using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class MeliOrderService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly AuditLogService _auditLog;

    public MeliOrderService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService, AuditLogService auditLog)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _auditLog = auditLog;
    }

    public async Task<MeliOrdersResponse> GetOrdersAsync(DateTime from, DateTime to, int? meliAccountId = null)
    {
        var query = _db.MeliOrders
            .Include(o => o.MeliAccount)
            .Where(o => o.DateCreated >= from && o.DateCreated <= to);

        if (meliAccountId.HasValue)
            query = query.Where(o => o.MeliAccountId == meliAccountId.Value);

        var total = await query.CountAsync();
        // BUG-FIX 2026-05-27: antes hacíamos GroupJoin+SelectMany contra MeliItems por
        // MeliItemId solo. Cuando el MLA tiene N variantes (color, talle), eso explotaba
        // 1 orden en N filas en el listado, dando la impresión de que se vendieron todas
        // las variantes (ver Pack 2000013199907009: 1 venta real pero la UI mostraba 5).
        // Fix: subquery correlada que prioriza match por VariationId si la orden tiene
        // variant. Devuelve UNA fila por orden.
        var orders = await query
            .OrderByDescending(o => o.DateCreated)
            .Select(o => new MeliOrderDto(
                o.Id, o.MeliOrderId, o.MeliAccountId,
                o.MeliAccount != null ? o.MeliAccount.Nickname : "Desconocida",
                o.Status, o.DateCreated, o.DateClosed,
                o.TotalAmount, o.CurrencyId,
                o.BuyerId, o.BuyerNickname,
                o.ItemId, o.ItemTitle, o.Quantity, o.UnitPrice, o.FullUnitPrice,
                o.ShippingId, o.PackId,
                // Thumbnail: priorizar variante exacta, fallback al primero del MeliItemId.
                _db.MeliItems
                    .Where(i => i.MeliItemId == o.ItemId
                        && (o.VariationId == null || i.VariationId == o.VariationId))
                    .Select(i => i.Thumbnail)
                    .FirstOrDefault()
                ?? _db.MeliItems
                    .Where(i => i.MeliItemId == o.ItemId)
                    .Select(i => i.Thumbnail)
                    .FirstOrDefault(),
                o.ShippingStatus,
                o.ShippingSubstatus,
                o.ShippingMode))
            .ToListAsync();

        return new MeliOrdersResponse(orders, total);
    }

    public async Task<MeliOrderSyncResult> SyncOrdersAsync(DateTime from, DateTime to)
    {
        var accounts = await _accountService.GetAllAccountEntitiesAsync();
        int totalSynced = 0;
        int totalErrors = 0;
        var errors = new List<string>();

        foreach (var account in accounts)
        {
            try
            {
                var token = await _accountService.GetValidTokenAsync(account);
                if (token is null)
                {
                    errors.Add($"Token expirado para {account.Nickname}");
                    totalErrors++;
                    continue;
                }

                var synced = await SyncOrdersForAccountAsync(account, token, from, to);
                totalSynced += synced;
            }
            catch (Exception ex)
            {
                errors.Add($"Error en {account.Nickname}: {ex.Message}");
                totalErrors++;
            }
        }

        // Audit log for sync - include full error details as JSON
        var auditData = new
        {
            resumen = $"Sincronizados {totalSynced} ordenes, {totalErrors} errores",
            totalSincronizados = totalSynced,
            totalErrores = totalErrors,
            errores = errors
        };
        var syncJson = System.Text.Json.JsonSerializer.Serialize(auditData);
        await _auditLog.LogAsync("Sync", "orders", "SYNC", syncJson);

        return new MeliOrderSyncResult(totalSynced, totalErrors, errors);
    }

    private async Task<int> SyncOrdersForAccountAsync(MeliAccount account, string token, DateTime from, DateTime to)
    {
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        int offset = 0;
        int limit = 50;
        int synced = 0;
        bool hasMore = true;

        var fromStr = from.ToString("yyyy-MM-ddTHH:mm:ss.000-00:00");
        var toStr = to.ToString("yyyy-MM-ddTHH:mm:ss.000-00:00");

        while (hasMore)
        {
            var url = $"https://api.mercadolibre.com/orders/search" +
                $"?seller={account.MeliUserId}" +
                $"&sort=date_desc" +
                $"&order.date_created.from={fromStr}" +
                $"&order.date_created.to={toStr}" +
                $"&offset={offset}&limit={limit}";

            var response = await http.GetAsync(url);

            // Si devuelve 401/403, refrescar token y reintentar
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                var newToken = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
                if (newToken is not null)
                {
                    token = newToken;
                    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    response = await http.GetAsync(url);
                }
            }

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new Exception($"MeLi API error ({response.StatusCode}): {errorBody}");
            }

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json).RootElement;

            var results = doc.GetProperty("results");
            var paging = doc.GetProperty("paging");
            var total = paging.GetProperty("total").GetInt32();

            foreach (var order in results.EnumerateArray())
            {
                long? packId = order.TryGetProperty("pack_id", out var pid)
                    && pid.ValueKind != JsonValueKind.Null
                    ? pid.GetInt64() : null;
                synced += await UpsertOrderAsync(account.Id, order, packId, http);
            }

            await _db.SaveChangesAsync();

            offset += limit;
            hasMore = offset < total;
        }

        return synced;
    }

    /// <summary>
    /// Sincroniza UNA sola orden desde MeLi por id. Usado por el webhook handler:
    /// MeLi nos avisa "se creó/cambió la orden 2000000000" → fetcheamos esa orden puntual
    /// y la upserteamos (en vez de pedir lista de ordenes de las ultimas 6h).
    ///
    /// El upsert es por (MeliOrderId, ItemId) — si la orden tiene N items, escribe N filas.
    /// Returns: cantidad de filas afectadas (items en la orden).
    /// </summary>
    public async Task<int> SyncSingleOrderAsync(long meliOrderId, MeliAccount account)
    {
        var token = await _accountService.GetValidTokenAsync(account);
        if (token is null)
            throw new InvalidOperationException($"Token invalido para cuenta {account.Nickname}");

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.GetAsync($"https://api.mercadolibre.com/orders/{meliOrderId}");
        // Refresh + retry si 401/403
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var newToken = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
            if (newToken is not null)
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                response = await http.GetAsync($"https://api.mercadolibre.com/orders/{meliOrderId}");
            }
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            throw new Exception($"MeLi /orders/{meliOrderId} error ({response.StatusCode}): {errorBody}");
        }

        var json = await response.Content.ReadAsStringAsync();
        var order = JsonDocument.Parse(json).RootElement;

        long? packId = order.TryGetProperty("pack_id", out var pid)
            && pid.ValueKind != JsonValueKind.Null
            ? pid.GetInt64() : null;

        var n = await UpsertOrderAsync(account.Id, order, packId, http);
        await _db.SaveChangesAsync();
        return n;
    }

    /// <summary>Sincroniza TODAS las órdenes de un PACK (paquete/carrito) desde MeLi.
    /// Importante: el número que ve el vendedor en su panel muchas veces es el pack_id, NO el
    /// order_id → /orders/{packId} da 404, pero /packs/{packId} lista las órdenes reales.
    /// Devuelve cuántas órdenes sincronizó (0 si el pack no existe / no es un pack).</summary>
    public async Task<int> SyncPackAsync(long packId, MeliAccount account)
    {
        var token = await _accountService.GetValidTokenAsync(account);
        if (token is null)
            throw new InvalidOperationException($"Token invalido para cuenta {account.Nickname}");

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await http.GetAsync($"https://api.mercadolibre.com/packs/{packId}");
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var newToken = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
            if (newToken is not null)
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                response = await http.GetAsync($"https://api.mercadolibre.com/packs/{packId}");
            }
        }
        if (!response.IsSuccessStatusCode) return 0;

        var json = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(json).RootElement;
        if (!root.TryGetProperty("orders", out var ordersEl) || ordersEl.ValueKind != JsonValueKind.Array)
            return 0;

        int total = 0;
        foreach (var o in ordersEl.EnumerateArray())
        {
            if (o.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var oid))
            {
                try { total += await SyncSingleOrderAsync(oid, account); } catch { }
            }
        }
        return total;
    }

    /// <summary>Un mensaje interno (post-venta) de una venta de MeLi.</summary>
    public record MeliPackMessage(long FromUserId, string Text, DateTime? Date);

    /// <summary>Trae EN VIVO los mensajes internos (post-venta) de una venta desde MeLi.
    /// packId puede ser el pack_id o, para ventas sin pack, el order_id. Best-effort: si MeLi
    /// no deja leerlos (permisos / sin conversación), devuelve lista vacía.</summary>
    public async Task<List<MeliPackMessage>> GetPackMessagesAsync(long packId, MeliAccount account)
    {
        var result = new List<MeliPackMessage>();
        var token = await _accountService.GetValidTokenAsync(account);
        if (token is null) return result;

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var url = $"https://api.mercadolibre.com/messages/packs/{packId}/sellers/{account.MeliUserId}?tag=post_sale&mark_as_read=false";
        var response = await http.GetAsync(url);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
            response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            var newToken = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
            if (newToken is not null)
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                response = await http.GetAsync(url);
            }
        }
        if (!response.IsSuccessStatusCode) return result;

        var json = await response.Content.ReadAsStringAsync();
        var root = JsonDocument.Parse(json).RootElement;
        if (!root.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var m in msgs.EnumerateArray())
        {
            long fromId = 0;
            if (m.TryGetProperty("from", out var from) && from.TryGetProperty("user_id", out var uid) && uid.TryGetInt64(out var f))
                fromId = f;
            string? text = m.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            DateTime? date = null;
            if (m.TryGetProperty("message_date", out var md) && md.TryGetProperty("created", out var cr)
                && cr.ValueKind == JsonValueKind.String && DateTime.TryParse(cr.GetString(), out var d))
                date = d;
            if (!string.IsNullOrWhiteSpace(text))
                result.Add(new MeliPackMessage(fromId, text!, date));
        }
        return result;
    }

    // ═══════════════════ POST-VENTA CAFÉ: mensaje automático de molienda ═══════════════════
    // Cuando entra una venta de una publicación de café ELEGIDA por el usuario, el sistema le
    // manda solo un mensaje al comprador (por la mensajería post-venta de MeLi) preguntándole
    // en qué formato/molienda quiere el café. Config en AppSettings (claves cafe.postventa.*).
    // Disparo real-time desde el webhook de órdenes + barrido de respaldo en el job de órdenes.

    public const string CfgPostventaEnabled = "cafe.postventa.enabled";
    public const string CfgPostventaMensaje = "cafe.postventa.mensaje";    // legacy (saludo único)
    public const string CfgPostventaMensajes = "cafe.postventa.mensajes";  // lista de saludos que rotan (JSON)
    public const string CfgPostventaOpciones = "cafe.postventa.opciones";
    public const string CfgPostventaItems = "cafe.postventa.items";
    // Si está en "true" (default), el mensaje de molienda SOLO se manda cuando el café es MOLIDO.
    // A quien compra grano entero no se le pregunta la molienda (no tiene sentido). Reversible
    // desde AppSettings poniendo el valor en "false".
    public const string CfgPostventaSoloMolido = "cafe.postventa.solo_molido";

    /// <summary>Saludos por defecto: se elige uno AL AZAR en cada venta para que no suene repetitivo
    /// (y MeLi no lo lea como spam). Debajo va siempre la lista de moliendas.</summary>
    public static readonly string[] PostventaMensajesDefault =
    {
        "Hola, ¿qué tal? Recibimos tu compra. ¿Podrías confirmarnos en qué formato lo deseás?",
        "¡Hola! ¿Cómo estás? Gracias por tu compra ☕ ¿Cómo preferís la molienda?",
        "Buenas 👋 Recibimos tu pedido. Contanos en qué formato lo preparamos:",
        "¡Hola! Gracias por elegirnos 🙌 ¿En qué formato querés el café?"
    };

    public static readonly string[] PostventaOpcionesDefault =
    {
        "En granos", "Molido filtro", "Molido express", "Molido moka", "Molido bodum",
        "Molido prensa francesa", "Molido cafetera italiana", "Molido a la turca", "Mini express"
    };

    public record ConfigPostventaCafe(bool Enabled, List<string> Mensajes, List<string> Opciones, List<string> Items, bool SoloMolido);

    /// <summary>Lee la config del respondedor post-venta de café desde AppSettings, con defaults.</summary>
    public async Task<ConfigPostventaCafe> LeerConfigPostventaAsync()
    {
        var vals = await _db.AppSettings
            .Where(s => s.Key.StartsWith("cafe.postventa."))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        bool enabled = vals.TryGetValue(CfgPostventaEnabled, out var e) && e == "true";

        // Saludos: la lista nueva (JSON) que rota al azar. Si nunca se guardó una lista, se muestran
        // los 4 saludos de ejemplo por defecto (el saludo único viejo, si existía, queda ignorado).
        var mensajes = new List<string>();
        if (vals.TryGetValue(CfgPostventaMensajes, out var ms) && !string.IsNullOrWhiteSpace(ms))
        {
            try { mensajes = JsonSerializer.Deserialize<List<string>>(ms) ?? new(); } catch { }
        }
        mensajes = mensajes.Select(x => (x ?? "").Trim()).Where(x => x.Length > 0).ToList();
        if (mensajes.Count == 0) mensajes = PostventaMensajesDefault.ToList();

        var opciones = vals.TryGetValue(CfgPostventaOpciones, out var op) && !string.IsNullOrWhiteSpace(op)
            ? op.Replace("\r", "").Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList()
            : PostventaOpcionesDefault.ToList();
        var items = new List<string>();
        if (vals.TryGetValue(CfgPostventaItems, out var it) && !string.IsNullOrWhiteSpace(it))
        {
            try { items = JsonSerializer.Deserialize<List<string>>(it) ?? new(); } catch { }
        }
        // "Solo molido" viene activado por defecto: solo se avisa si el café es molido.
        bool soloMolido = !vals.TryGetValue(CfgPostventaSoloMolido, out var sm) || sm != "false";
        return new ConfigPostventaCafe(enabled, mensajes, opciones, items, soloMolido);
    }

    // ─────────────── Detección de molienda (grano entero vs molido) ───────────────
    // El café se vende como publicaciones separadas por molienda; la molienda queda escrita en el
    // TÍTULO (o en los atributos de la variación, cuando la venta trae variación). No hay un dato
    // estructurado confiable, así que la deducimos por palabras clave.

    public enum MoliendaTipo { Molido, Granos, Desconocido }

    // Palabras que delatan que el café va MOLIDO (por el método de preparación).
    // OJO: "italiano"/"italiana" NO va acá: es un TIPO DE TUESTE (Tostado Italiano), no una molienda,
    // y aparece también en publicaciones de grano entero.
    private static readonly string[] _kwMolido =
    {
        "molido", "molida", "filtro", "melita", "goteo",
        "express", "expres", "expresso", "espresso", "expreso", "espreso",
        "moka", "moca", "napolitana", "prensa", "embolo", "émbolo",
        "bodum", "capsula", "cápsula", "turca", "aeropress", "chemex", "v60", "cafetera"
    };
    // Palabras de grano ENTERO.
    private static readonly string[] _kwGrano = { "grano", "granos" };

    /// <summary>Clasifica por la VARIANTE que eligió el comprador (el dato más confiable).
    /// Acá GANA el grano: algunas variantes vienen escritas "Molido: En Grano" — donde "Molido" es
    /// el nombre del selector y "En Grano" es lo que realmente eligió — así que si aparece "grano"
    /// es grano entero, aunque el texto también diga "molido".</summary>
    public static MoliendaTipo ClasificarPorVariante(string? variationAttrs)
    {
        var t = (variationAttrs ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(t)) return MoliendaTipo.Desconocido;
        if (t.Contains("grano")) return MoliendaTipo.Granos;
        foreach (var k in _kwMolido) if (t.Contains(k)) return MoliendaTipo.Molido;
        return MoliendaTipo.Desconocido;
    }

    /// <summary>Clasifica por el TÍTULO de la publicación (cuando la venta no trae variante porque
    /// cada molienda es una publicación aparte, agrupadas por familia en MeLi). Acá GANA el molido:
    /// muchas publis de molido llevan igual "en grano" en el nombre del producto
    /// (ej "Café En Grano Premium ... Molido Filtro"), por eso primero buscamos señales de molido.</summary>
    public static MoliendaTipo ClasificarPorTitulo(string? titulo)
    {
        var t = (titulo ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(t)) return MoliendaTipo.Desconocido;
        foreach (var k in _kwMolido) if (t.Contains(k)) return MoliendaTipo.Molido;
        if (t.Contains("grano")) return MoliendaTipo.Granos;
        return MoliendaTipo.Desconocido;
    }

    /// <summary>Decide la molienda de UNA fila de venta: primero la variante elegida por el comprador
    /// (si vino y es concluyente), y si no, cae al título de la publicación.</summary>
    private async Task<MoliendaTipo> MoliendaDeOrdenAsync(MeliOrder o, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(o.VariationId))
        {
            var attrs = await _db.MeliItems
                .Where(i => i.MeliItemId == o.ItemId && i.VariationId == o.VariationId)
                .Select(i => i.VariationAttributes).FirstOrDefaultAsync(ct);
            var porVar = ClasificarPorVariante(attrs);
            if (porVar != MoliendaTipo.Desconocido) return porVar;
        }
        return ClasificarPorTitulo(o.ItemTitle);
    }

    /// <summary>Devuelve true si TODAS las filas de café elegidas de esta venta son grano entero
    /// (y por lo tanto NO hay que preguntar la molienda). Si hay al menos una molida o desconocida,
    /// devuelve false (mejor preguntar ante la duda). Respeta la config: si "solo molido" está
    /// apagado, nunca filtra.</summary>
    private async Task<bool> EsSoloGranoAsync(List<MeliOrder> rows, ConfigPostventaCafe cfg, CancellationToken ct)
    {
        if (!cfg.SoloMolido) return false;
        var relevantes = rows.Where(o => cfg.Items.Contains(o.ItemId)).ToList();
        if (relevantes.Count == 0) return false;
        foreach (var o in relevantes)
            if (await MoliendaDeOrdenAsync(o, ct) != MoliendaTipo.Granos) return false;
        return true; // todas grano entero
    }

    /// <summary>Elige un saludo al azar de la lista (o vacío si no hay ninguno).</summary>
    public static string ElegirSaludo(List<string> mensajes)
    {
        var lista = (mensajes ?? new List<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (lista.Count == 0) return "";
        return lista[Random.Shared.Next(lista.Count)];
    }

    /// <summary>Arma el texto final que ve el comprador: saludo + lista de opciones + cierre.</summary>
    public static string ComponerMensajePostventa(string mensaje, List<string> opciones)
    {
        var sb = new StringBuilder();
        sb.Append((mensaje ?? "").Trim());
        var ops = (opciones ?? new List<string>()).Where(o => !string.IsNullOrWhiteSpace(o)).ToList();
        if (ops.Count > 0)
        {
            sb.Append("\n");
            foreach (var o in ops) sb.Append("\n• ").Append(o.Trim());
            sb.Append("\n\nRespondé con la opción que prefieras 🙂");
        }
        return sb.ToString().Trim();
    }

    /// <summary>Envía un mensaje post-venta al comprador iniciando la conversación por la vía que
    /// MeLi permite: "Motivos para comunicarse" (action_guide) con el motivo OTHER (texto libre).
    /// MeLi bloquea el texto libre directo cuando lo inicia el vendedor. Devuelve (ok, error).
    /// packId puede ser el pack_id o, para ventas sin pack, el order_id.</summary>
    public async Task<(bool ok, string? error)> SendPackMessageAsync(long packId, MeliAccount account, long buyerId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return (false, "El mensaje está vacío.");
        var token = await _accountService.GetValidTokenAsync(account);
        if (token is null) return (false, "No se pudo obtener el token de MercadoLibre.");

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Vía correcta para que el VENDEDOR inicie: motivo OTHER (texto libre) del action_guide.
        var url = $"https://api.mercadolibre.com/messages/action_guide/packs/{packId}/option?tag=post_sale";
        var payload = new { option_id = "OTHER", text = text };

        StringContent NuevoContent() => new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var resp = await http.PostAsync(url, NuevoContent());
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            var newToken = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
            if (newToken is not null)
            {
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", newToken);
                resp = await http.PostAsync(url, NuevoContent());
            }
        }
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            return (false, TraducirErrorMeli((int)resp.StatusCode, body));
        }
        return (true, null);
    }

    /// <summary>Convierte los errores técnicos de MeLi en un mensaje claro para el usuario.</summary>
    private static string TraducirErrorMeli(int status, string body)
    {
        var b = (body ?? "").ToLowerInvariant();
        if (b.Contains("cap") || b.Contains("limit") || b.Contains("blocked_by_conversation"))
            return "MercadoLibre no permite iniciar (o mandar más) mensajes en esta venta por ahora. " +
                   "Suele pasar en ventas ya entregadas o cuando ya se usó el cupo del vendedor: hay que esperar a que el comprador responda.";
        if (b.Contains("moderat"))
            return "MercadoLibre moderó el mensaje (algo del texto no lo deja pasar). Probá acortarlo o sacar links/números.";
        if (b.Contains("max") && b.Contains("length") || b.Contains("350"))
            return "El mensaje es demasiado largo para MercadoLibre. Acortá el texto o las opciones (tope ~350 caracteres).";
        return $"MercadoLibre rechazó el mensaje ({status}).";
    }

    /// <summary>Real-time (desde el webhook): si esta orden es de una publicación elegida y todavía
    /// no se avisó, manda el mensaje de molienda al comprador y marca la orden. Best-effort: si algo
    /// falla no marca, y el barrido de respaldo lo reintenta más tarde.</summary>
    public async Task EnviarPostventaCafeParaOrdenAsync(long meliOrderId, CancellationToken ct = default)
    {
        var cfg = await LeerConfigPostventaAsync();
        if (!cfg.Enabled || cfg.Items.Count == 0) return;

        var rows = await _db.MeliOrders.Include(o => o.MeliAccount)
            .Where(o => o.MeliOrderId == meliOrderId).ToListAsync(ct);
        if (rows.Count == 0 || rows.All(o => o.PostventaCafeMsgSent)) return;
        if (!rows.Any(o => cfg.Items.Contains(o.ItemId))) return;

        var any = rows[0];
        if (any.Status != "paid" && any.Status != "confirmed") return; // esperamos a que esté paga
        if (any.MeliAccount is null) return;

        // Solo molido: si es café en grano entero no preguntamos la molienda. Marcamos como
        // resuelto (sin fecha de envío) para que el barrido de respaldo no lo reintente.
        if (await EsSoloGranoAsync(rows, cfg, ct))
        {
            foreach (var o in rows) o.PostventaCafeMsgSent = true;
            await _db.SaveChangesAsync(ct);
            return;
        }

        long packId = any.PackId ?? any.MeliOrderId;
        var text = ComponerMensajePostventa(ElegirSaludo(cfg.Mensajes), cfg.Opciones);
        var (ok, _) = await SendPackMessageAsync(packId, any.MeliAccount, any.BuyerId, text);
        if (!ok) return;

        var ahora = DateTime.UtcNow;
        foreach (var o in rows) { o.PostventaCafeMsgSent = true; o.PostventaCafeMsgSentAt = ahora; }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>Barrido de respaldo (corre en el job de órdenes): agarra las ventas de café elegidas
    /// que quedaron sin avisar (ej. el webhook falló, o la orden se pagó después) y les manda el
    /// mensaje. Anti-backlog: lo más viejo que 48h se marca como enviado sin mandar nada.</summary>
    public async Task<int> EnviarPostventaCafePendientesAsync(CancellationToken ct = default)
    {
        var cfg = await LeerConfigPostventaAsync();
        if (!cfg.Enabled || cfg.Items.Count == 0) return 0;

        var corte = DateTime.UtcNow.AddHours(-48);

        var viejas = await _db.MeliOrders
            .Where(o => !o.PostventaCafeMsgSent && o.DateCreated < corte && cfg.Items.Contains(o.ItemId))
            .ToListAsync(ct);
        if (viejas.Count > 0)
        {
            foreach (var o in viejas) o.PostventaCafeMsgSent = true;
            await _db.SaveChangesAsync(ct);
        }

        var pendientes = await _db.MeliOrders.Include(o => o.MeliAccount)
            .Where(o => !o.PostventaCafeMsgSent && o.DateCreated >= corte
                && cfg.Items.Contains(o.ItemId)
                && (o.Status == "paid" || o.Status == "confirmed"))
            .OrderBy(o => o.DateCreated)
            .ToListAsync(ct);
        if (pendientes.Count == 0) return 0;

        int enviados = 0;
        bool huboCambios = false;
        foreach (var grupo in pendientes.GroupBy(o => o.MeliOrderId))
        {
            if (enviados >= 20) break; // el resto queda para la próxima vuelta
            var lista = grupo.ToList();
            var any = lista[0];
            if (any.MeliAccount is null) continue;

            // Solo molido: el grano entero se marca como resuelto sin mandar nada.
            if (await EsSoloGranoAsync(lista, cfg, ct))
            {
                foreach (var o in lista) o.PostventaCafeMsgSent = true;
                huboCambios = true;
                continue;
            }

            long packId = any.PackId ?? any.MeliOrderId;
            var text = ComponerMensajePostventa(ElegirSaludo(cfg.Mensajes), cfg.Opciones); // saludo al azar por venta
            var (ok, _) = await SendPackMessageAsync(packId, any.MeliAccount, any.BuyerId, text);
            if (!ok) continue; // no marcamos: reintenta la próxima
            var ahora = DateTime.UtcNow;
            foreach (var o in lista) { o.PostventaCafeMsgSent = true; o.PostventaCafeMsgSentAt = ahora; }
            enviados++;
            huboCambios = true;
        }
        if (huboCambios) await _db.SaveChangesAsync(ct);
        return enviados;
    }

    /// <summary>2026-06-15: Re-chequea contra MeLi todas las órdenes paid en estado pre-despacho
    /// (etiqueta no impresa / no retiradas) de los últimos N días para refrescar el ShippingSubstatus.
    /// Sin esto las órdenes quedan "congeladas" en ready_to_print aunque ya estén impresas en MeLi,
    /// lo cual rompe el cálculo de stock reservado. Llamar después del sync regular cada 30 min.</summary>
    public async Task<int> RefreshPendingOrdersAsync(int dias = 7)
    {
        // 2026-06-15: solo ready_to_print — lo único que MeLi considera "etiqueta a imprimir"
        var subEstadosPreDespacho = new[] { "ready_to_print" };
        var desde = DateTime.UtcNow.AddDays(-Math.Max(1, dias));

        // Cargar órdenes pendientes con sus cuentas (las que el cálculo de reserva considera "reservadas")
        var pendientes = await _db.MeliOrders
            .Where(o => o.Status == "paid"
                     && o.DateCreated >= desde
                     && o.LogisticType != "fulfillment"
                     && o.ShippingSubstatus != null
                     && subEstadosPreDespacho.Contains(o.ShippingSubstatus))
            .Select(o => new { o.MeliOrderId, o.MeliAccountId })
            .ToListAsync();

        if (pendientes.Count == 0) return 0;

        // Agrupar por cuenta para reusar token + http client
        var porCuenta = pendientes.GroupBy(p => p.MeliAccountId);
        int refrescadas = 0;

        foreach (var grupo in porCuenta)
        {
            var account = await _db.MeliAccounts.FirstOrDefaultAsync(a => a.Id == grupo.Key);
            if (account is null) continue;
            foreach (var p in grupo)
            {
                try
                {
                    await SyncSingleOrderAsync(p.MeliOrderId, account);
                    refrescadas++;
                }
                catch
                {
                    // si una orden falla (ej. 404), seguir con la siguiente
                }
            }
        }

        await _auditLog.LogAsync("Sync", "orders", "REFRESH_PENDING",
            System.Text.Json.JsonSerializer.Serialize(new { dias, candidatas = pendientes.Count, refrescadas }));
        return refrescadas;
    }

    public async Task<MeliOrderDetailResponse> GetOrderDetailAsync(long meliOrderId)
    {
        // Find the order in our DB to get the account
        var dbOrder = await _db.MeliOrders
            .Include(o => o.MeliAccount)
            .FirstOrDefaultAsync(o => o.MeliOrderId == meliOrderId);

        if (dbOrder is null)
            throw new Exception("Orden no encontrada");

        var token = await _accountService.GetValidTokenAsync(dbOrder.MeliAccount!);
        if (token is null)
        {
            // Forzar refresh si el token normal falla
            token = await _accountService.GetValidTokenAsync(dbOrder.MeliAccount!, forceRefresh: true);
            if (token is null)
                throw new Exception("Token expirado para la cuenta. Reconecta la cuenta de MercadoLibre.");
        }

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // If part of a pack, find all order IDs in this pack
        var orderIds = new List<long> { meliOrderId };
        if (dbOrder.PackId.HasValue)
        {
            var packOrderIds = await _db.MeliOrders
                .Where(o => o.PackId == dbOrder.PackId.Value)
                .Select(o => o.MeliOrderId)
                .Distinct()
                .ToListAsync();
            orderIds = packOrderIds;
        }

        var result = new MeliOrderDetailResponse();

        foreach (var orderId in orderIds)
        {
            var detail = await FetchOrderDetailFromMeli(http, orderId);
            if (detail is not null)
                result.Orders.Add(detail);
        }

        return result;
    }

    public async Task<MeliOrderDetailResponse> GetPackDetailAsync(long packId)
    {
        // Find all orders in this pack
        var dbOrders = await _db.MeliOrders
            .Include(o => o.MeliAccount)
            .Where(o => o.PackId == packId)
            .ToListAsync();

        if (!dbOrders.Any())
            throw new Exception("Pack no encontrado");

        var account = dbOrders.First().MeliAccount!;
        var token = await _accountService.GetValidTokenAsync(account);
        if (token is null)
        {
            token = await _accountService.GetValidTokenAsync(account, forceRefresh: true);
            if (token is null)
                throw new Exception("Token expirado para la cuenta. Reconecta la cuenta de MercadoLibre.");
        }

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var uniqueOrderIds = dbOrders.Select(o => o.MeliOrderId).Distinct().ToList();
        var result = new MeliOrderDetailResponse();

        foreach (var orderId in uniqueOrderIds)
        {
            var detail = await FetchOrderDetailFromMeli(http, orderId);
            if (detail is not null)
                result.Orders.Add(detail);
        }

        return result;
    }

    private async Task<MeliOrderDetailDto?> FetchOrderDetailFromMeli(HttpClient http, long meliOrderId)
    {
        var response = await http.GetAsync($"https://api.mercadolibre.com/orders/{meliOrderId}");
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync();
        var order = JsonDocument.Parse(json).RootElement;

        var detail = new MeliOrderDetailDto
        {
            MeliOrderId = meliOrderId,
            Status = order.GetProperty("status").GetString() ?? "unknown",
            DateCreated = order.GetProperty("date_created").GetDateTime(),
            DateClosed = order.TryGetProperty("date_closed", out var dc) && dc.ValueKind != JsonValueKind.Null
                ? dc.GetDateTime() : null,
            TotalAmount = order.GetProperty("total_amount").GetDecimal(),
            CurrencyId = order.GetProperty("currency_id").GetString() ?? "ARS",
            PackId = order.TryGetProperty("pack_id", out var pid) && pid.ValueKind != JsonValueKind.Null
                ? pid.GetInt64() : null,
            BuyerId = order.GetProperty("buyer").GetProperty("id").GetInt64(),
            BuyerNickname = order.GetProperty("buyer").GetProperty("nickname").GetString() ?? "",
            BuyerFirstName = order.GetProperty("buyer").TryGetProperty("first_name", out var fn) && fn.ValueKind != JsonValueKind.Null
                ? fn.GetString() : null,
            BuyerLastName = order.GetProperty("buyer").TryGetProperty("last_name", out var ln) && ln.ValueKind != JsonValueKind.Null
                ? ln.GetString() : null,
        };

        // Parse taxes at order level
        if (order.TryGetProperty("taxes", out var taxes) && taxes.ValueKind != JsonValueKind.Null)
        {
            if (taxes.TryGetProperty("amount", out var taxAmt) && taxAmt.ValueKind != JsonValueKind.Null)
                detail.TaxesAmount = taxAmt.GetDecimal();
        }

        // Parse items
        decimal totalSaleFee = 0;
        var itemIds = new List<string>();
        foreach (var item in order.GetProperty("order_items").EnumerateArray())
        {
            var itemId = item.GetProperty("item").GetProperty("id").GetString() ?? "";
            var saleFee = item.TryGetProperty("sale_fee", out var sf) && sf.ValueKind != JsonValueKind.Null
                ? sf.GetDecimal() : (decimal?)null;
            if (saleFee.HasValue) totalSaleFee += saleFee.Value;

            detail.Items.Add(new MeliOrderItemDetail
            {
                ItemId = itemId,
                Title = item.GetProperty("item").GetProperty("title").GetString() ?? "",
                Quantity = item.GetProperty("quantity").GetInt32(),
                UnitPrice = item.GetProperty("unit_price").GetDecimal(),
                SaleFee = saleFee
            });
            itemIds.Add(itemId);
        }
        detail.TotalSaleFee = totalSaleFee;

        // Fetch thumbnails for items (batch)
        if (itemIds.Any())
        {
            try
            {
                var idsParam = string.Join(",", itemIds);
                var itemsResponse = await http.GetAsync($"https://api.mercadolibre.com/items?ids={idsParam}");
                if (itemsResponse.IsSuccessStatusCode)
                {
                    var itemsJson = await itemsResponse.Content.ReadAsStringAsync();
                    var itemsDoc = JsonDocument.Parse(itemsJson).RootElement;
                    foreach (var itemResult in itemsDoc.EnumerateArray())
                    {
                        if (itemResult.TryGetProperty("body", out var body) && body.ValueKind != JsonValueKind.Null)
                        {
                            var id = body.GetProperty("id").GetString() ?? "";
                            var thumb = body.TryGetProperty("thumbnail", out var th) && th.ValueKind != JsonValueKind.Null
                                ? th.GetString() : null;
                            // Replace http with https for thumbnails
                            if (thumb != null && thumb.StartsWith("http://"))
                                thumb = "https://" + thumb[7..];
                            var detailItem = detail.Items.FirstOrDefault(i => i.ItemId == id);
                            if (detailItem is not null)
                                detailItem.ThumbnailUrl = thumb;
                        }
                    }
                }
            }
            catch { /* thumbnails are optional, don't fail the whole request */ }
        }

        // Parse payments
        decimal totalShippingCost = 0;
        if (order.TryGetProperty("payments", out var payments) && payments.ValueKind != JsonValueKind.Null)
        {
            foreach (var payment in payments.EnumerateArray())
            {
                var shCost = payment.TryGetProperty("shipping_cost", out var sc) && sc.ValueKind != JsonValueKind.Null
                    ? sc.GetDecimal() : (decimal?)null;
                if (shCost.HasValue) totalShippingCost += shCost.Value;

                detail.Payments.Add(new MeliPaymentDetail
                {
                    PaymentId = payment.GetProperty("id").GetInt64(),
                    Status = payment.GetProperty("status").GetString() ?? "",
                    PaymentType = payment.TryGetProperty("payment_type", out var pt) && pt.ValueKind != JsonValueKind.Null
                        ? pt.GetString() : null,
                    PaymentMethodId = payment.TryGetProperty("payment_method_id", out var pm) && pm.ValueKind != JsonValueKind.Null
                        ? pm.GetString() : null,
                    TransactionAmount = payment.TryGetProperty("transaction_amount", out var ta) && ta.ValueKind != JsonValueKind.Null
                        ? ta.GetDecimal() : 0,
                    TotalPaidAmount = payment.TryGetProperty("total_paid_amount", out var tpa) && tpa.ValueKind != JsonValueKind.Null
                        ? tpa.GetDecimal() : 0,
                    ShippingCost = shCost,
                    TaxesAmount = payment.TryGetProperty("taxes_amount", out var taxA) && taxA.ValueKind != JsonValueKind.Null
                        ? taxA.GetDecimal() : null,
                    DateApproved = payment.TryGetProperty("date_approved", out var da) && da.ValueKind != JsonValueKind.Null
                        ? da.GetDateTime() : null,
                    Installments = payment.TryGetProperty("installments", out var inst) && inst.ValueKind != JsonValueKind.Null
                        ? inst.GetInt32() : null
                });
            }
        }
        detail.ShippingCost = totalShippingCost;

        return detail;
    }

    private async Task<int> UpsertOrderAsync(int accountId, JsonElement order, long? packId, HttpClient http)
    {
        var meliOrderId = order.GetProperty("id").GetInt64();
        var status = order.GetProperty("status").GetString() ?? "unknown";
        var dateCreated = order.GetProperty("date_created").GetDateTime();
        var dateClosed = order.TryGetProperty("date_closed", out var dc)
            && dc.ValueKind != JsonValueKind.Null
            ? dc.GetDateTime() : (DateTime?)null;
        var totalAmount = order.GetProperty("total_amount").GetDecimal();
        var currencyId = order.GetProperty("currency_id").GetString() ?? "ARS";
        var buyerId = order.GetProperty("buyer").GetProperty("id").GetInt64();
        var buyerNickname = order.GetProperty("buyer")
            .GetProperty("nickname").GetString() ?? "Desconocido";
        var shippingId = order.TryGetProperty("shipping", out var sh)
            && sh.TryGetProperty("id", out var shId)
            && shId.ValueKind != JsonValueKind.Null
            ? shId.GetInt64() : (long?)null;

        // Fetch shipping status + substatus + mode + logistic_type from shipments API
        string? shippingStatus = null;
        string? shippingSubstatus = null;
        string? shippingMode = null;
        string? logisticType = null;
        if (shippingId.HasValue)
        {
            try
            {
                var shipResp = await http.GetAsync($"https://api.mercadolibre.com/shipments/{shippingId.Value}");
                if (shipResp.IsSuccessStatusCode)
                {
                    var shipJson = await shipResp.Content.ReadAsStringAsync();
                    var shipDoc = JsonDocument.Parse(shipJson).RootElement;
                    if (shipDoc.TryGetProperty("status", out var shipSt) && shipSt.ValueKind != JsonValueKind.Null)
                        shippingStatus = shipSt.GetString();
                    if (shipDoc.TryGetProperty("substatus", out var shipSub) && shipSub.ValueKind != JsonValueKind.Null)
                        shippingSubstatus = shipSub.GetString();
                    if (shipDoc.TryGetProperty("mode", out var shipMd) && shipMd.ValueKind != JsonValueKind.Null)
                        shippingMode = shipMd.GetString();
                    // 2026-05-25: logistic_type del shipment indica si la orden sale de Full o de tu depósito.
                    // Posibles valores: fulfillment (Full), self_service (Flex), drop_off, xd_drop_off,
                    // cross_docking, custom. Lo usamos para descontar del depósito correcto.
                    if (shipDoc.TryGetProperty("logistic_type", out var shipLt) && shipLt.ValueKind != JsonValueKind.Null)
                        logisticType = shipLt.GetString();
                }
            }
            catch { /* ignore shipping fetch errors */ }
        }

        var items = order.GetProperty("order_items");
        int count = 0;

        foreach (var item in items.EnumerateArray())
        {
            var itemNode = item.GetProperty("item");
            var itemId = itemNode.GetProperty("id").GetString() ?? "UNKNOWN";
            var itemTitle = itemNode.GetProperty("title").GetString() ?? "Sin titulo";
            // variation_id: solo viene cuando la publicacion es multi-variante (color/talle/etc)
            string? variationId = null;
            if (itemNode.TryGetProperty("variation_id", out var varEl) && varEl.ValueKind != JsonValueKind.Null)
            {
                variationId = varEl.ValueKind == JsonValueKind.Number
                    ? varEl.GetInt64().ToString()
                    : varEl.GetString();
            }
            var quantity = item.GetProperty("quantity").GetInt32();
            var unitPrice = item.GetProperty("unit_price").GetDecimal();
            decimal? fullUnitPrice = item.TryGetProperty("full_unit_price", out var fup)
                && fup.ValueKind != JsonValueKind.Null
                ? fup.GetDecimal() : null;

            var existing = await _db.MeliOrders.FirstOrDefaultAsync(
                o => o.MeliOrderId == meliOrderId && o.ItemId == itemId);

            if (existing is not null)
            {
                existing.Status = status;
                existing.DateClosed = dateClosed;
                existing.TotalAmount = totalAmount;
                existing.BuyerNickname = buyerNickname;
                existing.Quantity = quantity;
                existing.UnitPrice = unitPrice;
                existing.FullUnitPrice = fullUnitPrice;
                existing.ShippingId = shippingId;
                existing.PackId = packId;
                existing.ShippingStatus = shippingStatus;
                existing.ShippingSubstatus = shippingSubstatus;
                existing.ShippingMode = shippingMode;
                // Solo overrideamos LogisticType si vino del shipment (no perder valor previo si null)
                if (logisticType != null) existing.LogisticType = logisticType;
                // Solo actualizar VariationId si todavia no estaba seteado (no pisar lo que ya capturamos)
                if (string.IsNullOrEmpty(existing.VariationId))
                    existing.VariationId = variationId;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.MeliOrders.Add(new MeliOrder
                {
                    MeliOrderId = meliOrderId,
                    MeliAccountId = accountId,
                    Status = status,
                    DateCreated = dateCreated,
                    DateClosed = dateClosed,
                    TotalAmount = totalAmount,
                    CurrencyId = currencyId,
                    BuyerId = buyerId,
                    BuyerNickname = buyerNickname,
                    ItemId = itemId,
                    VariationId = variationId,
                    ItemTitle = itemTitle,
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    FullUnitPrice = fullUnitPrice,
                    ShippingId = shippingId,
                    PackId = packId,
                    ShippingStatus = shippingStatus,
                    ShippingSubstatus = shippingSubstatus,
                    ShippingMode = shippingMode,
                    LogisticType = logisticType
                });
            }
            count++;
        }

        return count;
    }
}
