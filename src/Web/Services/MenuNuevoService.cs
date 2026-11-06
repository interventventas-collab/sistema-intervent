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

    public async Task VolverAlOriginalAsync()
    {
        await _api.ResetMenuConfigAsync();
        _guardado = null;
        Actual = MenuNuevoCatalogo.Armar(null);
        OnChange?.Invoke();
    }
}
