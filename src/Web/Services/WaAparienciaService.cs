namespace Web.Services;

/// <summary>
/// 2026-08-15: cómo se VE el WhatsApp. Dos cosas:
///  1) TEMA por línea: cada número (FRIKAF, FIJO TRANSRADIO, …) puede verse claro u oscuro.
///     Sirve para saber de un vistazo con qué línea estás escribiendo. Se guarda en
///     WhatsApp_LineasConfig.Tema (por línea, lo ve todo el mundo igual).
///  2) LETRA: la tipografía de la pantalla de WhatsApp. Es una sola para todo el sistema,
///     guardada en AppSettings con la clave "whatsapp.fuente".
///
/// Lo usan las tres pantallas: la grande (/whatsapp), la del celu (/whatsapp-movil) y el
/// chat flotante (PinnedWaChat). Cada una pide las clases CSS con ClasesPara(lineaId) y
/// las pega en su caja; el CSS vive en wwwroot/css/whatsapp-tema.css.
/// </summary>
public class WaAparienciaService
{
    public const string SettingFuente = "whatsapp.fuente";

    private readonly ApiClient _api;
    private Dictionary<string, string> _temas = new(StringComparer.OrdinalIgnoreCase);
    private string _fuente = "sistema";
    private bool _cargado;
    private Task? _cargando;

    public WaAparienciaService(ApiClient api) => _api = api;

    /// <summary>Avisa a las pantallas abiertas que cambió el tema o la letra, para que se repinten.</summary>
    public event Action? OnChange;

    /// <summary>Letra elegida (clave de FuentesDisponibles).</summary>
    public string Fuente => _fuente;

    /// <summary>Las letras que se pueden elegir. La clave va al CSS como .wa-font-{clave}.</summary>
    public static readonly (string Key, string Label, string Ejemplo)[] FuentesDisponibles = new[]
    {
        ("sistema",    "Normal (la de siempre)",    "La letra que usa todo el sistema."),
        ("redonda",    "Redondeada",                "Más suave y redondita."),
        ("ancha",      "Bien legible",              "Letras separadas, fáciles de leer."),
        ("clasica",    "Clásica (con serifas)",     "Como la de los diarios."),
        ("maquina",    "Máquina de escribir",       "Todas las letras del mismo ancho."),
        ("manuscrita", "Manuscrita",                "Estilo escrito a mano."),
    };

    /// <summary>Carga tema de cada línea + letra elegida. Se hace una sola vez por sesión.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_cargado) return;
        _cargando ??= CargarAsync();
        await _cargando;
    }

    private async Task CargarAsync()
    {
        try
        {
            var lineas = await _api.GetTwLineasConfigAsync();
            _temas = lineas
                .Where(l => !string.IsNullOrEmpty(l.LineaId))
                .ToDictionary(l => l.LineaId, l => l.Tema ?? "claro", StringComparer.OrdinalIgnoreCase);
        }
        catch { /* si falla, todo claro: no rompemos la pantalla por un color */ }

        try
        {
            var f = await _api.GetSettingAsync(SettingFuente);
            if (!string.IsNullOrWhiteSpace(f)) _fuente = f.Trim();
        }
        catch { }

        _cargado = true;
        _cargando = null;
    }

    /// <summary>Vuelve a leer todo del servidor (después de guardar un cambio).</summary>
    public async Task RecargarAsync()
    {
        _cargado = false;
        _cargando = null;
        await EnsureLoadedAsync();
        OnChange?.Invoke();
    }

    public bool EsOscura(string? lineaId)
        => !string.IsNullOrEmpty(lineaId)
           && _temas.TryGetValue(lineaId!, out var t)
           && string.Equals(t, "oscuro", StringComparison.OrdinalIgnoreCase);

    public string TemaDe(string? lineaId) => EsOscura(lineaId) ? "oscuro" : "claro";

    /// <summary>Guarda el tema de una línea (sin pisar nombre/imagen: eso lo maneja el otro modal).</summary>
    public async Task<bool> GuardarTemaAsync(string lineaId, string tema, string? nombre, string? sonido)
    {
        var (ok, _) = await _api.GuardarTwLineaConfigAsync(lineaId, nombre, null, sonido, tema);
        if (ok)
        {
            _temas[lineaId] = tema;
            OnChange?.Invoke();
        }
        return ok;
    }

    public async Task<bool> GuardarFuenteAsync(string fuente)
    {
        var ok = await _api.UpdateSettingAsync(SettingFuente, fuente);
        if (ok)
        {
            _fuente = fuente;
            OnChange?.Invoke();
        }
        return ok;
    }

    /// <summary>
    /// Clases CSS para la caja del WhatsApp: el tema de esa línea + la letra elegida.
    /// lineaId null o "todas las líneas" => claro (no sabemos con cuál está trabajando).
    /// </summary>
    public string ClasesPara(string? lineaId)
    {
        var clases = $"wa-font-{(string.IsNullOrWhiteSpace(_fuente) ? "sistema" : _fuente)}";
        if (EsOscura(lineaId)) clases = "wa-dark " + clases;
        return clases;
    }
}
