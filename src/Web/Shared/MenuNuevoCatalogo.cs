namespace Web.Shared;

/// <summary>
/// 2026-11-06: menú nuevo ordenado según las 5 partes del negocio (pedido del dueño):
/// Ventas por fuera · Frikaf · MercadoLibre · Intereventos · Logística, y aparte lo que
/// comparten todas (Stock y compras, Productos y precios, Plata, Empleados).
/// Por ahora lo ve SOLO el usuario admin (MainLayout.MenuNuevo); los demás siguen con el menú
/// de siempre. Cada pantalla está en UN solo lugar: lo que está acá no se repite en el costado.
/// </summary>
public static class MenuNuevoCatalogo
{
    /// <summary>Un renglón del menú. Seccion != null = título chiquito antes del renglón.
    /// Permiso null = lo ve cualquiera que vea el menú. Externo = abre la página entera (mapa).
    /// Nuevo = con botón + pegado que carga uno nuevo (venta, cobranza, alquiler, pago).
    /// SoloCelu = en compu ya está a la vista en la barra, así que no se repite en el cajón.</summary>
    public record Item(string Label, string Href, string? Permiso = null, string? Seccion = null, bool Externo = false,
                       string? Nuevo = null, bool SoloCelu = false);

    /// <summary>Nuevo = el desplegable lleva un + pegado que carga uno nuevo (alquiler, stock, compra).</summary>
    public record Grupo(string Titulo, string Subtitulo, bool DelNegocio, Item[] Items, string? Nuevo = null);

    /// <summary>Botones directos de todos los días (un clic, sin abrir nada).</summary>
    public static readonly Item[] Directos =
    {
        // 2026-11-10: la barra de arriba queda con estos 5 solos, en este orden (pedido del dueño).
        new("Preparación", "/cafe/preparacion"),
        new("Ventas", "/cafe/ventas", Nuevo: "venta"),
        new("Cobranzas", "/cafe/tesoreria/cobranzas", Nuevo: "cobranza"),
        new("Pagos", "/cafe/tesoreria/pagos", Nuevo: "pago"),
        new("MAPEO", "/mapeo?full=1", Externo: true),
    };

    /// <summary>Tienen su + en el desplegable (Stock, Intereventos): no vuelven como botón suelto,
    /// aunque el orden guardado en la base todavía los traiga.</summary>
    public static readonly HashSet<string> NoDirectos = new(StringComparer.OrdinalIgnoreCase)
    {
        "/cafe/stock-masivo",
        "/alquileres/reservas",
    };

    /// <summary>"Clientes" antes se llamaba así: lo acomodado y su color siguen guardados con ese nombre.</summary>
    private const string TITULO_VIEJO_CLIENTES = "Ventas por fuera";

