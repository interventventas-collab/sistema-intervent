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
            meliUrl = $"https://articulo.mercadolibre.com.ar/{x.ItemId}"
        }));
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
