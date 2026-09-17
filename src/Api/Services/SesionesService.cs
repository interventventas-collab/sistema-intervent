using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Services;

/// <summary>
/// 2026-09-17 — El registro de quién está adentro del sistema, y la manija para echarlo.
///
/// CÓMO HACE EFECTO "AL TOQUE". El chequeo de cada pedido no puede ir a la base siempre: serían
/// miles de consultas por hora. Entonces el resultado se guarda en memoria por un ratito
/// (<see cref="TtlVivaSeg"/>). Lo que hace que igual sea instantáneo es que quien cierra la sesión
/// BORRA esa memoria a mano: el pedido siguiente ya no la encuentra, va a la base, ve la fila
/// muerta y rebota. No hay que esperar a que se venza nada.
///
/// ⚠ Esto vale porque corre UN solo contenedor de API (así está armado dev y prod). Si algún día
/// hubiera dos, la memoria de uno no se enteraría de lo que borró el otro y el cierre podría tardar
/// hasta <see cref="TtlVivaSeg"/> segundos en ese contenedor. Anotado a propósito.
/// </summary>
public class SesionesService
{
    /// <summary>Cuánto vale la respuesta guardada en memoria antes de volver a mirar la base.</summary>
    public const int TtlVivaSeg = 60;

    /// <summary>Clave de AppSettings donde viven las redes conocidas ("Oficina", "Depósito").</summary>
    public const string KeyRedesConocidas = "sesiones.redes_conocidas";

    /// <summary>Claim propio donde queda el número de pase ya validado, para que cada controlador
    /// sepa cuál es SU sesión. Se pone en Program.cs, después de validar el token.</summary>
    public const string ClaimJti = "sesion_jti";

    private readonly AppDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SesionesService> _log;

    public SesionesService(AppDbContext db, IMemoryCache cache, ILogger<SesionesService> log)
    {
        _db = db; _cache = cache; _log = log;
    }

    private static string CacheKey(string jti) => "sesion:" + jti;

    // ------------------------------------------------------------------
    // Abrir
    // ------------------------------------------------------------------

    /// <summary>
    /// Registra una sesión nueva y devuelve el número de pase que hay que meter en el JWT.
    ///
    /// Si esa persona ya tenía una sesión viva EN EL MISMO APARATO, se reusa la fila: se le pone el
    /// pase nuevo y el anterior queda muerto. Por eso la pantalla muestra aparatos y no un historial
    /// de logins, y por eso volver a entrar desde la misma compu tira abajo el pase viejo de ahí.
    /// </summary>
    /// <returns>El jti nuevo y si el aparato nunca se había visto antes para esa persona.</returns>
    public async Task<(string Jti, bool AparatoNuevo)> AbrirAsync(
        int? userId, string nombre, string tipo, DateTime expiraAt,
        string? userAgent, string? ip)
    {
        var (dispositivo, huella) = DescribirDispositivo(userAgent);
        var jti = Guid.NewGuid().ToString("N");
        var ahora = DateTime.UtcNow;

        // ¿Este aparato ya se había visto alguna vez para esta persona? (viva o cerrada)
        var conocido = await _db.UserSessions
            .Where(s => s.Nombre == nombre && s.Tipo == tipo && s.Huella == huella)
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync();

        // ⚠ El PRIMER aparato de una persona NO cuenta como "aparato nuevo": no hay contra qué
        // compararlo. Sin esta salvedad, el dia que se estrena esta pantalla TODAS las filas
        // saldrian marcadas y el aviso no serviria para nada. El que importa es el segundo: el que
        // aparece cuando alguien que siempre entra de la misma compu, de golpe entra de otra.
        var yaTeniaAlgunAparato = await _db.UserSessions.AnyAsync(s => s.Nombre == nombre);
        var aparatoNuevo = conocido is null && yaTeniaAlgunAparato;

        // Reusar la fila viva del mismo aparato, si hay.
        var viva = conocido is not null && conocido.CerradaAt == null ? conocido : null;
        if (viva is not null)
        {
            Olvidar(viva.Jti);                  // el pase anterior de ese aparato deja de valer
            viva.Jti = jti;
            viva.UserId = userId;
            viva.UserAgent = Recortar(userAgent, 400);
            viva.Dispositivo = dispositivo;
            viva.IpUltima = Recortar(ip, 60);
            viva.ExpiraAt = expiraAt;
            viva.UltimaActividadAt = ahora;
            viva.UltimaEntradaAt = ahora;       // esta ES una entrada nueva, aunque el renglon se reuse
            viva.CerradaAt = null;
            viva.CerradaPor = null;
            viva.CerradaMotivo = null;
        }
        else
        {
            _db.UserSessions.Add(new UserSession
            {
                Jti = jti,
                UserId = userId,
                Nombre = nombre,
                Tipo = tipo,
                UserAgent = Recortar(userAgent, 400),
                Dispositivo = dispositivo,
                Huella = huella,
                // Si el aparato ya se conocía, heredar el apodo que le habían puesto.
                Apodo = conocido?.Apodo,
                IpCreacion = Recortar(ip, 60),
                IpUltima = Recortar(ip, 60),
                AparatoNuevo = aparatoNuevo,
                CreatedAt = ahora,
                UltimaEntradaAt = ahora,
                ExpiraAt = expiraAt,
                UltimaActividadAt = ahora
            });
        }

        await _db.SaveChangesAsync();

        // La entrada se anota DESPUES de guardar porque una fila nueva recien ahi tiene Id.
        // Va en su propia tabla: el renglon del aparato se pisa a si mismo en cada entrada, la
        // historia no. Si esto fallara, la persona ya entro igual: no se le corta el acceso por
        // no poder anotar el renglon de historial.
        var sesionId = viva?.Id ?? await _db.UserSessions
            .Where(x => x.Jti == jti).Select(x => x.Id).FirstOrDefaultAsync();
        if (sesionId > 0)
        {
            try
            {
                _db.UserSessionEntradas.Add(new UserSessionEntrada
                {
                    SesionId = sesionId,
                    Nombre = nombre,
                    Tipo = tipo,
                    CuandoAt = ahora,
                    Ip = Recortar(ip, 60)
                });
                await _db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "No se pudo anotar la entrada de {Nombre}", nombre);
            }
        }

