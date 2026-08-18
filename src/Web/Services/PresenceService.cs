using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.SignalR.Client;

namespace Web.Services;

/// <summary>
/// 2026-08-06: Cliente de PRESENCIA en vivo de la bandeja de WhatsApp (SignalR). Se conecta al hub
/// /api/hubs/presence (la cookie de login viaja sola, mismo origen). Expone eventos para que la
/// pantalla pinte quién mira/escribe cada chat, y avise en el "cierre de carrera".
///
/// Alcance ACOTADO: NO envía mensajes ni toca asignación/estados. Si la conexión falla, la pantalla
/// sigue funcionando igual (best-effort): simplemente no se ven los indicadores de presencia.
/// </summary>
public class PresenceService : IAsyncDisposable
{
    public class Viewer
    {
        public int UserId { get; set; }
        public string UserName { get; set; } = "";
        public bool IsTyping { get; set; }
    }

    private readonly NavigationManager _nav;
    private HubConnection? _hub;

    public PresenceService(NavigationManager nav) => _nav = nav;

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>(convId, viewers de esa conversación). Se dispara ante cualquier cambio de presencia.</summary>
    public event Action<string, List<Viewer>>? OnPresence;
    /// <summary>(convId, userId, userName, cuándo UTC). Otro agente acaba de enviar un mensaje.</summary>
    public event Action<string, int, string, DateTime>? OnMessageSent;
    /// <summary>2026-08-18: (convId, direccion, cuándo UTC). Entró o salió un mensaje DE VERDAD — lo
    /// avisa el servidor (webhook de Meta / endpoint de envío). Sirve para refrescar al instante en
    /// vez de preguntar cada 12–15 s, que es lo que hacía sentir lento al celular.</summary>
    public event Action<string, string, DateTime>? OnNuevoMensaje;

    public async Task EnsureStartedAsync()
    {
        if (_hub != null) return;
        _hub = new HubConnectionBuilder()
            .WithUrl($"{_nav.BaseUri}api/hubs/presence")
            .WithAutomaticReconnect()
            .Build();

        _hub.On<string, List<Viewer>>("Presence", (convId, viewers) => OnPresence?.Invoke(convId, viewers));
        _hub.On<string, int, string, DateTime>("MessageSent",
            (convId, uid, uname, at) => OnMessageSent?.Invoke(convId, uid, uname, at));
        _hub.On<string, string, DateTime>("WaNuevoMensaje",
            (convId, direccion, at) => OnNuevoMensaje?.Invoke(convId, direccion, at));

        try { await _hub.StartAsync(); }
        catch { /* best-effort: sin presencia, la pantalla anda igual */ }
    }

    public Task JoinAsync(string convId) => SafeInvoke("JoinConversation", convId);
    public Task LeaveAsync(string convId) => SafeInvoke("LeaveConversation", convId);
    public Task TypingAsync(string convId, bool isTyping) => SafeInvoke("Typing", convId, isTyping);
    public Task HeartbeatAsync(string convId) => SafeInvoke("Heartbeat", convId);
    public Task NotifyMessageSentAsync(string convId) => SafeInvoke("NotifyMessageSent", convId);

    private async Task SafeInvoke(string method, params object?[] args)
    {
        if (_hub is null || _hub.State != HubConnectionState.Connected) return;
        try { await _hub.SendCoreAsync(method, args); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        if (_hub != null)
        {
            try { await _hub.DisposeAsync(); } catch { }
            _hub = null;
        }
    }
}
