using System.IO.Compression;
using MaxMind.GeoIP2;

namespace Api.Services;

/// <summary>
/// 2026-09-17 — De un número de conexión a "Rosario, Santa Fe".
///
/// QUÉ PUEDE Y QUÉ NO. Esto NO es un GPS y no dice el barrio: eso no se puede sacar de una conexión
/// a internet, ni acá ni en ningún lado. Lo que da:
///   • Conexión fija (oficina, depósito, una casa): suele acertar la ciudad.
///   • Celular con datos: poco confiable — la conexión de Claro o Personal suele figurar en Buenos
///     Aires aunque la persona esté en Córdoba. Por eso en pantalla va siempre como aproximada.
///
/// El dato exacto sigue siendo el de las redes conocidas que carga el dueño ("Oficina", "Depósito").
/// La ciudad es un complemento para lo que cae como "Afuera".
///
/// POR QUÉ UNA BASE PROPIA Y NO UN SERVICIO DE INTERNET. Era una decisión del dueño (17/09): con un
/// servicio, cada vez que alguien entra le mandaríamos su número de conexión a una empresa de
/// afuera. Con la base acá adentro, no sale ningún dato del servidor.
///
/// La base es la gratuita de DB-IP (CC BY 4.0 — por eso la pantalla dice de dónde sale). Se renueva
/// todos los meses; <see cref="GeoIpDownloader"/> la baja sola cuando se pone vieja.
/// </summary>
public class GeoIpService
{
    public const string Carpeta = "/data/geoip";
    public static readonly string ArchivoPath = Path.Combine(Carpeta, "ciudades.mmdb");

    private readonly ILogger<GeoIpService> _log;
    private readonly object _candado = new();
    private DatabaseReader? _lector;
    private DateTime _cargadoDe;

    public GeoIpService(ILogger<GeoIpService> log) => _log = log;

    /// <summary>¿Hay base cargada? Si no, la pantalla simplemente no muestra ciudad.</summary>
    public bool Disponible => Abrir() is not null;

    /// <summary>De cuándo es la base que tenemos (para poder mostrarlo y decidir si renovarla).</summary>
    public DateTime? FechaBase
    {
        get
        {
            var fi = new FileInfo(ArchivoPath);
            return fi.Exists ? fi.LastWriteTimeUtc : null;
        }
    }

    /// <summary>
    /// "Rosario, Santa Fe" · "Madrid, España" · null si no se pudo, o si es una IP interna.
    /// Nunca tira excepción: si algo falla, no hay ciudad y listo — no se rompe la pantalla.
    /// </summary>
    public string? Ciudad(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return null;
        if (EsInterna(ip)) return null;

        var lector = Abrir();
        if (lector is null) return null;

        try
        {
            if (!lector.TryCity(ip, out var r) || r is null) return null;

            var ciudad = r.City?.Name;
            var provincia = r.MostSpecificSubdivision?.Name;
            var pais = r.Country?.Name;
            var esArgentina = string.Equals(r.Country?.IsoCode, "AR", StringComparison.OrdinalIgnoreCase);

            // De lo más preciso a lo menos, sin repetir ("Madrid, Madrid" no aporta nada).
            //
            // ⚠ EL PAÍS SE MUESTRA SIEMPRE QUE NO SEA ARGENTINA, aunque haya ciudad y provincia.
            // Antes se recortaba a dos partes y una conexión de España aparecía como
            // "O Carballiño, Galicia" — que leído rápido pasa por un pueblo argentino. Justo en una
            // pantalla de seguridad, esconder el país es lo peor que se puede hacer.
            var partes = new List<string>();
            void Sumar(string? x)
            {
                if (string.IsNullOrWhiteSpace(x)) return;
                if (partes.Any(p => string.Equals(p, x, StringComparison.OrdinalIgnoreCase))) return;
                partes.Add(x);
            }

            Sumar(ciudad);
            Sumar(provincia);
            if (!esArgentina) Sumar(pais);

            return partes.Count == 0 ? null : string.Join(", ", partes);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "No se pudo ubicar la IP {Ip}", ip);
            return null;
        }
    }

    /// <summary>Las IPs de adentro (Docker, la red de la oficina) no están en ninguna base de
    /// ciudades: preguntarlas es perder el tiempo y llena el log de ruido.</summary>
    public static bool EsInterna(string ip)
        => ip.StartsWith("10.") || ip.StartsWith("192.168.") || ip.StartsWith("127.")
           || ip.StartsWith("::1") || ip.StartsWith("169.254.")
           || (ip.StartsWith("172.") && int.TryParse(ip.Split('.').Skip(1).FirstOrDefault(), out var b)
               && b >= 16 && b <= 31);

    /// <summary>Abre la base, y la vuelve a abrir sola si el archivo cambió (renovación mensual).</summary>
    private DatabaseReader? Abrir()
    {
        var fi = new FileInfo(ArchivoPath);
        if (!fi.Exists) return null;

        lock (_candado)
        {
            if (_lector is not null && _cargadoDe == fi.LastWriteTimeUtc) return _lector;

            try
            {
                // Los nombres, en castellano si la base los trae; si no, en ingles.
                var nuevo = new DatabaseReader(ArchivoPath, new[] { "es", "en" });
                _lector?.Dispose();
                _lector = nuevo;
                _cargadoDe = fi.LastWriteTimeUtc;
                _log.LogInformation("Base de ciudades cargada ({Fecha:yyyy-MM-dd})", fi.LastWriteTimeUtc);
                return _lector;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "La base de ciudades no se pudo abrir; se sigue sin ciudad");
                return null;
            }
        }
    }

    /// <summary>Deja el archivo nuevo en su lugar y hace que la próxima consulta lo tome.</summary>
    public void Recargar() => Abrir();
}

