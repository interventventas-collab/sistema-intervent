using System.Text.Json;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/meli/questions")]
[Authorize]
public class MeliQuestionsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MeliQuestionService _service;

    public MeliQuestionsController(AppDbContext db, MeliQuestionService service)
    {
        _db = db; _service = service;
    }

    /// <summary>
    /// 2026-08-16: link al perfil publico del comprador en MeLi. Se arma con el apodo, no con el id
    /// (MeLi no tiene una URL de perfil por id). Si no tenemos apodo devolvemos null y la UI no
    /// muestra el link — pasa cuando MeLi enmascara al que pregunta.
    /// Sitio fijo .com.ar, igual que el resto del controller (meliUrl de las publicaciones).
    /// </summary>
    private static string? PerfilUrl(string? nickname)
        => string.IsNullOrWhiteSpace(nickname)
            ? null
            : $"https://www.mercadolibre.com.ar/perfil/{Uri.EscapeDataString(nickname.Trim())}";

    /// <summary>Lista preguntas. Por default solo UNANSWERED. ?status=ALL para todas.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string status = "UNANSWERED", [FromQuery] int limit = 100)
    {
        var q = _db.MeliQuestions
            .Include(x => x.MeliAccount)
            .AsQueryable();
        if (!string.Equals(status, "ALL", StringComparison.OrdinalIgnoreCase))
            q = q.Where(x => x.Status == status.ToUpper());
        var list = await q.OrderByDescending(x => x.DateCreated).Take(Math.Clamp(limit, 1, 500)).ToListAsync();

        // Enriquecemos con el tipo de logística de cada publicación (Flex / Correo / ME1 / Full)
        // en una sola consulta, para pintar el cartelito de envío en la lista sin N+1.
        var itemIds = list.Select(x => x.ItemId).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
        var logiPairs = await _db.MeliItems
            .Where(i => itemIds.Contains(i.MeliItemId))
            .Select(i => new { i.MeliItemId, i.LogisticType })
            .ToListAsync();
        var logiMap = logiPairs
            .GroupBy(x => x.MeliItemId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.LogisticType).FirstOrDefault(v => !string.IsNullOrEmpty(v)));

        // 2026-08-16: resumen del que pregunta (si ya nos compro antes), en UNA sola consulta para
        // no hacer N+1. Con esto el cartel de la campanita puede avisar "cliente conocido" sin pedir
        // el detalle de cada pregunta.
        var buyerIds = list.Select(x => x.FromUserId).Where(v => v > 0).Distinct().ToList();
        var clientes = await _db.MeliClientes
            .Where(c => buyerIds.Contains(c.BuyerId))
            .Select(c => new { c.BuyerId, c.OrdersCount, c.LastPurchaseAt, c.Nickname })
            .ToListAsync();
        var cliMap = clientes.GroupBy(c => c.BuyerId).ToDictionary(g => g.Key, g => g.First());

        return Ok(list.Select(x => new
        {
            id = x.Id,
            meliQuestionId = x.MeliQuestionId,
            accountId = x.MeliAccountId,
            accountNickname = x.MeliAccount != null ? x.MeliAccount.Nickname : null,
            itemId = x.ItemId,
            itemTitle = x.ItemTitle,
            itemThumbnail = x.ItemThumbnail,
            fromUserId = x.FromUserId,
            fromNickname = x.FromNickname,
            text = x.Text,
            answerText = x.AnswerText,
            status = x.Status,
            dateCreated = x.DateCreated,
            dateAnswered = x.DateAnswered,
            seenAt = x.SeenAt,
            isNew = x.SeenAt == null && x.Status == "UNANSWERED",
            logisticType = logiMap.TryGetValue(x.ItemId, out var lt) ? lt : null,
            meliUrl = $"https://articulo.mercadolibre.com.ar/{x.ItemId}",
            buyerIsKnown = cliMap.ContainsKey(x.FromUserId),
            buyerOrdersCount = cliMap.TryGetValue(x.FromUserId, out var c1) ? c1.OrdersCount : 0,
            buyerLastPurchaseAt = cliMap.TryGetValue(x.FromUserId, out var c2) ? c2.LastPurchaseAt : null,
            buyerProfileUrl = PerfilUrl(string.IsNullOrEmpty(x.FromNickname)
                ? (cliMap.TryGetValue(x.FromUserId, out var c3) ? c3.Nickname : null)
                : x.FromNickname)
        }));
    }

    /// <summary>
    /// Detalle de una pregunta para la bandeja: la publicación (precio, stock, SKU, envío) y
    /// el comprador (lo que sabemos de él en nuestra base de clientes MeLi).
    /// </summary>
    [HttpGet("{id:int}/detail")]
    public async Task<IActionResult> GetDetail(int id)
    {
        var x = await _db.MeliQuestions.Include(q => q.MeliAccount).FirstOrDefaultAsync(q => q.Id == id);
        if (x is null) return NotFound(new { error = "Pregunta no encontrada" });

        // Publicación: puede haber varias filas (variantes) con el mismo MeliItemId.
        var itemRows = await _db.MeliItems.Where(i => i.MeliItemId == x.ItemId).ToListAsync();
        object? item = null;
        if (itemRows.Count > 0)
        {
            var first = itemRows[0];
            item = new
            {
                itemId = x.ItemId,
                title = first.Title,
                price = first.Price,
                stock = itemRows.Sum(i => i.AvailableQuantity),
                sku = first.Sku,
                status = first.Status,
                catalogListing = first.CatalogListing,
                logisticType = itemRows.Select(i => i.LogisticType).FirstOrDefault(v => !string.IsNullOrEmpty(v)),
                permalink = first.Permalink,
                thumbnail = string.IsNullOrEmpty(first.Thumbnail) ? x.ItemThumbnail : first.Thumbnail
            };
        }

        // Comprador: lo que tengamos guardado en la base de clientes MeLi (se llena con cada venta).
        var cli = await _db.MeliClientes.FirstOrDefaultAsync(c => c.BuyerId == x.FromUserId);

        // 2026-08-16: historial de compras anteriores de este mismo comprador. Sale de nuestra base
        // (MeliClienteCompras, una fila por venta), asi que no gasta consultas a MeLi. Cortamos en 10:
        // es para responder la pregunta, no para auditar la cuenta.
        var compras = cli is null
            ? new List<object>()
            : (await _db.MeliClienteCompras
                    .Where(c => c.BuyerId == x.FromUserId)
                    .OrderByDescending(c => c.Fecha)
                    .Take(10)
                    .Select(c => new { c.Fecha, c.Items, c.Cantidad, c.Total, c.Canal, c.MeliOrderId })
                    .ToListAsync())
                .Select(c => (object)new
                {
                    fecha = c.Fecha,
                    items = c.Items,
                    cantidad = c.Cantidad,
                    total = c.Total,
                    canal = c.Canal,
                    meliOrderId = c.MeliOrderId
                })
                .ToList();

        var nick = string.IsNullOrEmpty(x.FromNickname) ? cli?.Nickname : x.FromNickname;
        object buyer = new
        {
            buyerId = x.FromUserId,
            nickname = nick,
            profileUrl = PerfilUrl(nick),
            compras,
            isKnown = cli != null,
            receiverName = cli?.ReceiverName,
            phone = cli?.Phone,
            neighborhood = cli?.Neighborhood,
            city = cli?.City,
            state = cli?.State,
            ordersCount = cli?.OrdersCount ?? 0,
            totalSpent = cli?.TotalSpent ?? 0m,
            lastItems = cli?.LastItems,
            firstPurchaseAt = cli?.FirstPurchaseAt,
            lastPurchaseAt = cli?.LastPurchaseAt
        };

        return Ok(new
        {
            question = new
            {
                id = x.Id,
                itemId = x.ItemId,
                itemTitle = x.ItemTitle,
                itemThumbnail = x.ItemThumbnail,
                accountNickname = x.MeliAccount != null ? x.MeliAccount.Nickname : null,
                fromUserId = x.FromUserId,
                fromNickname = x.FromNickname,
                text = x.Text,
                answerText = x.AnswerText,
                status = x.Status,
                dateCreated = x.DateCreated,
                dateAnswered = x.DateAnswered,
                autoAnswered = x.AutoAnswered,
                meliUrl = $"https://articulo.mercadolibre.com.ar/{x.ItemId}"
            },
            item,
            buyer
        });
    }

    /// <summary>Endpoint chiquito para el polling de la campanita. Devuelve count de UNANSWERED.</summary>
    [HttpGet("unread-count")]
    public async Task<IActionResult> GetUnreadCount()
    {
        var total = await _db.MeliQuestions.CountAsync(q => q.Status == "UNANSWERED");
        var notSeen = await _db.MeliQuestions.CountAsync(q => q.Status == "UNANSWERED" && q.SeenAt == null);
        return Ok(new { total, notSeen });
    }

    public record AnswerRequest(string Text);

    /// <summary>Responde una pregunta — postea a MeLi y actualiza el registro.</summary>
    [HttpPost("{id:int}/answer")]
    public async Task<IActionResult> Answer(int id, [FromBody] AnswerRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text))
            return BadRequest(new { error = "La respuesta no puede estar vacía" });
        try
        {
            var q = await _service.AnswerAsync(id, req.Text);
            if (q is null) return NotFound(new { error = "Pregunta no encontrada" });
            return Ok(new { id = q.Id, status = q.Status, answerText = q.AnswerText });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>Marca todas las UNANSWERED como vistas (para que la campanita deje de parpadear).</summary>
    [HttpPost("mark-seen")]
    public async Task<IActionResult> MarkSeen()
    {
        await _service.MarkAllSeenAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Trigger manual de sincronizacion (boton "Refrescar ahora").</summary>
    [HttpPost("sync-now")]
    public async Task<IActionResult> SyncNow()
    {
        var r = await _service.SyncAsync();
        return Ok(new { sincronizadas = r.TotalSynced, nuevas = r.TotalNew, errores = r.TotalErrors, mensajes = r.Errors });
    }

    // ===================== RESPUESTAS RÁPIDAS (canned) =====================
    // Lista propia de respuestas para contestar rápido a mano desde la bandeja.
    // Es SEPARADA de los "mensajes que rotan" del robot. Se guarda como JSON en AppSettings
    // (una sola clave) para no tocar la base de datos.

    private const string CfgQuickReplies = "meli.quickreplies";

    public class QuickReplyItem
    {
        public string Category { get; set; } = "Otros";
        public string Text { get; set; } = "";
    }

    private static readonly JsonSerializerOptions CamelJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static readonly List<QuickReplyItem> DefaultQuickReplies = new()
    {
        new() { Category = "Stock",  Text = "¡Hola! Sí, tenemos stock disponible para entrega inmediata 😊" },
        new() { Category = "Envío",  Text = "¡Gracias por tu consulta! Hacemos envíos a todo el país por Mercado Envíos." },
        new() { Category = "Precio", Text = "¡Hola! El precio publicado es el final, con IVA incluido." },
        new() { Category = "Retiro", Text = "Sí, se puede retirar por nuestro depósito. Coordinamos por acá 😊" }
    };

    /// <summary>Parsea la lista guardada. Tolera el formato viejo (array de strings sin categoría).</summary>
    private static List<QuickReplyItem> ParseQuickReplies(string raw)
    {
        var list = new List<QuickReplyItem>();
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.String)
                {
                    // formato viejo: solo texto → categoría "Otros"
                    list.Add(new QuickReplyItem { Category = "Otros", Text = el.GetString() ?? "" });
                }
                else if (el.ValueKind == JsonValueKind.Object)
                {
                    var cat = el.TryGetProperty("category", out var c) ? c.GetString() : null;
                    var txt = el.TryGetProperty("text", out var t) ? t.GetString() : null;
                    list.Add(new QuickReplyItem
                    {
                        Category = string.IsNullOrWhiteSpace(cat) ? "Otros" : cat!.Trim(),
                        Text = txt ?? ""
                    });
                }
            }
        }
        catch { /* si el JSON está roto, devolvemos lo que se pudo */ }
        return list.Where(i => !string.IsNullOrWhiteSpace(i.Text)).ToList();
    }

    /// <summary>Devuelve las respuestas rápidas guardadas. Si nunca se guardó nada, devuelve unas de ejemplo.</summary>
    [HttpGet("quick-replies")]
    public async Task<IActionResult> GetQuickReplies()
    {
        var raw = (await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == CfgQuickReplies))?.Value;
        var items = string.IsNullOrWhiteSpace(raw) ? DefaultQuickReplies : ParseQuickReplies(raw);
        return Ok(items);
    }

    public record QuickRepliesRequest(List<QuickReplyItem> Items);

    /// <summary>Guarda la lista completa de respuestas rápidas (reemplaza la anterior).</summary>
    [HttpPut("quick-replies")]
    public async Task<IActionResult> SaveQuickReplies([FromBody] QuickRepliesRequest req)
    {
        var items = (req.Items ?? new List<QuickReplyItem>())
            .Select(i => new QuickReplyItem
            {
                Category = string.IsNullOrWhiteSpace(i.Category) ? "Otros" : i.Category.Trim(),
                Text = (i.Text ?? "").Trim()
            })
            .Where(i => i.Text.Length > 0)
            .Take(40)
            .ToList();

        var json = JsonSerializer.Serialize(items, CamelJson);
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == CfgQuickReplies);
        if (s is null) { s = new AppSetting { Key = CfgQuickReplies }; _db.AppSettings.Add(s); }
        s.Value = json;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(items);
    }
}
