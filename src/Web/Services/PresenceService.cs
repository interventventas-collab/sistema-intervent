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
    private bool _yaArranco;   // para no avisar "reconectado" en el primer arranque

    public PresenceService(NavigationManager nav) => _nav = nav;

    /// <summary>
    /// 2026-08-20: reintenta PARA SIEMPRE. `WithAutomaticReconnect()` sin parámetros prueba 4
    /// veces (0 s, 2 s, 10 s, 30 s) y después SE RINDE Y NO VUELVE A INTENTAR NUNCA. En un celular
    /// eso pasa todos los días: bloqueás la pantalla, la conexión se corta, y cuando volvés ya se
    /// había rendido — los mensajes dejaban de llegar solos y había que esperar la consulta de
    /// respaldo (hasta 1 minuto) o recargar la pantalla a mano.
    /// </summary>
    private sealed class ReintentarSiempre : IRetryPolicy
    {
        private static readonly TimeSpan[] Escalera =
        {
            TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20)
        };

        public TimeSpan? NextRetryDelay(RetryContext ctx)
            => ctx.PreviousRetryCount < (long)Escalera.Length
                ? Escalera[ctx.PreviousRetryCount]
                : TimeSpan.FromSeconds(30);   // después, uno cada 30 s, sin rendirse
    }

    public bool IsConnected => _hub?.State == HubConnectionState.Connected;

    /// <summary>(convId, viewers de esa conversación). Se dispara ante cualquier cambio de presencia.</summary>
    public event Action<string, List<Viewer>>? OnPresence;
    /// <summary>(convId, userId, userName, cuándo UTC). Otro agente acaba de enviar un mensaje.</summary>
    public event Action<string, int, string, DateTime>? OnMessageSent;
    /// <summary>2026-08-18: (convId, direccion, cuándo UTC). Entró o salió un mensaje DE VERDAD — lo
    /// avisa el servidor (webhook de Meta / endpoint de envío). Sirve para refrescar al instante en
    /// vez de preguntar cada 12–15 s, que es lo que hacía sentir lento al celular.</summary>
    public event Action<string, string, DateTime>? OnNuevoMensaje;
    /// <summary>2026-08-20: se recuperó la conexión después de un corte. Mientras estuvo cortada NO
    /// llegó ningún aviso, así que la pantalla tiene que ir a buscar lo que se perdió.</summary>
    public event Action? OnReconectado;

    /// <summary>
    /// Conecta si no está conectado. Se puede llamar todas las veces que haga falta.
    /// 2026-08-20: antes salía con `if (_hub != null) return;`, así que si el PRIMER intento fallaba
    /// (abrir la pantalla con mala señal) el objeto quedaba creado pero muerto y no se reintentaba
    /// NUNCA en toda la sesión. Ahora lo que manda es el estado, no que el objeto exista.
    /// </summary>
    public async Task EnsureStartedAsync()
    {
        if (_hub is null)
        {
            _hub = new HubConnectionBuilder()
                .WithUrl($"{_nav.BaseUri}api/hubs/presence")
                .WithAutomaticReconnect(new ReintentarSiempre())
                .Build();

            _hub.On<string, List<Viewer>>("Presence", (convId, viewers) => OnPresence?.Invoke(convId, viewers));
            _hub.On<string, int, string, DateTime>("MessageSent",
                (convId, uid, uname, at) => OnMessageSent?.Invoke(convId, uid, uname, at));
            _hub.On<string, string, DateTime>("WaNuevoMensaje",
                (convId, direccion, at) => OnNuevoMensaje?.Invoke(convId, direccion, at));

            // Volvió solo después de un corte: hay que traer lo que entró mientras no había canal.
            _hub.Reconnected += _ => { OnReconectado?.Invoke(); return Task.CompletedTask; };
        }

        if (_hub.State != HubConnectionState.Disconnected) return;   // ya está conectado o conectando

        try
        {
            await _hub.StartAsync();
            if (_yaArranco) OnReconectado?.Invoke();   // era un RE-arranque: puede faltar algo
            _yaArranco = true;
        }
        catch { /* best-effort: sin presencia, la pantalla anda igual (queda el sondeo de respaldo) */ }
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
