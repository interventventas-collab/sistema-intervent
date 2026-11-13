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
        => Actual.Favoritos?.Keys.Where(k => k != MenuNuevoCatalogo.FAV_BASE && !k.StartsWith(MenuNuevoCatalogo.GRANDES) && !k.StartsWith(ATAJOS) && !k.StartsWith(TIRA) && !k.StartsWith(OCULTOS) && !k.StartsWith(CHCOL)).OrderBy(k => k).ToList() ?? new();

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

    /// <summary>2026-11-11: cada lugar se guarda como "principal|sub1|sub2…" (hasta 6 subbotones).</summary>
    public static (string Principal, List<string> Subs) Partir(string? lugar)
    {
        var p = (lugar ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        return p.Length == 0 ? ("", new()) : (p[0], p.Skip(1).Take(MenuNuevoCatalogo.MAX_SUBS).ToList());
    }
    public static string Juntar(string principal, IEnumerable<string> subs)
        => string.IsNullOrEmpty(principal) ? "" : string.Join("|", new[] { principal }.Concat(subs.Take(MenuNuevoCatalogo.MAX_SUBS)));

    /// <summary>2026-11-11: el principal puede llevar un nombre propio: "dirección¦Nombre". Con dirección "*" es
    /// un botón "solo título" (no lleva a ninguna pantalla, agrupa subbotones).</summary>
    public const string SOLO_TITULO = "*";
    public static (string Href, string? Titulo) PartirTitulo(string principal)
    {
        var (h, t, _) = PartirPrincipal(principal);
        return (h, t);
    }
    /// <summary>2026-11-11: "dirección¦Nombre¦color" (nombre y color opcionales; color = clave de pastel).</summary>
    public static (string Href, string? Titulo, string? Color) PartirPrincipal(string principal)
    {
        var p = principal.Split('¦');
        string? T(int i) => p.Length > i && p[i].Trim() != "" ? p[i].Trim() : null;
        return (p[0], T(1), T(2));
    }
    public static string JuntarTitulo(string href, string? titulo, string? color = null)
    {
        string L(string? x) => (x ?? "").Replace("|", " ").Replace("¦", " ").Trim();
        var t = L(titulo); var c = L(color);
        if (string.IsNullOrEmpty(href)) return "";
        return c != "" ? $"{href}¦{t}¦{c}" : t != "" ? $"{href}¦{t}" : href;
    }

    /// <summary>Pantallas que esta persona tiene en sus botones grandes (principales y subbotones):
    /// esas no se repiten en la barra de arriba.</summary>
    public HashSet<string> EnGrandes(string? op)
        => GrandesDe(op).SelectMany(l => { var (m, s) = Partir(l); return new[] { PartirTitulo(m).Href }.Concat(s); })
                        .Where(h => h != "").ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Numeritos (ventas sin entregar, cobros para volcar, para armar): los calcula la barra de arriba y los
    // muestran también los botones grandes.
    public Dictionary<string, int> Numeros { get; } = new(StringComparer.OrdinalIgnoreCase);
    public event Action? OnNumeros;
    public void AvisarNumeros() => OnNumeros?.Invoke();

    // El + de "Nueva venta / cobranza / pago" de un botón grande lo resuelve MainLayout (igual que el de arriba).
    public event Action<string>? OnNuevo;
    public void PedirNuevo(string tipo) => OnNuevo?.Invoke(tipo);

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
    /// <summary>2026-11-11: cambia de lugar dos botones grandes (arrastrando o con "Mover a…").</summary>
    public async Task IntercambiarGrandesAsync(string? op, int a, int b)
    {
        await RecargarAsync();
        var l = GrandesDe(op);
        if (a < 0 || b < 0 || a >= l.Count || b >= l.Count || a == b) return;
        (l[a], l[b]) = (l[b], l[a]);
        var fav = Actual.Favoritos?.ToDictionary(k => k.Key, k => k.Value.ToList()) ?? new Dictionary<string, List<string>>();
        fav[ClaveGrandes(op)] = l;
        await GuardarAsync(Actual with { Favoritos = fav });
    }
    // ───────── Botones propios arriba de cada pantalla (2026-11-11, empezó por Ventas) ─────────
    // Clave "ATAJOS:<pantalla>|<persona>" ("|" solo = los de Osmar, que ven todos los que no armaron los suyos).
    // Cada elemento es una pantalla ("/cafe/clientes") o una acción "+venta", "+cobranza"…
    public const string ATAJOS = "ATAJOS:";
    private static string ClaveAtajos(string pantalla, string? op)
    {
        var k = Clave(op);
        return ATAJOS + pantalla + "|" + (k == MenuNuevoCatalogo.FAV_DUENO ? MenuNuevoCatalogo.FAV_BASE : k);
    }
    public List<string> AtajosDe(string pantalla, string? op, IEnumerable<string> fabrica)
    {
        var f = Actual.Favoritos;
        if (f != null && f.TryGetValue(ClaveAtajos(pantalla, op), out var l)) return l.ToList();
        if (f != null && f.TryGetValue(ClaveAtajos(pantalla, null), out var b)) return b.ToList();
        return fabrica.ToList();
    }
    public bool TieneAtajosPropios(string pantalla, string? op)
        => ClaveAtajos(pantalla, op) != ClaveAtajos(pantalla, null) && Actual.Favoritos?.ContainsKey(ClaveAtajos(pantalla, op)) == true;
    public async Task GuardarAtajosAsync(string pantalla, string? op, List<string> lista)
    {
        await RecargarAsync();
        var fav = Actual.Favoritos?.ToDictionary(k => k.Key, k => k.Value.ToList()) ?? new Dictionary<string, List<string>>();
        fav[ClaveAtajos(pantalla, op)] = lista.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await GuardarAsync(Actual with { Favoritos = fav });
    }
    public async Task VolverAtajosAOsmarAsync(string pantalla, string? op)
    {
        await RecargarAsync();
        var k = ClaveAtajos(pantalla, op);
        if (k == ClaveAtajos(pantalla, null)) return;
        var fav = Actual.Favoritos?.ToDictionary(x => x.Key, x => x.Value.ToList()) ?? new Dictionary<string, List<string>>();
        fav.Remove(k);
        await GuardarAsync(Actual with { Favoritos = fav });
    }

    // ───────── Inicio: tira ⭐ Favoritos, botones ocultos y color de cada botón (2026-11-13) ─────────
    // TODO es de cada persona (pedido del dueño: "que los favoritos los elija cada uno"); sin PIN = los de Osmar.
    // Van en el diccionario Favoritos: "TIRA:<persona>" (pantallas en orden), "OCULTOS:<persona>" (pantallas)
    // y "CHCOL:<persona>" (elementos "pantalla¦color").
    public const string TIRA = "TIRA:", OCULTOS = "OCULTOS:", CHCOL = "CHCOL:";
    private static string ClavePersona(string prefijo, string? op)
    {
        var k = Clave(op);
        return prefijo + (k == MenuNuevoCatalogo.FAV_DUENO ? MenuNuevoCatalogo.FAV_BASE : k);
    }
    private List<string> ListaDe(string prefijo, string? op)
        => Actual.Favoritos != null && Actual.Favoritos.TryGetValue(ClavePersona(prefijo, op), out var l) ? l.ToList() : new();
    public List<string> TiraDe(string? op) => ListaDe(TIRA, op);
    public HashSet<string> OcultosDe(string? op) => ListaDe(OCULTOS, op).ToHashSet(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ColoresChipDe(string? op)
        => ListaDe(CHCOL, op).Select(x => x.Split('¦')).Where(p => p.Length == 2 && p[1] != "")
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.OrdinalIgnoreCase);

    private async Task GuardarListaAsync(string prefijo, string? op, Func<List<string>, List<string>> cambio)
    {
        await RecargarAsync();
        var fav = Actual.Favoritos?.ToDictionary(k => k.Key, k => k.Value.ToList()) ?? new Dictionary<string, List<string>>();
        var k = ClavePersona(prefijo, op);
        var nueva = cambio(fav.TryGetValue(k, out var l) ? l.ToList() : new());
        if (nueva.Count == 0) fav.Remove(k); else fav[k] = nueva;
        await GuardarAsync(Actual with { Favoritos = fav });
    }
    public Task PonerEnTiraAsync(string? op, string href, bool poner)
        => GuardarListaAsync(TIRA, op, l =>
        {
            l.RemoveAll(x => string.Equals(x, href, StringComparison.OrdinalIgnoreCase));
            if (poner) l.Add(href);
            return l;
        });
    public Task MoverEnTiraAsync(string? op, int desde, int hasta)
        => GuardarListaAsync(TIRA, op, l =>
        {
            if (desde < 0 || desde >= l.Count || hasta < 0 || hasta >= l.Count || desde == hasta) return l;
            var x = l[desde]; l.RemoveAt(desde); l.Insert(hasta, x);
            return l;
        });
    public Task OcultarAsync(string? op, string href, bool ocultar)
        => GuardarListaAsync(OCULTOS, op, l =>
        {
            l.RemoveAll(x => string.Equals(x, href, StringComparison.OrdinalIgnoreCase));
            if (ocultar) l.Add(href);
            return l;
        });
    public Task ColorChipAsync(string? op, string href, string? color)
        => GuardarListaAsync(CHCOL, op, l =>
        {
            l.RemoveAll(x => x.Split('¦')[0].Equals(href, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(color)) l.Add($"{href}¦{color}");
            return l;
        });

    /// <summary>Botón grande que se está arrastrando (puede soltarse en el otro bloque).</summary>
    public int? Arrastrando { get; set; }

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
