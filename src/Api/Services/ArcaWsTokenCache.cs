using System.Collections.Concurrent;

namespace Api.Services;

/// <summary>
/// Cache de TA (Ticket de Acceso) de WSAA. Los TA duran 12 horas y ARCA
/// rechaza pedidos sucesivos para el mismo (cuit, service) — si no cacheamos
/// vamos a recibir "El CEE ya posee un TA válido para el acceso al WSN
/// solicitado". Reutilizamos el TA cacheado mientras le queden más de 5
/// minutos de validez.
///
/// Importante: separamos por AMBIENTE en la key porque un mismo CUIT en
/// producción y homologación son contextos distintos.
/// </summary>
public class ArcaWsTokenCache
{
    public record CachedTa(string Token, string Sign, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<string, CachedTa> _cache = new();
    private static readonly TimeSpan SafetyMargin = TimeSpan.FromMinutes(5);

    private static string KeyOf(string environment, string cuit, string service)
        => $"{environment}:{cuit}:{service}";

    public CachedTa? Get(string environment, string cuit, string service)
    {
        var key = KeyOf(environment, cuit, service);
        if (_cache.TryGetValue(key, out var ta))
        {
            var left = ta.ExpiresAt - DateTimeOffset.UtcNow;
            if (left > SafetyMargin) return ta;
            // Margen muerto — sacarlo y forzar nuevo login
            _cache.TryRemove(key, out _);
        }
        return null;
    }

    public void Set(string environment, string cuit, string service, CachedTa ta)
        => _cache[KeyOf(environment, cuit, service)] = ta;

    public void Invalidate(string environment, string cuit, string service)
        => _cache.TryRemove(KeyOf(environment, cuit, service), out _);
}