/// <summary>
/// 2026-09-17 — Baja la base de ciudades y la renueva sola una vez por mes.
///
/// Corre aparte del arranque a propósito: si DB-IP está caído o no hay internet, el sistema tiene
/// que levantar igual. Sin base, la pantalla no muestra ciudad y nada más.
/// </summary>
public class GeoIpDownloader : BackgroundService
{
    /// <summary>A los cuántos días se considera vieja. El archivo sale una vez por mes.</summary>
    private const int DiasParaRenovar = 35;

    private readonly GeoIpService _geo;
    private readonly IHttpClientFactory _http;
    private readonly ILogger<GeoIpDownloader> _log;

    public GeoIpDownloader(GeoIpService geo, IHttpClientFactory http, ILogger<GeoIpDownloader> log)
    {
        _geo = geo; _http = http; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stop)
    {
        // Dejar que el sistema termine de arrancar antes de ponerse a bajar 57 MB.
        try { await Task.Delay(TimeSpan.FromMinutes(2), stop); } catch { return; }

        while (!stop.IsCancellationRequested)
        {
            try { await RenovarSiHaceFaltaAsync(stop); }
            catch (Exception ex) { _log.LogWarning(ex, "No se pudo renovar la base de ciudades"); }

            try { await Task.Delay(TimeSpan.FromDays(1), stop); } catch { return; }
        }
    }

    private async Task RenovarSiHaceFaltaAsync(CancellationToken stop)
    {
        var fecha = _geo.FechaBase;
        if (fecha is not null && (DateTime.UtcNow - fecha.Value).TotalDays < DiasParaRenovar) return;

        Directory.CreateDirectory(GeoIpService.Carpeta);

        // El archivo sale por mes. Si el de este mes todavía no está publicado, se prueba el anterior.
        var hoy = DateTime.UtcNow;
        foreach (var mes in new[] { hoy, hoy.AddMonths(-1), hoy.AddMonths(-2) })
        {
            var url = $"https://download.db-ip.com/free/dbip-city-lite-{mes:yyyy-MM}.mmdb.gz";
            if (await BajarAsync(url, stop)) return;
        }

        _log.LogWarning("No se pudo bajar ninguna version de la base de ciudades");
    }

    private async Task<bool> BajarAsync(string url, CancellationToken stop)
    {
        // Se baja a un archivo temporal y recién al final se pone en su lugar: si se corta a la
        // mitad, la base que ya había sigue funcionando en vez de quedar un archivo roto.
        var tmp = GeoIpService.ArchivoPath + ".bajando";

        try
        {
            var cli = _http.CreateClient();
            cli.Timeout = TimeSpan.FromMinutes(10);

            using var resp = await cli.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stop);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("La base {Url} no esta disponible ({Code})", url, (int)resp.StatusCode);
                return false;
            }

            await using (var origen = await resp.Content.ReadAsStreamAsync(stop))
            await using (var gz = new GZipStream(origen, CompressionMode.Decompress))
            await using (var destino = File.Create(tmp))
            {
                await gz.CopyToAsync(destino, stop);
            }

            // Sanidad: una base de ciudades pesa cientos de MB descomprimida. Si vino algo diminuto,
            // es una pagina de error disfrazada y no hay que pisarle la base buena a nadie.
            if (new FileInfo(tmp).Length < 5_000_000)
            {
                File.Delete(tmp);
                _log.LogWarning("La base bajada de {Url} vino demasiado chica, se descarta", url);
                return false;
            }

            File.Move(tmp, GeoIpService.ArchivoPath, overwrite: true);
            _geo.Recargar();
            _log.LogInformation("Base de ciudades actualizada desde {Url}", url);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Fallo bajando {Url}", url);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            return false;
        }
    }
}
