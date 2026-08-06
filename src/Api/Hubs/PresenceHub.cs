using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Api.Hubs;

/// <summary>
/// 2026-08-06: Hub de PRESENCIA para la bandeja de WhatsApp. Sirve para que los agentes se vean
/// entre sí en vivo (quién mira / escribe cada conversación) y no se pisen. Grupo por conversación
/// "conv-{convId}". El convId lo arma el frontend como "{numero}|{linea}".
///
/// Eventos que emite al cliente:
///  - "Presence"    (convId, viewers[])   → lista {userId, userName, isTyping} de esa conv.
///  - "MessageSent" (convId, userId, userName, atUtc) → otro agente acaba de enviar (cierre de carrera).
///
/// Alcance ACOTADO: NO toca envío de mensajes, ni asignación, ni estados.
/// </summary>
[Authorize]
public class PresenceHub : Hub
{
    private readonly PresenceTracker _tracker;
    public PresenceHub(PresenceTracker tracker) => _tracker = tracker;

    private int UserId =>
        int.TryParse(Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                     ?? Context.User?.FindFirst("sub")?.Value, out var id) ? id : 0;
    private string UserName => Context.User?.FindFirst(ClaimTypes.Name)?.Value ?? "?";

    private static string Grupo(string convId) => $"conv-{convId}";

    public async Task JoinConversation(string convId)
    {
        if (string.IsNullOrWhiteSpace(convId)) return;
        _tracker.Join(convId, UserId, UserName, Context.ConnectionId);
        await Groups.AddToGroupAsync(Context.ConnectionId, Grupo(convId));
        await BroadcastPresence(convId);
    }

    public async Task LeaveConversation(string convId)
    {
        if (string.IsNullOrWhiteSpace(convId)) return;
        _tracker.Leave(convId, UserId, Context.ConnectionId);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, Grupo(convId));
        await BroadcastPresence(convId);
    }

    /// <summary>El cliente lo llama al empezar/parar de tipear (se apaga solo a los 4 s en el cliente).</summary>
    public async Task Typing(string convId, bool isTyping)
    {
        if (string.IsNullOrWhiteSpace(convId)) return;
        _tracker.SetTyping(convId, UserId, isTyping);
        await BroadcastPresence(convId);
    }

    /// <summary>Latido para no expirar (el cliente lo manda cada ~15 s mientras tiene la conv abierta).</summary>
    public Task Heartbeat(string convId)
    {
        if (!string.IsNullOrWhiteSpace(convId)) _tracker.Heartbeat(convId, UserId);
        return Task.CompletedTask;
    }

    /// <summary>El cliente lo llama DESPUÉS de enviar un mensaje OK (no toca el envío en sí). Avisa a
    /// los demás para el "cierre de carrera" (confirmación si otro respondió recién).</summary>
    public async Task NotifyMessageSent(string convId)
    {
        if (string.IsNullOrWhiteSpace(convId)) return;
        _tracker.NoteSent(convId, UserId, UserName);
        await Clients.OthersInGroup(Grupo(convId))
            .SendAsync("MessageSent", convId, UserId, UserName, DateTime.UtcNow);
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var convId in _tracker.RemoveConnection(Context.ConnectionId, UserId))
            await BroadcastPresence(convId);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task BroadcastPresence(string convId)
    {
        var viewers = _tracker.Viewers(convId)
            .Select(v => new { userId = v.UserId, userName = v.UserName, isTyping = v.IsTyping })
            .ToList();
        await Clients.Group(Grupo(convId)).SendAsync("Presence", convId, viewers);
    }
}
