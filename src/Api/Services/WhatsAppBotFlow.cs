using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-07-23 (pedido Osmar): árbol del bot de bienvenida de WhatsApp.
/// Cuando un número DESCONOCIDO escribe por primera vez, el sistema le contesta solo con
/// 3 botones para elegir la empresa (nivel 1); al tocar uno, le manda una lista con 4
/// opciones (nivel 2); y según lo que elija lo etiqueta como contacto y le responde.
///
/// PARTE ESTRUCTURAL (ids, roles, ramas del árbol): vive acá y NO se edita desde la web.
/// PARTE DE TEXTOS (lo que lee el cliente): los valores por defecto están en <see cref="BotTextos.Campos"/>
/// y el usuario los puede editar desde la pantalla ⚙️ de WhatsApp → pestaña "Mensajes del bot".
/// Lo editado se guarda en AppSettings con la clave "whatsapp.bot.txt.{clave}". Si un texto no
/// fue editado, se usa el default. Ver <see cref="BotTextos"/>.
/// Los ids viajan a Meta y vuelven por el webhook (interactive.button_reply/list_reply.id).
/// </summary>
public static class WhatsAppBotFlow
{
    /// <summary>Marca vieja del mensaje de nivel 1. Se mantiene por compatibilidad, pero la
    /// detección de "ya le mandé el menú" ahora usa la etiqueta "[botones:" que se agrega siempre
    /// al guardar el saliente (así sigue funcionando aunque el usuario cambie el texto del saludo).</summary>
    public const string MarcaNivel1 = "¿Con quién te querés contactar?";

    /// <summary>Claves de las 3 empresas, en orden. Estructural (NO editable).</summary>
    public static readonly string[] Empresas = { "frikaf", "intervent", "intereventos" };

    /// <summary>Las 4 acciones del nivel 2, en orden. Estructural (NO editable).</summary>
    public static readonly string[] Acciones = { "pedido", "lista", "proveedor", "persona" };

    /// <summary>Nombre visible de cada empresa según la clave del id.</summary>
    public static string NombreEmpresa(string clave) => clave switch
    {
        "frikaf" => "Cafés Frikaf",
        "intervent" => "Intervent",
        "intereventos" => "Intereventos",
        _ => clave
    };

    /// <summary>Rol de contacto (cliente / proveedor / otro) para cada acción. Estructural: alimenta
    /// los filtros del chat, NO es editable.</summary>
    public static string RolDeAccion(string accion) => accion switch
    {
        "pedido" => "cliente",
        "lista" => "cliente",
        "proveedor" => "proveedor",
        _ => "otro"
    };

    /// <summary>Parsea un id de botón/lista del bot. Devuelve null si no es nuestro.</summary>
    public static (string Nivel, string Empresa, string? Accion)? ParseId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith("bot:")) return null;
        var partes = id.Split(':');
        if (partes.Length == 3 && partes[1] == "emp") return ("1", partes[2], null);
        if (partes.Length == 3) return ("2", partes[1], partes[2]);
        return null;
    }
}

/// <summary>
/// Resuelve los TEXTOS del bot: usa lo que el usuario guardó en AppSettings y, si no editó algo,
/// cae al valor por defecto definido en <see cref="Campos"/>. Se construye con
/// <see cref="CargarAsync"/> al principio de cada interacción del bot.
/// </summary>
public sealed class BotTextos
{
    /// <summary>Prefijo de las claves en AppSettings.</summary>
    public const string Prefijo = "whatsapp.bot.txt.";

    /// <summary>Un campo editable del bot: metadatos para la pantalla + su valor por defecto.</summary>
    public sealed record Campo(
        string Clave, string Grupo, string Etiqueta, string Default,
        bool Multilinea, int Max, string? Ayuda = null);

    // Default de cada acción (título/detalle/respuesta). Igual para las 3 empresas; después el usuario
    // lo edita por empresa. El orden es el orden de las opciones del menú.
    private static readonly (string Accion, string TitleDef, string DescDef, string RespDef)[] AccionesDef =
    {
        ("pedido",    "🛒 Hacer un pedido",    "Escribinos tu pedido por acá", "¡Dale! 🛒 Escribinos tu pedido por acá y en breve te atendemos 👍"),
        ("lista",     "💲 Lista de precios",   "Te mandamos los precios",       "¡Dale! 💲 En breve te mandamos la lista de precios 👍"),
        ("proveedor", "📦 Soy proveedor",      "Te anotamos como proveedor",    "¡Genial! 📦 Te anotamos como proveedor. En breve te contactamos."),
        ("persona",   "👤 Hablar con alguien", "Te atiende una persona",        "¡Dale! 👤 En un ratito te atiende una persona. ¡Gracias por escribirnos!"),
    };

    // Default del encabezado del menú por empresa.
    private static readonly (string Empresa, string Nombre, string CuerpoDef)[] EmpresasDef =
    {
        ("frikaf",       "Cafés Frikaf",  "¡Genial! ☕ ¿Qué necesitás de Cafés Frikaf?"),
        ("intervent",    "Intervent",     "¡Genial! 🏢 ¿Qué necesitás de Intervent?"),
        ("intereventos", "Intereventos",  "¡Genial! 🪑 Alquiler de mesas, sillas y livings. ¿Qué necesitás?"),
    };

