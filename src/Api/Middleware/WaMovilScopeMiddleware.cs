namespace Api.Middleware;

/// <summary>
/// 2026-08-26 — El candado de la llave que da la huella.
///
/// Pedido de Osmar: que la pantalla de WhatsApp del celu se abra con la huella y listo, sin tener
/// que loguearse antes en el sistema ("se me quejan de que hay que entrar a otro lado para
/// loguearse"). Para eso la huella tiene que ENTREGAR una sesión, no ser un segundo cerrojo.
///
/// El riesgo obvio: si esa sesión fuera una sesión normal, con el dedo entrarías también a ventas,
/// cobranzas y administración. Por eso la sesión que entrega la huella lleva la marca `scope`
/// = `wa-movil`, y este middleware la encierra: con esa marca SOLO se pueden pedir las direcciones
/// de la lista blanca de abajo. Cualquier otra cosa se responde 403 y no llega al controlador.
///
/// La lista sale de mirar QUÉ pide realmente la pantalla del celu (WhatsAppMovil.razor y el chat
/// que embebe, PinnedWaChat.razor). Si mañana esa pantalla necesita algo nuevo, hay que sumarlo
/// acá a propósito — que cueste agregar es la idea.
///
/// Lo que NO toca: las sesiones normales (las que salen de usuario y clave) no tienen la marca, así
/// que pasan de largo por este middleware sin ningún cambio.
/// </summary>
public class WaMovilScopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<WaMovilScopeMiddleware> _logger;

    /// <summary>La marca que llevan las sesiones nacidas de una huella.</summary>
    public const string ClaimScope = "scope";
    public const string ScopeWaMovil = "wa-movil";

    /// <summary>Lo único que abre la llave de la huella. Todo lo demás queda cerrado.</summary>
    private static readonly string[] Permitido =
    {
        "/api/whatsapp/",        // los chats: mensajes, plantillas, adjuntos, reacciones
        "/api/wa-movil/",        // el candado de la pantalla (huella, código, revalidar)
        "/api/wa-push/",         // avisos al celular
        "/api/hubs/presence",    // quién está mirando qué, en vivo
        "/api/auth/me",          // saber quién soy
        "/api/auth/logout",      // salir
    };

    /// <summary>Excepción puntual: el saldo del cliente se muestra arriba del chat.
    /// Es de solo lectura y de UN cliente, no la lista completa.</summary>
    private static bool EsEstadoDeCuenta(string ruta)
        => ruta.StartsWith("/api/cafe/clientes/", StringComparison.OrdinalIgnoreCase)
           && ruta.EndsWith("/estado-cuenta", StringComparison.OrdinalIgnoreCase);

    public WaMovilScopeMiddleware(RequestDelegate next, ILogger<WaMovilScopeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var scope = ctx.User?.FindFirst(ClaimScope)?.Value;
        if (scope != ScopeWaMovil)
        {
            await _next(ctx);   // sesión normal: no se toca nada
            return;
        }

        var ruta = ctx.Request.Path.Value ?? "";
        var permitido = Permitido.Any(p => ruta.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                        || EsEstadoDeCuenta(ruta);

        if (!permitido)
        {
            // No es un error del usuario: es la llave haciendo su trabajo. Se registra para poder
            // detectar si a la pantalla del celu le falta un permiso legítimo.
            _logger.LogWarning("[WaMovil] Sesión de huella ({Quien}) quiso entrar a {Ruta} — bloqueado",
                ctx.User?.Identity?.Name ?? "?", ruta);
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = "Entraste con la huella, que abre solo el WhatsApp del celular. " +
                        "Para el resto del sistema entrá con tu usuario y clave."
            });
            return;
        }

        await _next(ctx);
    }
}
