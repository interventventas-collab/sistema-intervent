using System.Collections.Concurrent;

namespace Api.Hubs;

/// <summary>
/// 2026-08-06: Estado EN MEMORIA (NO base de datos) de quién está viendo/escribiendo cada
/// conversación de WhatsApp. Singleton, compartido por todas las conexiones de SignalR.
///
/// Reglas del pedido:
///  - Un mismo usuario con VARIAS pestañas cuenta como UNO SOLO (se agrupa por userId y se
///    guardan sus connectionIds; recién desaparece cuando cierra todas).
///  - Expira si no llega heartbeat en cierto tiempo (lo barre PresenceSweeper).
///  - Guarda el último mensaje enviado por un agente por conversación (cierre de carrera).
///
/// Alcance ACOTADO: esto NO toca envío, ni asignación, ni estados. Solo "presencia".
/// </summary>
public class PresenceTracker
{
    public class Viewer
    {
        public int UserId { get; init; }
        public string UserName { get; set; } = "";
        public bool IsTyping { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public DateTime TypingSinceUtc { get; set; }
        public HashSet<string> ConnectionIds { get; } = new();
    }

    // convId -> (userId -> Viewer)
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<int, Viewer>> _convs = new();
    // connectionId -> convIds que esa pestaña tiene abiertos (para limpiar al desconectar)
    private readonly ConcurrentDictionary<string, HashSet<string>> _connConvs = new();
    // convId -> último mensaje mandado por un agente (para el "cierre de carrera")
    private readonly ConcurrentDictionary<string, (int UserId, string UserName, DateTime AtUtc)> _lastSent = new();

    private readonly object _lock = new();

    public void Join(string convId, int userId, string userName, string connectionId)
    {
        lock (_lock)
        {
            var viewers = _convs.GetOrAdd(convId, _ => new());
            var v = viewers.GetOrAdd(userId, _ => new Viewer { UserId = userId });
            v.UserName = userName;
            v.LastSeenUtc = DateTime.UtcNow;
            v.ConnectionIds.Add(connectionId);
            _connConvs.GetOrAdd(connectionId, _ => new()).Add(convId);
        }
    }

    public void Leave(string convId, int userId, string connectionId)
    {
        lock (_lock)
        {
            if (_convs.TryGetValue(convId, out var viewers) && viewers.TryGetValue(userId, out var v))
            {
                v.ConnectionIds.Remove(connectionId);
                if (v.ConnectionIds.Count == 0) viewers.TryRemove(userId, out _);
                if (viewers.IsEmpty) _convs.TryRemove(convId, out _);
            }
            if (_connConvs.TryGetValue(connectionId, out var set)) set.Remove(convId);
        }
    }

    /// <summary>Saca una pestaña (connection) de TODAS sus conversaciones — al desconectarse.
    /// Devuelve las convs afectadas para reemitir presencia.</summary>
    public List<string> RemoveConnection(string connectionId, int userId)
    {
        var affected = new List<string>();
        lock (_lock)
        {
            if (_connConvs.TryRemove(connectionId, out var convIds))
            {
                foreach (var convId in convIds)
                {
                    if (_convs.TryGetValue(convId, out var viewers) && viewers.TryGetValue(userId, out var v))
                    {
                        v.ConnectionIds.Remove(connectionId);
                        if (v.ConnectionIds.Count == 0) viewers.TryRemove(userId, out _);
                        if (viewers.IsEmpty) _convs.TryRemove(convId, out _);
                        affected.Add(convId);
                    }
                }
            }
        }
        return affected;
    }

    public void SetTyping(string convId, int userId, bool isTyping)
    {
        if (_convs.TryGetValue(convId, out var viewers) && viewers.TryGetValue(userId, out var v))
        {
            v.IsTyping = isTyping;
            v.TypingSinceUtc = isTyping ? DateTime.UtcNow : default;
            v.LastSeenUtc = DateTime.UtcNow;
        }
    }

    public void Heartbeat(string convId, int userId)
    {
        if (_convs.TryGetValue(convId, out var viewers) && viewers.TryGetValue(userId, out var v))
            v.LastSeenUtc = DateTime.UtcNow;
    }

    public void NoteSent(string convId, int userId, string userName)
        => _lastSent[convId] = (userId, userName, DateTime.UtcNow);

    public IReadOnlyList<Viewer> Viewers(string convId)
        => _convs.TryGetValue(convId, out var viewers) ? viewers.Values.ToList() : new List<Viewer>();

    /// <summary>Barre expirados: saca viewers sin heartbeat en más de <paramref name="ttl"/>, y apaga
    /// el "escribiendo…" que quedó pegado más de <paramref name="typingTtl"/> (por si una pestaña murió
    /// tipeando). Devuelve las convs que cambiaron, para reemitir presencia.</summary>
    public List<string> Sweep(TimeSpan ttl, TimeSpan typingTtl)
    {
        var changed = new HashSet<string>();
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            foreach (var (convId, viewers) in _convs)
            {
                foreach (var v in viewers.Values.ToList())
                {
                    if (now - v.LastSeenUtc > ttl)
                    {
                        viewers.TryRemove(v.UserId, out _);
                        foreach (var cid in v.ConnectionIds)
                            if (_connConvs.TryGetValue(cid, out var set)) set.Remove(convId);
                        changed.Add(convId);
                    }
                    else if (v.IsTyping && v.TypingSinceUtc != default && now - v.TypingSinceUtc > typingTtl)
                    {
                        v.IsTyping = false;
                        changed.Add(convId);
                    }
                }
                if (viewers.IsEmpty) _convs.TryRemove(convId, out _);
            }
        }
        return changed.ToList();
    }

    public (int UserId, string UserName, DateTime AtUtc)? LastSent(string convId)
        => _lastSent.TryGetValue(convId, out var x) ? x : null;
}
