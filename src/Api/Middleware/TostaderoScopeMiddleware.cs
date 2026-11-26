namespace Api.Middleware;

/// <summary>
/// 2026-11-26 — El candado del usuario "tostadero".
///
/// Pedido de Osmar: una tablet fija en el tostadero que muestre los pedidos para armar con el café
/// primero (pantalla /tostadero). El usuario y la clave son los dos "tostadero", a propósito, para
/// que cualquiera la pueda abrir. Por eso ese usuario NO puede ser una sesión normal: con ese
/// rol SOLO se pueden pedir las direcciones de abajo (leer el tablero de armado). Todo lo demás
/// (ventas, clientes, bancos, administración) responde 403 y no llega al controlador.
/// 26/11 2ª vuelta: también puede marcar "Café listo", ver las ventas MeLi con café y usar el teléfono.
///
/// Las sesiones de cualquier otro rol pasan de largo sin ningún cambio.
/// </summary>
public class TostaderoScopeMiddleware
{
    public const string Rol = "tostadero";

    private readonly RequestDelegate _next;
    private readonly ILogger<TostaderoScopeMiddleware> _logger;

    public TostaderoScopeMiddleware(RequestDelegate next, ILogger<TostaderoScopeMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    private static bool Permitido(string ruta, string metodo)
    {
        // entrar, saber quién soy, salir (cambiar la clave NO: dejaría la tablet afuera)
        if (ruta.Equals("/api/auth/login", StringComparison.OrdinalIgnoreCase)
            || ruta.Equals("/api/auth/logout", StringComparison.OrdinalIgnoreCase)
            || ruta.Equals("/api/auth/me", StringComparison.OrdinalIgnoreCase)) return true;
        // fotos de producto y demás cosas que ya son públicas
        if (ruta.StartsWith("/api/public/", StringComparison.OrdinalIgnoreCase)) return true;
        // su pantalla: ventas MeLi con café y marcar "Café listo" (Api/Controllers/TostaderoController.cs)
        if (ruta.StartsWith("/api/tostadero/", StringComparison.OrdinalIgnoreCase)) return true;
        // el teléfono de la tablet (interno 230): conectarse, quién está, llamadas perdidas
        if (metodo == "GET" && (ruta.Equals("/api/telefono/config", StringComparison.OrdinalIgnoreCase)
                                || ruta.Equals("/api/telefono/estado", StringComparison.OrdinalIgnoreCase)
                                || ruta.Equals("/api/telefono/perdidas", StringComparison.OrdinalIgnoreCase))) return true;
        if (metodo == "POST" && ruta.Equals("/api/telefono/perdidas/vistas", StringComparison.OrdinalIgnoreCase)) return true;
        // el tablero de armado: solo leer
        return metodo == "GET"
               && ruta.Equals("/api/cafe/ventas/preparacion", StringComparison.OrdinalIgnoreCase);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.User?.Identity?.IsAuthenticated != true || !ctx.User.IsInRole(Rol))
        {
            await _next(ctx);
            return;
        }

        var ruta = ctx.Request.Path.Value ?? "";
        var metodo = ctx.Request.Method?.ToUpperInvariant() ?? "";
        if (!ruta.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) || Permitido(ruta, metodo))
        {
            await _next(ctx);
            return;
        }

        _logger.LogWarning("[Tostadero] {Metodo} {Ruta} — bloqueado", metodo, ruta);
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new
        {
            error = "El usuario del tostadero solo puede ver los pedidos para armar.",
            codigo = "solo-tostadero"
        });
    }
}