        return (jti, aparatoNuevo);
    }

    /// <summary>Las ultimas entradas desde un aparato, lo mas nuevo arriba.</summary>
    public async Task<List<UserSessionEntrada>> EntradasAsync(int sesionId, int tope = 50)
    {
        return await _db.UserSessionEntradas.AsNoTracking()
            .Where(e => e.SesionId == sesionId)
            .OrderByDescending(e => e.CuandoAt)
            .Take(Math.Clamp(tope, 1, 500))
            .ToListAsync();
    }

    // ------------------------------------------------------------------
    // Chequear (camino caliente: corre en CADA pedido)
    // ------------------------------------------------------------------

    /// <summary>
    /// ¿Este pase sigue valiendo? Responde de memoria casi siempre. Cuando va a la base, aprovecha
    /// y le deja anotado que la sesión sigue activa (y desde qué IP).
    /// </summary>
    public async Task<bool> EstaVivaAsync(string jti, string? ip)
    {
        if (string.IsNullOrWhiteSpace(jti)) return false;

        if (_cache.TryGetValue<bool>(CacheKey(jti), out var cacheada))
            return cacheada;

        var s = await _db.UserSessions.FirstOrDefaultAsync(x => x.Jti == jti);
        var viva = s is not null && s.CerradaAt == null && s.ExpiraAt > DateTime.UtcNow;

        if (viva && s is not null)
        {
            // Pasamos por la base una vez por minuto como mucho: es el lugar barato para anotar
            // "sigue vivo" sin escribir en cada clic.
            s.UltimaActividadAt = DateTime.UtcNow;
            if (!string.IsNullOrWhiteSpace(ip)) s.IpUltima = Recortar(ip, 60);
            try { await _db.SaveChangesAsync(); }
            catch (Exception ex) { _log.LogWarning(ex, "No se pudo anotar la actividad de la sesion {Jti}", jti); }
        }

        _cache.Set(CacheKey(jti), viva, TimeSpan.FromSeconds(TtlVivaSeg));
        return viva;
    }

    /// <summary>Saca el pase de la memoria para que el próximo pedido vuelva a mirar la base.</summary>
    private void Olvidar(string jti) => _cache.Remove(CacheKey(jti));

    // ------------------------------------------------------------------
    // Cerrar
    // ------------------------------------------------------------------

    /// <summary>Cierra una sesión puntual. Devuelve false si no existía o ya estaba cerrada.</summary>
    public async Task<bool> CerrarAsync(int id, string porQuien, string motivo)
    {
        var s = await _db.UserSessions.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null || s.CerradaAt != null) return false;
        MarcarCerrada(s, porQuien, motivo);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Cierra la sesión que corresponde a un pase (la usa el botón "Salir").</summary>
    public async Task CerrarPorJtiAsync(string jti, string porQuien, string motivo)
    {
        if (string.IsNullOrWhiteSpace(jti)) return;
        var s = await _db.UserSessions.FirstOrDefaultAsync(x => x.Jti == jti && x.CerradaAt == null);
        if (s is null) return;
        MarcarCerrada(s, porQuien, motivo);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Echa a un usuario de todos lados. Es lo que corre cuando lo ponen Inactivo o le cambian la
    /// clave: hasta hoy esas dos cosas no lo sacaban, seguía trabajando hasta 24 h.
    /// </summary>
    /// <returns>Cuántas sesiones se cerraron.</returns>
    public async Task<int> CerrarTodasDelUsuarioAsync(int userId, string porQuien, string motivo, string? exceptoJti = null)
    {
        var vivas = await _db.UserSessions
            .Where(s => s.UserId == userId && s.CerradaAt == null)
            .ToListAsync();

        var n = 0;
        foreach (var s in vivas)
        {
            if (exceptoJti is not null && s.Jti == exceptoJti) continue;
            MarcarCerrada(s, porQuien, motivo);
            n++;
        }
        if (n > 0) await _db.SaveChangesAsync();
        return n;
    }

    private void MarcarCerrada(UserSession s, string porQuien, string motivo)
    {
        s.CerradaAt = DateTime.UtcNow;
        s.CerradaPor = Recortar(porQuien, 100);
        s.CerradaMotivo = Recortar(motivo, 120);
        Olvidar(s.Jti);   // esto es lo que hace que el corte sea de segundos y no de un minuto
    }

    // ------------------------------------------------------------------
    // Traducir el aparato a cristiano
    // ------------------------------------------------------------------

    /// <summary>
    /// De lo que dice el navegador ("Mozilla/5.0 (Windows NT 10.0…) Chrome/128.0…") a algo que se
    /// pueda leer: "Chrome en Windows".
    ///
    /// ⚠ El navegador NUNCA dice el nombre del aparato ("iPhone de Osmar"). Eso no existe en la web
    /// y no hay forma de sacarlo; por eso está el apodo a mano.
    ///
    /// Devuelve dos cosas: lo que se muestra, y la "huella" (lo mismo, pero sin versión) que sirve
    /// para reconocer el MISMO aparato aunque Chrome se actualice.
    /// </summary>
    public static (string Dispositivo, string Huella) DescribirDispositivo(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua)) return ("Aparato desconocido", "desconocido");

        var u = ua;
        // El orden importa: Edge y Opera se hacen pasar por Chrome, y Chrome por Safari.
        string nav =
            u.Contains("Edg/") || u.Contains("EdgA/") ? "Edge" :
            u.Contains("OPR/") || u.Contains("Opera") ? "Opera" :
            u.Contains("SamsungBrowser") ? "Samsung Internet" :
            u.Contains("Firefox") || u.Contains("FxiOS") ? "Firefox" :
            u.Contains("CriOS") ? "Chrome" :
            u.Contains("Chrome") ? "Chrome" :
            u.Contains("Safari") ? "Safari" :
            "Navegador";

        string so =
            u.Contains("iPhone") ? "iPhone" :
            u.Contains("iPad") ? "iPad" :
            u.Contains("Android") ? "Android" :
            u.Contains("Windows") ? "Windows" :
            u.Contains("Macintosh") || u.Contains("Mac OS X") ? "Mac" :
            u.Contains("CrOS") ? "Chromebook" :
            u.Contains("Linux") ? "Linux" :
            "aparato desconocido";

        var texto = $"{nav} en {so}";
        return (texto, texto);
    }

    // ------------------------------------------------------------------
    // Oficina / Afuera
    // ------------------------------------------------------------------

    /// <summary>Una red conocida: "las conexiones que empiezan con 190.2.3 son la Oficina".</summary>
    public record RedConocida(string Red, string Nombre);

    public async Task<List<RedConocida>> RedesConocidasAsync()
    {
        var s = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == KeyRedesConocidas);
        if (s is null || string.IsNullOrWhiteSpace(s.Value)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<RedConocida>>(s.Value) ?? new();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Las redes conocidas quedaron mal guardadas, se ignoran");
            return new();
        }
    }

    public async Task GuardarRedesConocidasAsync(List<RedConocida> redes)
    {
        var limpias = redes
            .Where(r => !string.IsNullOrWhiteSpace(r.Red) && !string.IsNullOrWhiteSpace(r.Nombre))
            .Select(r => new RedConocida(r.Red.Trim(), r.Nombre.Trim()))
            .ToList();

        var json = JsonSerializer.Serialize(limpias);
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == KeyRedesConocidas);
        if (s is null)
            _db.AppSettings.Add(new AppSetting { Key = KeyRedesConocidas, Value = json, UpdatedAt = DateTime.UtcNow });
        else { s.Value = json; s.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// "Oficina", "Depósito" o "Afuera", según la IP desde la que se conectó.
    ///
    /// Es a propósito que NO diga la ciudad: la ubicación por IP en celulares con datos móviles cae
    /// casi siempre en Buenos Aires, aunque la persona esté en otra provincia. Decir "Afuera" es
    /// menos vistoso pero es cierto.
    /// </summary>
    public static string Lugar(string? ip, List<RedConocida> redes)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "Sin dato";
        foreach (var r in redes)
            if (ip.StartsWith(r.Red, StringComparison.OrdinalIgnoreCase))
                return r.Nombre;
        return "Afuera";
    }

    /// <summary>La IP real del que pide. Detrás de Caddy/Nginx viene en X-Forwarded-For, y eso ya lo
    /// resuelve UseForwardedHeaders en Program.cs, así que acá alcanza con RemoteIpAddress.</summary>
    public static string? IpDe(HttpContext? http)
    {
        var ip = http?.Connection.RemoteIpAddress?.ToString();
        // ::ffff:10.0.0.5 → 10.0.0.5, para que las redes conocidas se puedan escribir normales.
        if (ip is not null && ip.StartsWith("::ffff:")) ip = ip[7..];
        return ip;
    }

    public static string? UserAgentDe(HttpContext? http)
        => http?.Request.Headers.UserAgent.ToString() is { Length: > 0 } ua ? ua : null;

    private static string? Recortar(string? s, int max)
        => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..max]);
}