    public static readonly Grupo[] Grupos =
    {
        new("Clientes", "WhatsApp, teléfono, en persona", true, new Item[]
        {
            new("Ventas", "/cafe/ventas"),
            new("Cobranzas", "/cafe/tesoreria/cobranzas"),
            new("¿Quién me debe?", "/cafe/clientes/panel-deudas"),
            new("Presupuestos", "/cafe/presupuestos"),
            new("Pedidos de Gaby", "/cafe/preventas"),
            new("Pedidos de WhatsApp", "/cafe/pedidos-whatsapp"),
            new("Visitas", "/visitas"),
            new("Clientes", "/cafe/clientes"),
            new("Precios y descuentos especiales", "/cafe/precios-especiales"),
        }),
        new("Frikaf", "solo café", true, new Item[]
        {
            new("Kilos de café vendidos", "/panorama?ver=kg"),
            new("Máquinas (comodato y financiación)", "/cafe/maquinas"),
            new("Fichas técnicas de café", "/cafe/fichas-tecnicas"),
            new("Post-venta café (molienda)", "/meli/postventa-cafe", "publicaciones"),
        }),
        new("MercadoLibre", "las 2 cuentas", true, new Item[]
        {
            new("Publicaciones", "/publicaciones-nueva", "publicaciones"),
            new("Órdenes", "/ordenes", "ordenes"),
            new("Preguntas", "/meli/preguntas", "publicaciones"),
            new("Respuestas automáticas", "/meli/respuestas-automaticas", "publicaciones"),
            new("Clonar publicación", "/meli/clonar", "publicaciones"),
            new("SKUs MeLi", "/cafe/skus-meli"),
            new("Reservados MeLi", "/cafe/reservados"),
            new("Cobros Mercado Pago", "/mercadopago/cobranzas", "cafe-tesoreria"),
            new("Teléfonos clientes MeLi", "/cafe/telefonos-clientes-meli", "cafe-telefonos-meli"),
        }),
        new("Intereventos", "alquileres", true, new Item[]
        {
            new("Inicio", "/alquileres", "alquileres"),
            new("Reservas", "/alquileres/reservas", "alquileres"),
            new("Calendario", "/alquileres/calendario", "alquileres"),
            new("Equipos", "/alquileres/equipos", "alquileres"),
            new("Fletes", "/alquileres/fletes", "alquileres"),
            new("Texto del presupuesto", "/alquileres/texto", "alquileres"),
        }, Nuevo: "alquiler"),
        new("Logística", "armado y reparto", true, new Item[]
        {
            new("Preparación de pedidos", "/cafe/preparacion"),
            new("Mapa de reparto", "/mapeo?full=1", Externo: true),
            new("Clientes mapeados", "/cafe/clientes-mapeados"),
            new("Repartidores", "/cafe/repartidores"),
            new("Viajes", "/viajes"),
            new("Arqueo repartidor", "/cafe/arqueo-repartidor"),
            new("Entregas me1", "/meli/me1/entregas", "me1"),
            new("Códigos postales me1", "/meli/me1/codigos-postales", "me1"),
        }),
        new("Stock", "", false, new Item[]
        {
            new("Cargar stock", "/cafe/stock-masivo"),
            new("Vencimientos", "/cafe/vencimientos"),
            new("Historial stock", "/cafe/historial-stock"),
            new("Faltantes para pedir", "/stock/ideal"),
            new("Reposición / pedidos", "/stock/reposicion"),
            new("Ubicaciones", "/cafe/ubicaciones"),
            new("Tareas del depósito", "/deposito/tareas"),
        }, Nuevo: "stock"),
        new("Compras", "", false, new Item[]
        {
            new("Proveedores", "/cafe/proveedores"),
            new("¿A quién le debo?", "/cafe/proveedores-ctacte"),
            new("Facturas de compra", "/contadora?tab=comp", "contadora"),
            new("Pagos a proveedores", "/cafe/tesoreria/pagos"),
            new("Compras", "/cafe/compras"),
        }, Nuevo: "compra"),
        new("Productos y precios", "", false, new Item[]
        {
            new("Productos", "/cafe/productos"),
            new("Listas de precios", "/cafe/listas-precios"),
            new("Listas personalizadas", "/cafe/listas-precios-custom"),
            new("Precios de packs", "/cafe/packs-control"),
        }),
        new("Plata", "", false, new Item[]
        {
            new("Cajas", "/cafe/tesoreria/cajas", "cafe-tesoreria"),
            new("Cheques", "/cafe/tesoreria/cheques-todos", "cafe-tesoreria"),
            new("Plata que entró", "/plata-que-entro", "cafe-tesoreria"),
            new("Cobranzas a aprobar", "/cafe/cobranzas-pendientes", "cafe-tesoreria"),
            new("Pagos a confirmar", "/tesoreria/pagos-pendientes", "cafe-tesoreria"),
            new("Extracto banco", "/cafe/tesoreria/extracto-banco", "cafe-tesoreria"),
            new("Resumen tesorería", "/cafe/tesoreria/resumen", "cafe-tesoreria"),
            new("Bóveda de claves", "/vault"),
        }),
        new("Empleados", "", false, new Item[]
        {
            new("Horario", "/horas-extras"),
            new("Ficha empleados", "/nominas/empleados", "nominas"),
            new("Vacaciones", "/nominas/vacaciones", "nominas"),
            new("¿Cuánto debo pagar?", "/nominas/panel", "nominas"),
            new("Liquidaciones", "/nominas/liquidaciones", "nominas"),
        }),
    };

    // ───────────── Orden acomodado por el dueño (✏ Acomodar) ─────────────
    // Se guarda en la base (api/menu-config) como listas de direcciones. Las etiquetas y permisos
    // salen siempre de este archivo, así que un cambio de nombre acá no rompe lo acomodado.

    public record GrupoOrden(string Titulo, List<string> Items);
    /// <summary>Colores: clave = título de sección o dirección de pantalla → nombre de pastel.</summary>
    /// <summary>Iconos (2026-11-10): clave = título de sección o dirección de pantalla → emoji que eligió el dueño.</summary>
    public record Orden(List<string> Directos, List<GrupoOrden> Grupos, List<string> Mas,
                        Dictionary<string, string>? Colores = null, List<string>? Costado = null,
                        Dictionary<string, string>? Iconos = null);

