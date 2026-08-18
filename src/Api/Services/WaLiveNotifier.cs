using Api.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace Api.Services;

/// <summary>
/// 2026-08-18: avisa EN EL MOMENTO a las pantallas abiertas que entró o salió un mensaje de WhatsApp.
///
/// Por qué: la pantalla del celular preguntaba "¿hay algo nuevo?" cada 12–15 segundos y se traía la
/// conversación entera (hasta 200 mensajes) cada vez. Se sentía lento comparado con WhatsApp y comía
/// datos del teléfono. Ahora el servidor empuja un aviso chiquito (número + línea + cuándo) por el
/// mismo canal en vivo que ya se usaba para ver quién está mirando cada chat, y la pantalla recarga
/// solo esa conversación.
///
/// Es best-effort: si el aviso falla, las pantallas siguen con su sondeo de respaldo (más lento).
/// </summary>
public class WaLiveNotifier
{
    private readonly IHubContext<PresenceHub> _hub;
    private readonly ILogger<WaLiveNotifier> _logger;
    private readonly IServiceScopeFactory _scopes;

    public WaLiveNotifier(IHubContext<PresenceHub> hub, ILogger<WaLiveNotifier> logger, IServiceScopeFactory scopes)
    {
        _hub = hub;
        _logger = logger;
        _scopes = scopes;
    }

    /// <summary>convId con el MISMO formato que usa la presencia: "{numero}|{linea}".</summary>
    public static string ConvId(string? numero, string? lineaPhoneId) => $"{numero}|{lineaPhoneId}";

    /// <summary>Avisa a todas las pantallas abiertas. <paramref name="direccion"/> = INCOMING | OUTGOING.</summary>
    public async Task AvisarAsync(string? numero, string? lineaPhoneId, string direccion)
    {
        if (string.IsNullOrWhiteSpace(numero)) return;
        try
        {
            await _hub.Clients.Group("presence-all")
                .SendAsync("WaNuevoMensaje", ConvId(numero, lineaPhoneId), direccion, DateTime.UtcNow);

            // 2026-08-18: ademas, despertar a los telefonos que tengan la pantalla CERRADA.
            // Solo con mensajes del cliente: no tiene sentido avisarle a alguien de lo que
            // acabamos de mandar nosotros. Es best-effort y no bloquea el flujo del mensaje.
            if (string.Equals(direccion, "INCOMING", StringComparison.OrdinalIgnoreCase))
            {
                using var scope = _scopes.CreateScope();
                var push = scope.ServiceProvider.GetRequiredService<WaPushService>();
                await push.AvisarAsync();
            }
        }
        catch (Exception ex)
        {
            // No romper el flujo del mensaje por un aviso de pantalla.
            _logger.LogDebug(ex, "[WaLive] no se pudo avisar del mensaje de {Numero}", numero);
        }
    }
}
