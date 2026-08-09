using Api.Data;
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
            meliUrl = $"https://articulo.mercadolibre.com.ar/{x.ItemId}"
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
        object buyer = new
        {
            buyerId = x.FromUserId,
            nickname = string.IsNullOrEmpty(x.FromNickname) ? cli?.Nickname : x.FromNickname,
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
}
