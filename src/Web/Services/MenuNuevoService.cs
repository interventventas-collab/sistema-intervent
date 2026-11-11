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
        await RecargarAsync();
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
        await RecargarAsync();
        var a = Actual;
        var col = new Dictionary<string, string>(a.Colores, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in colores)
        {
            if (string.IsNullOrEmpty(v)) col.Remove("pas:" + k); else col["pas:" + k] = v;
        }
        await GuardarAsync(a with { Colores = col, Pastillas = orden });
    }

    // ───────── Favoritos por persona (2026-11-10) ─────────
    // En Favoritos: clave "" = lista base que arma el dueño; clave "OSMAR"/"GABRIEL"/… = la de cada uno.
    private static string Clave(string? op) => string.IsNullOrWhiteSpace(op) ? MenuNuevoCatalogo.FAV_BASE : op.Trim().ToUpperInvariant();

    public List<string> FavoritosBase()
        => Actual.Favoritos != null && Actual.Favoritos.TryGetValue(MenuNuevoCatalogo.FAV_BASE, out var b) ? b.ToList()
           : MenuNuevoCatalogo.FavoritosDeFabrica.ToList();

    /// <summary>Los favoritos que ve esta persona: los suyos si los cambió, si no la lista base.</summary>
    public List<string> FavoritosDe(string? op)
    {
        var k = Clave(op);
        if (k != MenuNuevoCatalogo.FAV_BASE && Actual.Favoritos != null && Actual.Favoritos.TryGetValue(k, out var l)) return l.ToList();
        return FavoritosBase();
    }
    public bool TienePropios(string? op)
        => Clave(op) != MenuNuevoCatalogo.FAV_BASE && Actual.Favoritos?.ContainsKey(Clave(op)) == true;
    public List<string> PersonasConFavoritos()
        => Actual.Favoritos?.Keys.Where(k => k != MenuNuevoCatalogo.FAV_BASE && !k.StartsWith(MenuNuevoCatalogo.GRANDES)).OrderBy(k => k).ToList() ?? new();

    // ───────── 8 favoritos grandes del Dashboard (2026-11-11) ─────────
    // Van en el mismo diccionario Favoritos con clave "GRANDES:" + persona ("GRANDES:" solo = los de Osmar, que ven todos
    // los que no armaron los suyos). Siempre 8 lugares; "" = lugar vacío (cuadro con +).
    private static string ClaveGrandes(string? op)
    {
        var k = Clave(op);
        return MenuNuevoCatalogo.GRANDES + (k == MenuNuevoCatalogo.FAV_DUENO ? MenuNuevoCatalogo.FAV_BASE : k);
    }
    private static List<string> A8(IEnumerable<string>? l)
    {
        var r = (l ?? Enumerable.Empty<string>()).Take(MenuNuevoCatalogo.CANT_GRANDES).ToList();
        while (r.Count < MenuNuevoCatalogo.CANT_GRANDES) r.Add("");
        return r;
    }
    public List<string> GrandesDe(string? op)
    {
        var f = Actual.Favoritos;
        if (f != null && f.TryGetValue(ClaveGrandes(op), out var l)) return A8(l);
        if (f != null && f.TryGetValue(MenuNuevoCatalogo.GRANDES, out var b)) return A8(b);
        return A8(null);
    }
    public bool TieneGrandesPropios(string? op)
        => ClaveGrandes(op) != MenuNuevoCatalogo.GRANDES && Actual.Favoritos?.ContainsKey(ClaveGrandes(op)) == true;

    /// <summary>Pone (o vacía, href "") el lugar n de los 8 grandes de esta persona. Osmar (o sin PIN) cambia los de todos.</summary>
    public async Task GuardarGrandeAsync(string? op, int n, string href)
    {
        await RecargarAsync();
        var l = GrandesDe(op);
        if (n < 0 || n >= l.Count) return;
        l[n] = href ?? "";
        var fav = Actual.Favoritos?.ToDictionary(k => k.Key, k => k.Value.ToList()) ?? new Dictionary<string, List<string>>();
        fav[ClaveGrandes(op)] = l;
        await GuardarAsync(Actual with { Favoritos = fav });
    }
    public async Task VolverGrandesAOsmarAsync(string? op)
    {
        await RecargarAsync();
        var fav = Actual.Favoritos?.ToDictionary(k => k.Key, k => k.Value.ToList()) ?? new Dictionary<string, List<string>>();
        var k = ClaveGrandes(op);
        if (k == MenuNuevoCatalogo.GRANDES) return;
        fav.Remove(k);
        await GuardarAsync(Actual with { Favoritos = fav });
    }

    public string EstiloFav(string? op)
        => Actual.FavEstilo != null && (Actual.FavEstilo.TryGetValue(Clave(op), out var e) || Actual.FavEstilo.TryGetValue(MenuNuevoCatalogo.FAV_BASE, out e)) ? e : "texto";

    /// <summary>Antes de guardar se vuelve a leer lo guardado: así no se pisan los cambios que otro hizo desde otra compu.</summary>
    private async Task RecargarAsync()
    {
        try
        {
            _guardado = await _api.GetMenuConfigAsync();
            Actual = MenuNuevoCatalogo.Armar(_guardado);
        }
        catch { }
    }

    /// <summary>Guarda la lista de favoritos de una persona (op) o la base (op = null). lista null = borra la propia (vuelve a la base).</summary>
    public async Task GuardarFavoritosAsync(string? op, List<string>? lista)
    {
        await RecargarAsync();
        var fav = Actual.Favoritos?.ToDictionary(k => k.Key, k => k.Value.ToList()) ?? new Dictionary<string, List<string>>();
        var k = Clave(op);
        if (lista == null) fav.Remove(k); else fav[k] = lista.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await GuardarAsync(Actual with { Favoritos = fav });
    }

    public async Task AlternarFavoritoAsync(string? op, string href)
    {
        var l = FavoritosDe(op);
        if (l.Any(x => string.Equals(x, href, StringComparison.OrdinalIgnoreCase))) l.RemoveAll(x => string.Equals(x, href, StringComparison.OrdinalIgnoreCase));
        else l.Add(href);
        // Sin operador elegido, lo que toca es la lista base.
        await GuardarFavoritosAsync(op, l);
    }

    public async Task GuardarEstiloFavAsync(string? op, string estilo)
    {
        await RecargarAsync();
        var est = Actual.FavEstilo == null ? new Dictionary<string, string>() : new Dictionary<string, string>(Actual.FavEstilo);
        est[Clave(op)] = estilo;
        await GuardarAsync(Actual with { FavEstilo = est });
    }

    public async Task VolverAlOriginalAsync()
    {
        await _api.ResetMenuConfigAsync();
        _guardado = null;
        Actual = MenuNuevoCatalogo.Armar(null);
        OnChange?.Invoke();
    }
}
