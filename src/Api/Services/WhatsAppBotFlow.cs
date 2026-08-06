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

    /// <summary>
    /// TODOS los textos editables del bot, con su valor por defecto. Este es el ÚNICO lugar donde
    /// viven los defaults. Los límites (Max) respetan los máximos de WhatsApp: botón = 20,
    /// título de opción = 24, detalle de opción = 72 caracteres.
    /// </summary>
    public static readonly IReadOnlyList<Campo> Campos = new List<Campo>
    {
        // ── Paso 1: saludo + elegir empresa ──
        new("nivel1.cuerpo", "Paso 1 · Saludo",
            "Mensaje de bienvenida",
            "¡Hola! 👋 Gracias por escribirnos.\n\n¿Con quién te querés contactar?",
            true, 1024,
            "Es lo primero que recibe un número nuevo, junto con los 3 botones de empresa."),
        new("boton.frikaf", "Paso 1 · Saludo", "Botón empresa 1", "☕ Cafés Frikaf", false, 20),
        new("boton.intervent", "Paso 1 · Saludo", "Botón empresa 2", "🏢 Intervent", false, 20),
        new("boton.intereventos", "Paso 1 · Saludo", "Botón empresa 3", "🪑 Intereventos", false, 20),

        // ── Paso 2: encabezado del menú según empresa ──
        new("nivel2.cuerpo.frikaf", "Paso 2 · Menú",
            "Encabezado — Cafés Frikaf", "¡Genial! ☕ ¿Qué necesitás de Cafés Frikaf?", true, 1024),
        new("nivel2.cuerpo.intervent", "Paso 2 · Menú",
            "Encabezado — Intervent", "¡Genial! 🏢 ¿Qué necesitás de Intervent?", true, 1024),
        new("nivel2.cuerpo.intereventos", "Paso 2 · Menú",
            "Encabezado — Intereventos", "¡Genial! 🪑 Alquiler de mesas, sillas y livings. ¿Qué necesitás?", true, 1024),
        new("nivel2.botonlista", "Paso 2 · Menú",
            "Botón para abrir las opciones", "📋 Ver opciones", false, 20),

        // ── Las 4 opciones del menú (título + detalle) ──
        new("opcion.pedido.title", "Opciones del menú", "Opción 1 · título", "🛒 Hacer un pedido", false, 24),
        new("opcion.pedido.desc", "Opciones del menú", "Opción 1 · detalle", "Escribinos tu pedido por acá", false, 72),
        new("opcion.lista.title", "Opciones del menú", "Opción 2 · título", "💲 Lista de precios", false, 24),
        new("opcion.lista.desc", "Opciones del menú", "Opción 2 · detalle", "Te mandamos los precios", false, 72),
        new("opcion.proveedor.title", "Opciones del menú", "Opción 3 · título", "📦 Soy proveedor", false, 24),
        new("opcion.proveedor.desc", "Opciones del menú", "Opción 3 · detalle", "Te anotamos como proveedor", false, 72),
        new("opcion.persona.title", "Opciones del menú", "Opción 4 · título", "👤 Hablar con alguien", false, 24),
        new("opcion.persona.desc", "Opciones del menú", "Opción 4 · detalle", "Te atiende una persona", false, 72),

        // ── Respuesta final al tocar cada opción ──
        new("accion.pedido.resp", "Respuestas al elegir",
            "Al tocar «Hacer un pedido»", "¡Dale! 🛒 Escribinos tu pedido por acá y en breve te atendemos 👍", true, 1024),
        new("accion.lista.resp", "Respuestas al elegir",
            "Al tocar «Lista de precios»", "¡Dale! 💲 En breve te mandamos la lista de precios 👍", true, 1024,
            "Ojo: para Cafés Frikaf, además de este texto el bot manda solo el PDF de la lista de precios."),
        new("accion.proveedor.resp", "Respuestas al elegir",
            "Al tocar «Soy proveedor»", "¡Genial! 📦 Te anotamos como proveedor. En breve te contactamos.", true, 1024),
        new("accion.persona.resp", "Respuestas al elegir",
            "Al tocar «Hablar con alguien»", "¡Dale! 👤 En un ratito te atiende una persona. ¡Gracias por escribirnos!", true, 1024),
    };

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
            .Select(a => ($"bot:{empresa}:{a}", V($"opcion.{a}.title"), (string?)V($"opcion.{a}.desc")))
            .ToArray();

    /// <summary>Respuesta final + rol de contacto para cada acción del nivel 2.</summary>
    public (string Respuesta, string Rol) AccionNivel2(string accion, string empresa)
    {
        var resp = PorClave.ContainsKey($"accion.{accion}.resp")
            ? V($"accion.{accion}.resp")
            : "¡Gracias por escribirnos! En breve te atendemos.";
        return (resp, WhatsAppBotFlow.RolDeAccion(accion));
    }
}