    /// <summary>
    /// TODOS los textos editables del bot, con su valor por defecto. Este es el ÚNICO lugar donde
    /// viven los defaults. Las opciones y respuestas son POR EMPRESA (cada una editable aparte).
    /// Los límites (Max) respetan los máximos de WhatsApp: botón = 20, título de opción = 24,
    /// detalle de opción = 72 caracteres.
    /// </summary>
    public static readonly IReadOnlyList<Campo> Campos = ConstruirCampos();

    private static List<Campo> ConstruirCampos()
    {
        var lista = new List<Campo>();
        const string g1 = "Paso 1 · Saludo (común a las 3)";

        // ── Paso 1: saludo + botones de empresa (común a las 3) ──
        lista.Add(new("nivel1.cuerpo", g1, "Mensaje de bienvenida",
            "¡Hola! 👋 Gracias por escribirnos.\n\n¿Con quién te querés contactar?", true, 1024,
            "Es lo primero que recibe un número nuevo, junto con los 3 botones de empresa."));
        lista.Add(new("boton.frikaf", g1, "Botón empresa 1", "☕ Cafés Frikaf", false, 20));
        lista.Add(new("boton.intervent", g1, "Botón empresa 2", "🏢 Intervent", false, 20));
        lista.Add(new("boton.intereventos", g1, "Botón empresa 3", "🪑 Intereventos", false, 20));
        lista.Add(new("nivel2.botonlista", g1, "Botón para abrir las opciones", "📋 Ver opciones", false, 20,
            "El texto del botón que abre la lista de opciones (igual para las 3 empresas)."));

        // ── Un bloque por empresa: encabezado + sus 4 opciones (título, detalle, respuesta) ──
        foreach (var (emp, nombre, cuerpoDef) in EmpresasDef)
        {
            var g = $"Menú de {nombre}";
            lista.Add(new($"nivel2.cuerpo.{emp}", g, "Encabezado del menú", cuerpoDef, true, 1024));

            int i = 1;
            foreach (var (acc, titleDef, descDef, respDef) in AccionesDef)
            {
                lista.Add(new($"opcion.{emp}.{acc}.title", g, $"Opción {i} · título", titleDef, false, 24));
                lista.Add(new($"opcion.{emp}.{acc}.desc", g, $"Opción {i} · detalle", descDef, false, 72));
                lista.Add(new($"accion.{emp}.{acc}.resp", g, $"Opción {i} · respuesta al tocarla", respDef, true, 1024,
                    acc == "lista" && emp == "frikaf"
                        ? "Ojo: para Cafés Frikaf, además de este texto el bot manda solo el PDF de la lista de precios."
                        : null));
                i++;
            }
        }
        return lista;
    }

    private static readonly Dictionary<string, Campo> PorClave =
        Campos.ToDictionary(c => c.Clave, c => c);

    private readonly IReadOnlyDictionary<string, string> _overrides;

    private BotTextos(IReadOnlyDictionary<string, string> overrides) => _overrides = overrides;

    /// <summary>Instancia sin ninguna edición (solo defaults). Útil para tests o fallbacks.</summary>
    public static BotTextos Default { get; } = new(new Dictionary<string, string>());

    /// <summary>Carga los textos editados desde AppSettings. Los que no estén, quedan en default.</summary>
    public static async Task<BotTextos> CargarAsync(AppDbContext db)
    {
        var dict = await db.AppSettings
            .Where(s => s.Key.StartsWith(Prefijo))
            .ToDictionaryAsync(s => s.Key.Substring(Prefijo.Length), s => s.Value);
        return new BotTextos(dict);
    }

    /// <summary>Valor efectivo de una clave: lo editado si existe y no está vacío, si no el default.</summary>
    public string V(string clave)
    {
        if (_overrides.TryGetValue(clave, out var v) && !string.IsNullOrWhiteSpace(v))
            return v;
        return PorClave.TryGetValue(clave, out var c) ? c.Default : "";
    }

    // ── Nivel 1 ──
    public string CuerpoNivel1 => V("nivel1.cuerpo");

    public (string Id, string Title)[] BotonesNivel1 =>
        WhatsAppBotFlow.Empresas.Select(e => ($"bot:emp:{e}", V($"boton.{e}"))).ToArray();

    // ── Nivel 2 ──
    public string CuerpoNivel2(string empresa) => V($"nivel2.cuerpo.{empresa}");

    public string BotonListaNivel2 => V("nivel2.botonlista");

    public (string Id, string Title, string? Desc)[] FilasNivel2(string empresa) =>
        WhatsAppBotFlow.Acciones
            .Select(a => ($"bot:{empresa}:{a}", V($"opcion.{empresa}.{a}.title"), (string?)V($"opcion.{empresa}.{a}.desc")))
            .ToArray();

    /// <summary>Respuesta final + rol de contacto para cada acción del nivel 2 (respuesta por empresa).</summary>
    public (string Respuesta, string Rol) AccionNivel2(string accion, string empresa)
    {
        var clave = $"accion.{empresa}.{accion}.resp";
        var resp = PorClave.ContainsKey(clave)
            ? V(clave)
            : "¡Gracias por escribirnos! En breve te atendemos.";
        return (resp, WhatsAppBotFlow.RolDeAccion(accion));
    }
}
