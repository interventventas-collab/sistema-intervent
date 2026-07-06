using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Chat interno entre usuarios del sistema.
/// - Grupo general: mensajes con ParaUserId NULL, todos lo ven.
/// - Privado uno-a-uno: mensajes con ParaUserId = destinatario.
/// El "no leído" se calcula contra la tabla Chat_Lecturas (hasta dónde leyó cada uno).
/// </summary>
[ApiController]
[Route("api/chat")]
[Authorize]
public class ChatController : ControllerBase
{
    private readonly AppDbContext _db;
    private const int MaxLen = 4000;

    public ChatController(AppDbContext db) { _db = db; }

    private int? GetUserId()
    {
        var c = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
             ?? User.FindFirst("sub")?.Value;
        return int.TryParse(c, out var id) ? id : null;
    }

    private static string NombreDe(User u)
    {
        var full = $"{u.FirstName} {u.LastName}".Trim();
        return string.IsNullOrWhiteSpace(full) ? u.Username : full;
    }

    private static ChatMensajeDto Map(ChatMensaje m, int meId) =>
        new(m.Id, m.DeUserId, m.DeNombre ?? "Usuario", m.ParaUserId, m.Cuerpo, m.CreatedAt, m.DeUserId == meId);

    // ────────────────────────────────────────────────────────────────────────────
    // Panel izquierdo: grupo general + lista de usuarios con sus no leídos
    // ────────────────────────────────────────────────────────────────────────────
    [HttpGet("conversaciones")]
    public async Task<IActionResult> Conversaciones()
    {
        var meId = GetUserId();
        if (meId is null) return Unauthorized();

        var users = await _db.Users
            .Where(u => u.IsActive && u.Id != meId)
            .Select(u => new { u.Id, u.Username, u.FirstName, u.LastName, u.Role })
            .ToListAsync();

        var lecturas = await _db.ChatLecturas
            .Where(l => l.UserId == meId)
            .ToDictionaryAsync(l => l.Conversacion, l => l.LastReadAt);

        DateTime LastRead(string conv) => lecturas.TryGetValue(conv, out var t) ? t : DateTime.MinValue;

        // --- Grupo general ---
        var grupoLast = LastRead("grupo");
        var grupoNoLeidos = await _db.ChatMensajes
            .CountAsync(m => m.ParaUserId == null && m.DeUserId != meId && m.CreatedAt > grupoLast);
        var grupoUltimoMsg = await _db.ChatMensajes
            .Where(m => m.ParaUserId == null)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Cuerpo, m.CreatedAt })
            .FirstOrDefaultAsync();

        // --- Privados: cargo en memoria los mensajes directos que me involucran ---
        var dm = await _db.ChatMensajes
            .Where(m => m.ParaUserId != null && (m.DeUserId == meId || m.ParaUserId == meId))
            .Select(m => new { m.DeUserId, m.ParaUserId, m.Cuerpo, m.CreatedAt })
            .ToListAsync();

        var usuarios = new List<ChatUsuarioDto>();
        foreach (var u in users)
        {
            var lastRead = LastRead($"u:{u.Id}");
            var conMsgs = dm
                .Where(m => (m.DeUserId == u.Id && m.ParaUserId == meId)
                         || (m.DeUserId == meId && m.ParaUserId == u.Id))
                .ToList();
            var noLeidos = conMsgs.Count(m => m.DeUserId == u.Id && m.CreatedAt > lastRead);
            var ultimo = conMsgs.OrderByDescending(m => m.CreatedAt).FirstOrDefault();

            var uu = new User { FirstName = u.FirstName, LastName = u.LastName, Username = u.Username };
            usuarios.Add(new ChatUsuarioDto(
                u.Id,
                NombreDe(uu),
                u.Role,
                noLeidos,
                ultimo?.Cuerpo,
                ultimo?.CreatedAt));
        }

        // Ordeno: primero los que tienen algo (por último mensaje más reciente), después el resto alfabético
        usuarios = usuarios
            .OrderByDescending(x => x.UltimaFecha ?? DateTime.MinValue)
            .ThenBy(x => x.Nombre)
            .ToList();

        return Ok(new ChatConversacionesDto(
            grupoNoLeidos,
            grupoUltimoMsg?.Cuerpo,
            grupoUltimoMsg?.CreatedAt,
            usuarios));
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Mensajes de una conversación (y marca como leída). con = "grupo" ó "u:{id}"
    // ────────────────────────────────────────────────────────────────────────────
    [HttpGet("mensajes")]
    public async Task<IActionResult> Mensajes([FromQuery] string con)
    {
        var meId = GetUserId();
        if (meId is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(con)) return BadRequest(new { error = "Falta la conversación" });

        List<ChatMensaje> msgs;
        string convKey;

        if (con == "grupo")
        {
            convKey = "grupo";
            msgs = await _db.ChatMensajes
                .Where(m => m.ParaUserId == null)
                .OrderByDescending(m => m.CreatedAt)
                .Take(200)
                .ToListAsync();
        }
        else if (con.StartsWith("u:") && int.TryParse(con[2..], out var otherId))
        {
            convKey = $"u:{otherId}";
            msgs = await _db.ChatMensajes
                .Where(m => (m.DeUserId == meId && m.ParaUserId == otherId)
                         || (m.DeUserId == otherId && m.ParaUserId == meId))
                .OrderByDescending(m => m.CreatedAt)
                .Take(200)
                .ToListAsync();
        }
        else
        {
            return BadRequest(new { error = "Conversación inválida" });
        }

        msgs.Reverse(); // más viejo arriba, más nuevo abajo

        await MarcarLeidaAsync(meId.Value, convKey);

        return Ok(msgs.Select(m => Map(m, meId.Value)).ToList());
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Enviar mensaje
    // ────────────────────────────────────────────────────────────────────────────
    [HttpPost("enviar")]
    public async Task<IActionResult> Enviar([FromBody] EnviarChatRequest req)
    {
        var meId = GetUserId();
        if (meId is null) return Unauthorized();

        var cuerpo = (req.Cuerpo ?? "").Trim();
        if (string.IsNullOrWhiteSpace(cuerpo))
            return BadRequest(new { error = "El mensaje está vacío" });
        if (cuerpo.Length > MaxLen) cuerpo = cuerpo[..MaxLen];

        var yo = await _db.Users.FirstOrDefaultAsync(u => u.Id == meId);
        if (yo is null) return Unauthorized();

        if (req.ParaUserId is int para)
        {
            var existe = await _db.Users.AnyAsync(u => u.Id == para && u.IsActive);
            if (!existe) return BadRequest(new { error = "El destinatario no existe" });
        }

        // 2026-07-06: si viene firma del operador (Osmar/Germán/Gabriel/...), esa es el
        // nombre a mostrar — así se ve quién escribió aunque compartan la cuenta. Si no, el nombre de la cuenta.
        var firma = (req.Firma ?? "").Trim();
        var deNombre = string.IsNullOrWhiteSpace(firma) ? NombreDe(yo) : firma;
        if (deNombre.Length > 120) deNombre = deNombre[..120];

        var msg = new ChatMensaje
        {
            DeUserId = meId.Value,
            DeNombre = deNombre,
            ParaUserId = req.ParaUserId,
            Cuerpo = cuerpo,
            CreatedAt = DateTime.UtcNow
        };
        _db.ChatMensajes.Add(msg);

        // Mi propia conversación queda "al día" (lo que escribo, ya lo vi)
        var convKey = req.ParaUserId is null ? "grupo" : $"u:{req.ParaUserId}";
        await _db.SaveChangesAsync();
        await MarcarLeidaAsync(meId.Value, convKey);

        return Ok(Map(msg, meId.Value));
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Contador de no leídos (para el globito)
    // ────────────────────────────────────────────────────────────────────────────
    [HttpGet("no-leidos")]
    public async Task<IActionResult> NoLeidos()
    {
        var meId = GetUserId();
        if (meId is null) return Unauthorized();

        var lecturas = await _db.ChatLecturas
            .Where(l => l.UserId == meId)
            .ToDictionaryAsync(l => l.Conversacion, l => l.LastReadAt);

        DateTime LastRead(string conv) => lecturas.TryGetValue(conv, out var t) ? t : DateTime.MinValue;

        var grupoLast = LastRead("grupo");
        var grupo = await _db.ChatMensajes
            .CountAsync(m => m.ParaUserId == null && m.DeUserId != meId && m.CreatedAt > grupoLast);

        // Mensajes privados dirigidos a mí, agrupo por remitente y comparo con su lectura
        var haciaMi = await _db.ChatMensajes
            .Where(m => m.ParaUserId == meId)
            .Select(m => new { m.DeUserId, m.CreatedAt })
            .ToListAsync();

        var directos = haciaMi.Count(m => m.CreatedAt > LastRead($"u:{m.DeUserId}"));

        return Ok(new ChatNoLeidosDto(grupo + directos, grupo, directos));
    }

    private async Task MarcarLeidaAsync(int meId, string convKey)
    {
        var lect = await _db.ChatLecturas
            .FirstOrDefaultAsync(l => l.UserId == meId && l.Conversacion == convKey);
        if (lect is null)
            _db.ChatLecturas.Add(new ChatLectura { UserId = meId, Conversacion = convKey, LastReadAt = DateTime.UtcNow });
        else
            lect.LastReadAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }
}