    /// <summary>Clave de "grupo de fábrica" para lo del costado suelto ("" = Más pantallas).</summary>
    public const string CLAVE_COSTADO = "◧";

    /// <summary>2026-11-06: colores pastel que el dueño puede ponerle a secciones y pantallas.
    /// Fondo suave + borde un poco más fuerte (para la rayita de la sección).</summary>
    public static readonly (string Clave, string Nombre, string Fondo, string Borde)[] Pasteles =
    {
        ("rosa", "Rosa", "#fce7f3", "#f9a8d4"),
        ("durazno", "Durazno", "#ffedd5", "#fdba74"),
        ("amarillo", "Amarillo", "#fef9c3", "#facc15"),
        ("verde", "Verde", "#dcfce7", "#86efac"),
        ("menta", "Menta", "#ccfbf1", "#5eead4"),
        ("celeste", "Celeste", "#dbeafe", "#93c5fd"),
        ("lavanda", "Lavanda", "#ede9fe", "#c4b5fd"),
        ("cafe", "Café con leche", "#efe4d6", "#c8a27c"),
        // 2026-11-07: más variedad (pedido del dueño). Siguen siendo suaves para que el texto se lea.
        ("rojo", "Rojo suave", "#fee2e2", "#fca5a5"),
        ("coral", "Coral", "#ffe4d6", "#fb923c"),
        ("mostaza", "Mostaza", "#fdf0c4", "#eab308"),
        ("lima", "Lima", "#ecfccb", "#a3e635"),
        ("salvia", "Salvia", "#e3ede4", "#9cb8a0"),
        ("turquesa", "Turquesa", "#cffafe", "#22d3ee"),
        ("azul", "Azul", "#e0e7ff", "#818cf8"),
        ("violeta", "Violeta", "#f3e8ff", "#c084fc"),
        ("fucsia", "Fucsia", "#fae8ff", "#e879f9"),
        ("gris", "Gris", "#f1f5f9", "#94a3b8"),
        ("arena", "Arena", "#f5f0e6", "#d6c7a1"),
        ("plomo", "Plomo", "#e5e7eb", "#4b5563"),
    };

    public static (string Fondo, string Borde)? Pastel(string? clave)
    {
        if (string.IsNullOrEmpty(clave)) return null;
        foreach (var p in Pasteles) if (p.Clave == clave) return (p.Fondo, p.Borde);
        return null;
    }

    public static bool EsDirectoDeFabrica(string href)
        => Directos.Any(d => string.Equals(d.Href, href, StringComparison.OrdinalIgnoreCase));

    /// <summary>Cómo queda el menú para mostrar: grupos con sus renglones ya ordenados.</summary>
    public record GrupoArmado(Grupo Grupo, List<Item> Items);
    public record Armado(List<Item> Directos, List<GrupoArmado> Grupos, List<Item> Mas,
                         Dictionary<string, string> Colores, List<Item> Costado,
                         Dictionary<string, string>? Iconos = null);

    /// <summary>2026-11-10: emojis para elegir en ✏ Acomodar (pedido del dueño: "poner íconos yo").</summary>
    /// <summary>2026-11-10 (2ª): más emojis y ordenados por tema del negocio (café, envases y
    /// limpieza, eventos, ventas, plata, reparto, oficina, colores). Pedido del dueño.</summary>
    public static readonly (string Tema, string[] Lista)[] EmojiGrupos =
    {
        ("Café", new[] { "☕","🫘","🫖","🍵","🧋","🥛","🍫","🍪","🥐","🧁","🍰","🍩","🥤","🧃","🍦","🍨","🧉","🍯","🧂","🍬" }),
        ("Envases y limpieza", new[] { "📦","🫙","🥫","🧴","🧽","🧻","🪣","🧺","🗑","🛍","🥡","🍽","🥄","🍴","🧊","🧤","🧹","🧼","🪥","🫧" }),
        ("Eventos y alquileres", new[] { "🎪","🎉","🎈","🪑","🛋","⛺","💡","🔌","🎤","🎶","🥂","🍾","🎂","🎁","🎊","🪅" }),
        ("Ventas y MercadoLibre", new[] { "🛒","🏷","💲","🧾","📈","📉","⭐","🤝","📣","🆕","🔖","🏪" }),
        ("Plata", new[] { "💵","💰","💳","🏦","💸","🪙","🧮","📊","⛽" }),
        ("Reparto", new[] { "🚚","🛵","🚐","🚗","📍","🗺","🧭","🏠","🏬","🏭" }),
        ("Oficina y equipo", new[] { "📋","📝","📁","🗂","📅","⏰","📞","📱","💬","✉","🔔","👥","👤","🔧","⚙","🔑","🔒","🔍","📸","🖨" }),
        ("Colores y avisos", new[] { "🟢","🟡","🔴","🔵","🟣","🟠","⚫","⚪","🔥","❤","✅","⚠","🚨","💯" }),
    };
    public static readonly string[] Emojis = EmojiGrupos.SelectMany(g => g.Lista).Distinct().ToArray();
    public static string? IconoDe(Dictionary<string, string>? iconos, string clave)
        => iconos != null && iconos.TryGetValue(clave, out var e) && !string.IsNullOrWhiteSpace(e) ? e : null;

