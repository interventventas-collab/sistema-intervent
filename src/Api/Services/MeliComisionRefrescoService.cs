using System.Collections.Concurrent;

namespace Api.Services;

/// <summary>
/// 2026-10-07 — Vuelve a preguntarle a MeLi la comisión y el envío de una publicación cuando cambia
/// algo que los mueve (precio, tipo, cuotas, envío gratis), y lo repite un par de veces.
///
/// Por qué existe: el armario C9305GR (MLA2898760758) pasó de 6 a 12 cuotas desde el panel de MeLi.
/// El sistema se enteró por el aviso de MeLi y actualizó precio y cuotas, pero siguió mostrando la
/// comisión del 18/09 ("dato viejo"): el "Recibís" de la pantalla no coincidía con el de MeLi.
/// Y MeLi a veces tarda en reflejar un cambio, así que una sola consulta al instante puede volver
/// con el número anterior. Por eso: al toque, al minuto y a los 5 minutos.
///
/// Solo consulta (GET) — no toca precios ni stock.
/// </summary>
public class MeliComisionRefrescoService : BackgroundService
{
    private static readonly TimeSpan[] REPETICIONES =
        { TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5) };

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliComisionRefrescoService> _logger;

    // MLA → cuándo toca la próxima consulta (las que quedan, en orden).
    private readonly ConcurrentDictionary<string, List<DateTime>> _pendientes = new();

    public MeliComisionRefrescoService(IServiceScopeFactory scopeFactory, ILogger<MeliComisionRefrescoService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>Agenda las consultas. Si la publicación ya estaba agendada, la tanda arranca de nuevo
    /// (un segundo cambio a los 2 minutos también tiene que esperar sus 5 minutos).</summary>
    public void Programar(string meliItemId, bool incluirInmediata = true)
    {
        if (string.IsNullOrWhiteSpace(meliItemId)) return;
        var ahora = DateTime.UtcNow;
        var cuando = REPETICIONES.Skip(incluirInmediata ? 0 : 1).Select(d => ahora + d).ToList();
        _pendientes[meliItemId] = cuando;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken); }
            catch (OperationCanceledException) { return; }

            var ahora = DateTime.UtcNow;
            var vencidas = new List<string>();
            foreach (var (mla, cuando) in _pendientes)
            {
                lock (cuando)
                {
                    if (cuando.Count == 0 || cuando[0] > ahora) continue;
                    // Si se juntaron varias vencidas (API reiniciando, MeLi lento) alcanza con una consulta.
                    cuando.RemoveAll(c => c <= ahora);
                }
                vencidas.Add(mla);
            }

            foreach (var mla in vencidas)
            {
                if (stoppingToken.IsCancellationRequested) return;
                if (_pendientes.TryGetValue(mla, out var resto))
                    lock (resto) { if (resto.Count == 0) _pendientes.TryRemove(new KeyValuePair<string, List<DateTime>>(mla, resto)); }
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var itemSvc = scope.ServiceProvider.GetRequiredService<MeliItemService>();
                    var (ok, cambios) = await itemSvc.RefreshSaleFeeConDetalleAsync(mla);
                    _logger.LogInformation("[ComisionRefresco] {Mla}: {Res}{Cambios}", mla, ok ? "ok" : "sin dato",
                        cambios.Count > 0 ? " · " + string.Join(", ", cambios) : "");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[ComisionRefresco] {Mla}: falló la consulta", mla);
                }
            }
        }
    }
}
