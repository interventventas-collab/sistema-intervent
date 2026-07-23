namespace Api.Services;

/// <summary>
/// 2026-07-23 (pedido Osmar): árbol del bot de bienvenida de WhatsApp.
/// Cuando un número DESCONOCIDO escribe por primera vez, el sistema le contesta solo con
/// 3 botones para elegir la empresa (nivel 1); al tocar uno, le manda una lista con 4
/// opciones (nivel 2); y según lo que elija lo etiqueta como contacto y le responde.
///
/// Los textos y opciones viven TODOS acá para poder cambiarlos fácil en un solo lugar.
/// Los ids viajan a Meta y vuelven por el webhook (interactive.button_reply/list_reply.id).
/// </summary>
public static class WhatsAppBotFlow
{
    /// <summary>Marca única del mensaje de nivel 1 — sirve para saber si ya le mandamos el menú
    /// a un número (se busca en el historial de OUTGOING) y no repetirlo.</summary>
    public const string MarcaNivel1 = "¿Con quién te querés contactar?";

    public const string CuerpoNivel1 =
        "¡Hola! 👋 Gracias por escribirnos.\n\n¿Con quién te querés contactar?";

    /// <summary>Los 3 botones del nivel 1 (máximo de WhatsApp). El id lleva la empresa.</summary>
    public static readonly (string Id, string Title)[] BotonesNivel1 =
    {
        ("bot:emp:frikaf",       "☕ Cafés Frikaf"),
        ("bot:emp:intervent",    "🏢 Intervent"),
        ("bot:emp:intereventos", "🪑 Intereventos")
    };

    /// <summary>Nombre visible de cada empresa según la clave del id.</summary>
    public static string NombreEmpresa(string clave) => clave switch
    {
        "frikaf" => "Cafés Frikaf",
        "intervent" => "Intervent",
        "intereventos" => "Intereventos",
        _ => clave
    };

    public static string CuerpoNivel2(string claveEmpresa) => claveEmpresa switch
    {
        "frikaf" => "¡Genial! ☕ ¿Qué necesitás de Cafés Frikaf?",
        "intervent" => "¡Genial! 🏢 ¿Qué necesitás de Intervent?",
        "intereventos" => "¡Genial! 🪑 Alquiler de mesas, sillas y livings. ¿Qué necesitás?",
        _ => "¡Genial! ¿Qué necesitás?"
    };

    public const string BotonListaNivel2 = "📋 Ver opciones";

    /// <summary>Las 4 opciones del nivel 2, iguales para las 3 empresas. El id lleva empresa+acción.</summary>
    public static (string Id, string Title, string? Desc)[] FilasNivel2(string claveEmpresa) => new[]
    {
        ($"bot:{claveEmpresa}:pedido",    "🛒 Hacer un pedido",   (string?)"Escribinos tu pedido por acá"),
        ($"bot:{claveEmpresa}:lista",     "💲 Lista de precios",  "Te mandamos los precios"),
        ($"bot:{claveEmpresa}:proveedor", "📦 Soy proveedor",     "Te anotamos como proveedor"),
        ($"bot:{claveEmpresa}:persona",   "👤 Hablar con alguien","Te atiende una persona")
    };

    /// <summary>Respuesta final + rol de contacto para cada acción del nivel 2.
    /// El rol alimenta los filtros del chat (cliente / proveedor / otro).</summary>
    public static (string Respuesta, string Rol) AccionNivel2(string accion, string claveEmpresa) => accion switch
    {
        "pedido" => ("¡Dale! 🛒 Escribinos tu pedido por acá y en breve te atendemos 👍", "cliente"),
        "lista" => ("¡Dale! 💲 En breve te mandamos la lista de precios 👍", "cliente"),
        "proveedor" => ("¡Genial! 📦 Te anotamos como proveedor. En breve te contactamos.", "proveedor"),
        "persona" => ("¡Dale! 👤 En un ratito te atiende una persona. ¡Gracias por escribirnos!", "otro"),
        _ => ("¡Gracias por escribirnos! En breve te atendemos.", "otro")
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