    private static Dictionary<string, Item>? _porHref;
    private static Dictionary<string, string>? _grupoDeFabrica;

    /// <summary>Cada pantalla una sola vez, por dirección (la primera vez que aparece).</summary>
    public static Dictionary<string, Item> PorHref
    {
        get
        {
            if (_porHref != null) return _porHref;
            var d = new Dictionary<string, Item>(StringComparer.OrdinalIgnoreCase);
            foreach (var it in Grupos.SelectMany(g => g.Items).Concat(MasPantallas).Concat(Costado).Concat(Directos))
                d.TryAdd(it.Href, it);
            return _porHref = d;
        }
    }

    /// <summary>En qué grupo venía cada pantalla de fábrica ("" = Más pantallas). Sirve para
    /// mostrar los subtítulos (Stock / Compras) solo mientras la pantalla siga en su lugar.</summary>
    public static Dictionary<string, string> GrupoDeFabrica
    {
        get
        {
            if (_grupoDeFabrica != null) return _grupoDeFabrica;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in Grupos) foreach (var it in g.Items) d.TryAdd(it.Href, g.Titulo);
            foreach (var it in MasPantallas) d.TryAdd(it.Href, "");
            foreach (var it in Costado) d.TryAdd(it.Href, CLAVE_COSTADO);
            return _grupoDeFabrica = d;
        }
    }

    public static Orden OrdenDeFabrica() => new(
        Directos.Select(i => i.Href).ToList(),
        Grupos.Select(g => new GrupoOrden(g.Titulo, g.Items.Select(i => i.Href).ToList())).ToList(),
        MasPantallas.Select(i => i.Href).ToList(),
        null,
        Costado.Select(i => i.Href).ToList());

    /// <summary>Junta lo acomodado con el catálogo: lo que ya no existe se descarta, y una
    /// pantalla nueva que el dueño todavía no acomodó aparece en su grupo de fábrica.</summary>
    public static Armado Armar(Orden? o)
    {
        o ??= OrdenDeFabrica();
        var usados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        List<Item> Tomar(IEnumerable<string>? hrefs, bool marcar = true)
        {
            var r = new List<Item>();
            foreach (var h in hrefs ?? Enumerable.Empty<string>())
                if (PorHref.TryGetValue(h, out var it) && (!marcar || usados.Add(it.Href)))
                    r.Add(it);
            return r;
        }
        // Los directos usan su nombre corto si lo tienen ("Preparación" y no "Preparación de pedidos").
        var guardados = Tomar(o.Directos, marcar: false).DistinctBy(i => i.Href)
            .Where(i => !NoDirectos.Contains(i.Href))
            .Select(i => Directos.FirstOrDefault(d => string.Equals(d.Href, i.Href, StringComparison.OrdinalIgnoreCase)) ?? i)
            .ToList();
        // 2026-11-10: los 5 de fábrica van siempre primero y en su orden; después, si el dueño
        // marcó alguno más con ★, ese también.
        var directos = Directos.Concat(guardados.Where(i => i.Nuevo == null && !EsDirectoDeFabrica(i.Href))).ToList();
        // Cada pantalla una sola vez: lo que tiene botón directo no se repite en los desplegables.
        foreach (var d in directos) usados.Add(d.Href);
        var grupos = Grupos.Select(g =>
        {
            var go = o.Grupos?.FirstOrDefault(x => x.Titulo == g.Titulo)
                     ?? (g.Titulo == "Clientes" ? o.Grupos?.FirstOrDefault(x => x.Titulo == TITULO_VIEJO_CLIENTES) : null);
            return new GrupoArmado(g, Tomar(go?.Items));
        }).ToList();
        var mas = Tomar(o.Mas);
        // Lo guardado antes de que existiera el "Costado" no lo trae: va el de fábrica.
        var costado = Tomar(o.Costado ?? Costado.Select(i => i.Href).ToList());
        // Pantallas que no estaban en lo guardado (agregadas después): a su lugar de fábrica.
        foreach (var (href, it) in PorHref)
        {
            if (usados.Contains(href)) continue;
            if (!GrupoDeFabrica.TryGetValue(href, out var gt)) continue; // solo-directo
            usados.Add(href);
            if (gt == "") mas.Add(it);
            else if (gt == CLAVE_COSTADO) costado.Add(it);
            else grupos.First(x => x.Grupo.Titulo == gt).Items.Add(it);
        }
        var colores = new Dictionary<string, string>(o.Colores ?? new(), StringComparer.OrdinalIgnoreCase);
        if (colores.Remove(TITULO_VIEJO_CLIENTES, out var colorClientes)) colores.TryAdd("Clientes", colorClientes);
        // "Stock y compras" se separó en dos: cada mitad hereda el color que tenía.
        if (colores.Remove("Stock y compras", out var colorStock))
        {
            colores.TryAdd("Stock", colorStock);
            colores.TryAdd("Compras", colorStock);
        }
        var iconos = new Dictionary<string, string>(o.Iconos ?? new(), StringComparer.OrdinalIgnoreCase);
        return new Armado(directos, grupos, mas, colores, costado, iconos);
    }

    public static Orden AOrden(Armado a) => new(
        a.Directos.Select(i => i.Href).ToList(),
        a.Grupos.Select(g => new GrupoOrden(g.Grupo.Titulo, g.Items.Select(i => i.Href).ToList())).ToList(),
        a.Mas.Select(i => i.Href).ToList(),
        new Dictionary<string, string>(a.Colores),
        a.Costado.Select(i => i.Href).ToList(),
        a.Iconos == null ? null : new Dictionary<string, string>(a.Iconos));

    /// <summary>2026-11-06: lo que va suelto en el menú del costado (arriba de "Más pantallas").
    /// También se puede mover a las secciones de arriba con ✏ Acomodar, y al revés.</summary>
    public static readonly Item[] Costado =
    {
        new("Dashboard", "/", "dashboard"),
        new("Panorama", "/panorama", "dashboard"),
        new("Chat", "/chat"),
        new("Mis alertas", "/mis-alertas", SoloCelu: true),
        new("Automatizaciones y alertas", "/automatizaciones"),
        new("Compartido", "/compartido", "compartido"),
        // 2026-11-10: el botón verde de WhatsApp ya no está arriba en compu: queda acá, en "⚙ Organizar".
        new("WhatsApp · Chats", "/whatsapp", "cafe-depositos"),
        new("WhatsApp · Llamadas", "/llamadas", "cafe-depositos"),
        new("Documentación", "/documentacion"),
    };

    /// <summary>Lo que casi no se usa: queda en el costado, adentro de "Más pantallas".</summary>
    public static readonly Item[] MasPantallas =
    {
        new("Inicio café", "/cafe"),
        new("Consultas", "/cafe/consultas"),
        new("Todos los códigos", "/cafe/catalogo"),
        new("Combos MeLi", "/cafe/combos"),
        new("Kits", "/cafe/kits"),
        new("Servicios", "/cafe/servicios"),
        new("OEMs", "/cafe/oems"),
        new("Marcas", "/cafe/marcas"),
        new("Fotos fondo verde/azul", "/cafe/fotos-chroma"),
        new("Ranking productos", "/stock/ranking"),
        new("Valor de stock", "/stock/valuacion"),
        new("Depósitos", "/cafe/depositos", "cafe-depositos"),
        new("Pagos rápidos (celu)", "/pagos-movil", "cafe-tesoreria"),
        new("Catálogo de bancos", "/cafe/configuracion/bancos", "cafe-tesoreria"),
        new("Cobrar con Mercado Pago", "/mercadopago/cobrar", "cafe-tesoreria"),
        new("Publicaciones (vieja)", "/publicaciones", "publicaciones"),
        new("Publicaciones ME1", "/meli/me1/publicaciones", "me1"),
        new("Re-vincular publicaciones", "/relink-meli-orphans", "import-productos"),
        new("Push de stock (lote seguro)", "/cafe/push-quietos", "publicaciones"),
        new("Inicio nóminas", "/nominas", "nominas"),
        new("Configuración del sistema", "/cafe/configuracion"),
        new("Errores ARCA", "/cafe/arca/errores"),
        new("Cotejo MeLi", "/cafe/cotejo"),
        new("Saldos migración", "/cafe/saldos"),
    };
}
