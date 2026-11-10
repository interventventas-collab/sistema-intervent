using Web.Shared;

namespace Web.Services;

/// <summary>
/// 2026-11-06: guarda en memoria cómo está acomodado el menú nuevo (lo que el dueño ordenó con
/// "✏ Acomodar"). La barra de arriba y el "Más pantallas" del costado leen de acá, y se repintan
/// juntos cuando cambia (OnChange).
/// </summary>
public class MenuNuevoService
{
    private readonly ApiClient _api;
    private MenuNuevoCatalogo.Orden? _guardado;
    private bool _cargado, _cargando;

    public event Action? OnChange;

    public MenuNuevoService(ApiClient api) { _api = api; }

    /// <summary>El menú como se muestra ahora (orden guardado, o el de fábrica si no hay).</summary>
    public MenuNuevoCatalogo.Armado Actual { get; private set; } = MenuNuevoCatalogo.Armar(null);

    public bool TieneOrdenPropio => _guardado != null;

    public async Task EnsureLoadedAsync()
    {
        if (_cargado || _cargando) return;
        _cargando = true;
        try
        {
            _guardado = await _api.GetMenuConfigAsync();
            Actual = MenuNuevoCatalogo.Armar(_guardado);
            _cargado = true;
            OnChange?.Invoke();
        }
        finally { _cargando = false; }
    }

    public async Task GuardarAsync(MenuNuevoCatalogo.Armado nuevo)
    {
        var orden = MenuNuevoCatalogo.AOrden(nuevo);
        await _api.SaveMenuConfigAsync(orden);
        _guardado = orden;
        Actual = MenuNuevoCatalogo.Armar(orden);
        OnChange?.Invoke();
    }

    /// <summary>2026-11-10: lápiz ✏ de un botón: cambia solo su ícono y su color, y guarda.</summary>
    public async Task GuardarIconoColorAsync(string clave, string? icono, string? color)
    {
        var a = Actual;
        var colores = new Dictionary<string, string>(a.Colores, StringComparer.OrdinalIgnoreCase);
        var iconos = new Dictionary<string, string>(a.Iconos ?? new(), StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(icono)) iconos.Remove(clave); else iconos[clave] = icono;
        if (string.IsNullOrEmpty(color)) colores.Remove(clave); else colores[clave] = color;
        await GuardarAsync(a with { Colores = colores, Iconos = iconos });
    }

    /// <summary>2026-11-10: orden y colores de las pastillas de la derecha del Dashboard.
    /// Los colores van en el mismo diccionario, con la clave "pas:" + pastilla.</summary>
    public async Task GuardarPastillasAsync(List<string> orden, Dictionary<string, string?> colores)
    {
        var a = Actual;
        var col = new Dictionary<string, string>(a.Colores, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in colores)
        {
            if (string.IsNullOrEmpty(v)) col.Remove("pas:" + k); else col["pas:" + k] = v;
        }
        await GuardarAsync(a with { Colores = col, Pastillas = orden });
    }

    public async Task VolverAlOriginalAsync()
    {
        await _api.ResetMenuConfigAsync();
        _guardado = null;
        Actual = MenuNuevoCatalogo.Armar(null);
        OnChange?.Invoke();
    }
}
