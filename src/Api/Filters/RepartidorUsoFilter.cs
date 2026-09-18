using Api.Data;
using Api.Middleware;
using Api.Services;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Api.Filters;

/// <summary>
/// 2026-09-18 — Anota la última vez que cada repartidor usó su link personal.
///
/// Pedido del dueño: "¿no podría decir la última vez que lo abrió cada repartidor y listo?". Los
/// repartidores no entran con usuario y clave, así que no dejan sesión y no aparecen en "Sesiones
/// abiertas". Esto es lo único que dice si su link se está usando y desde qué aparato.
///
/// Va como filtro sobre los controladores del repartidor (y no copiado en cada método) porque son
/// más de 20 puertas y basta con que la persona toque cualquiera para que cuente como "la abrió".
///
/// DOS CUIDADOS:
///   • Desde la oficina se puede abrir "👁 Ver como" un repartidor. Esa compu tiene sesión de
///     usuario, y si se anotara, cada vez que la oficina mira la pantalla de Nacho figuraría que
///     Nacho la abrió. Regla: si viene con una sesión normal de la oficina, NO se anota. Los celus
///     de la huella (marca wa-movil) sí cuentan: Osmar y Gabriel también reparten.
///   • Se escribe como mucho una vez por minuto por repartidor: la pantalla se refresca sola y no
///     tiene sentido ir a la base en cada refresco.
///
/// Nunca rompe nada: si falla la anotación, el repartidor igual ve sus pedidos.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RepartidorUsoFilter : Attribute, IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        var resultado = await next();

        try
        {
            if (!ctx.ActionArguments.TryGetValue("tokenRepartidor", out var t)
                || t is not string token || string.IsNullOrWhiteSpace(token)) return;

            // Solo si el pedido anduvo: un link equivocado o vencido no es "lo abrió".
            if (resultado.Exception is not null && !resultado.ExceptionHandled) return;
            if (resultado.Result is IStatusCodeActionResult sc && sc.StatusCode >= 400) return;

            var http = ctx.HttpContext;

            // "Ver como" desde la oficina: sesión normal de usuario → no es el repartidor.
            var user = http.User;
            if (user.Identity?.IsAuthenticated == true
                && user.FindFirst(WaMovilScopeMiddleware.ClaimScope)?.Value != WaMovilScopeMiddleware.ScopeWaMovil)
                return;

            var cache = http.RequestServices.GetRequiredService<IMemoryCache>();
            var clave = "repartidor-uso:" + token;
            if (cache.TryGetValue(clave, out _)) return;
            cache.Set(clave, true, TimeSpan.FromMinutes(1));

            var (aparato, _) = SesionesService.DescribirDispositivo(SesionesService.UserAgentDe(http));
            var ip = SesionesService.IpDe(http);
            if (ip is { Length: > 60 }) ip = ip[..60];
            if (aparato.Length > 80) aparato = aparato[..80];
            var ahora = DateTime.UtcNow;

            var db = http.RequestServices.GetRequiredService<AppDbContext>();
            await db.CafeRepartidores
                .Where(r => r.PublicToken == token && r.IsActive)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(r => r.UltimoUsoAt, ahora)
                    .SetProperty(r => r.UltimoUsoAparato, aparato)
                    .SetProperty(r => r.UltimoUsoIp, ip));
        }
        catch (Exception ex)
        {
            ctx.HttpContext.RequestServices.GetService<ILogger<RepartidorUsoFilter>>()
                ?.LogWarning(ex, "No se pudo anotar el uso del link del repartidor");
        }
    }
}
