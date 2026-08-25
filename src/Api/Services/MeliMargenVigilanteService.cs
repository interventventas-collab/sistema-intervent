using Microsoft.EntityFrameworkCore;
using Api.Data;
using Api.Models;

namespace Api.Services;

/// <summary>
/// 2026-08-25 — Vigilante nocturno del margen. AVISA, NO TOCA NADA.
///
/// La idea es de Osmar: los primeros días las publicaciones quedan "pusheando" solas (con el
/// objetivo de ganancia y el sincro de precio prendidos), pero después él quiere volver a
/// decidir a mano — porque si mete un producto en promoción no le sirve que el sistema le
/// suba el precio automáticamente.
///
/// Entonces esto revisa cada noche cuánto deja realmente cada publicación activa y avisa las
/// que cayeron abajo de su objetivo (o del 50% si no tiene). El aviso sale por la campanita y
/// por Telegram, con la CAUSA: qué cambió desde la última vez (comisión, envío o costo).
///
/// NO cambia precios. NO pausa nada. Solo registra el evento MARGEN_BAJO en
/// MeliCambiosDetectados y deja que el notificador de siempre lo mande.
///
/// Horario: 07:00 UTC = 04:00 ARG, una hora después del refresco de comisiones (que corre a
/// las 06:00 UTC) para trabajar siempre con datos frescos.
///
/// KILL SWITCH: AppSettings["meli.margen_vigilante.enabled"], default PRENDIDO.
/// Para no repetir avisos: si ya hay uno sin ver de esa publicación, no se vuelve a avisar
/// salvo que el margen haya empeorado 5 puntos o más. Marcar el aviso como visto (o resolverlo)
/// hace que el próximo cambio vuelva a avisar — eso cubre el caso "está en promoción, dejala".
/// </summary>
public class MeliMargenVigilanteService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MeliMargenVigilanteService> _logger;

    private const decimal IVA = 1.21m;
    private const decimal PISO_DEFAULT = 50m;
    private const decimal EMPEORO_PUNTOS = 5m;   // cuánto tiene que empeorar para volver a avisar
    private const int MAX_AVISOS_POR_NOCHE = 40; // no llenar el Telegram: los peores primero

    public MeliMargenVigilanteService(IServiceScopeFactory scopeFactory,
        ILogger<MeliMargenVigilanteService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(4), stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var hora = await LeerHoraAsync(db, stoppingToken);
                var ahora = DateTime.UtcNow;

                if (!await ApagadoAsync(db, stoppingToken)
                    && !await CorrioHoyAsync(db, ahora, stoppingToken)
                    && ahora.Hour == hora
                    && await db.MeliAccounts.AnyAsync(stoppingToken))
                {
                    await RevisarAsync(db, ahora, stoppingToken);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Vigilante margen] Falló el ciclo");
            }

            try { await Task.Delay(TimeSpan.FromMinutes(20), stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RevisarAsync(AppDbContext db, DateTime ahora, CancellationToken ct)
    {
        // Costo de cada publicación (una consulta para todas).
        var costos = await (
            from c in db.MeliItemComponentes.AsNoTracking()
            join p in db.CafeProductos.AsNoTracking() on c.CafeProductoId equals p.Id
            group p.Costo * c.Cantidad by c.MeliItemId into g
            select new { MeliItemId = g.Key, Costo = g.Sum() }
        ).ToDictionaryAsync(x => x.MeliItemId, x => x.Costo, ct);

        var activas = await db.MeliItems.AsNoTracking()
            .Where(m => m.VariationId == null && m.Status == "active" && m.Price > 0 && m.SaleFeeAmount > 0)
            .Select(m => new
            {
                m.MeliItemId, m.MeliAccountId, m.Sku, m.Title, m.Price,
                m.SaleFeeAmount, m.SaleFeeShippingCost
            })
            .ToListAsync(ct);

        var objetivos = await db.MeliItemSyncConfigs.AsNoTracking()
            .Where(c => c.GananciaObjetivoPct != null && c.GananciaObjetivoPct > 0)
            .ToDictionaryAsync(c => c.MeliItemId, c => c.GananciaObjetivoPct!.Value, ct);

        // Avisos anteriores que siguen SIN VER: sirven para no repetir.
        var previos = await db.MeliCambiosDetectados
            .Where(c => c.Tipo == "MARGEN_BAJO" && c.SeenAt == null)
            .ToDictionaryAsync(c => c.MeliItemId, ct);

        var nuevos = new List<(decimal Margen, MeliCambioDetectado Ev)>();

        foreach (var m in activas)
        {
            if (!costos.TryGetValue(m.MeliItemId, out var costo) || costo <= 0) continue;

            var seLleva = (m.SaleFeeAmount ?? 0m) + (m.SaleFeeShippingCost ?? 0m);
            var ganancia = (m.Price - seLleva) / IVA - costo;
            var margen = Math.Round(ganancia / costo * 100m, 1);

            var piso = objetivos.TryGetValue(m.MeliItemId, out var obj) ? obj : PISO_DEFAULT;
            if (margen >= piso) continue;   // está bien, no molestamos

            // ¿Ya le avisamos y no lo miró? Solo insistimos si empeoró de verdad.
            if (previos.TryGetValue(m.MeliItemId, out var previo))
            {
                var margenPrevio = previo.DeltaPct ?? 0m;
                if (margen > margenPrevio - EMPEORO_PUNTOS) continue;
                previo.DeltaPct = margen;
                previo.ValorNuevo = m.Price.ToString(System.Globalization.CultureInfo.InvariantCulture);
                previo.Notes = ArmarDetalle(m.Price, costo, m.SaleFeeAmount ?? 0m, m.SaleFeeShippingCost ?? 0m, margen, piso);
                previo.DetectedAt = ahora;
                previo.NotifiedAt = null;   // que vuelva a avisar, porque empeoró
                continue;
            }

            nuevos.Add((margen, new MeliCambioDetectado
            {
                MeliItemId = m.MeliItemId,
                MeliAccountId = m.MeliAccountId,
                Sku = m.Sku,
                Title = m.Title,
                Tipo = "MARGEN_BAJO",
                ValorAnterior = piso.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                ValorNuevo = m.Price.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Delta = costo,
                DeltaPct = margen,
                Source = "vigilante",
                DetectedAt = ahora,
                Notes = ArmarDetalle(m.Price, costo, m.SaleFeeAmount ?? 0m, m.SaleFeeShippingCost ?? 0m, margen, piso)
            }));
        }

        // Los peores primero, y con tope para no inundar el Telegram.
        var aAvisar = nuevos.OrderBy(x => x.Margen).Take(MAX_AVISOS_POR_NOCHE).Select(x => x.Ev).ToList();
        if (aAvisar.Count > 0) db.MeliCambiosDetectados.AddRange(aAvisar);

        await db.SaveChangesAsync(ct);

        var resumen = $"{nuevos.Count} abajo del piso, {aAvisar.Count} avisadas";
        _logger.LogWarning("[Vigilante margen] {Resumen} (de {Total} activas con costo)", resumen, activas.Count);
        await MarcarCorridaAsync(db, ahora, resumen, ct);
    }

    /// <summary>El texto que explica de dónde sale el número, para que el aviso se entienda solo.</summary>
    private static string ArmarDetalle(decimal precio, decimal costo, decimal comision, decimal envio,
        decimal margen, decimal piso)
    {
        var ar = new System.Globalization.CultureInfo("es-AR");
        var partes = new List<string>
        {
            $"Precio ${precio.ToString("N0", ar)}",
            $"costo ${costo.ToString("N0", ar)}",
            $"comisión ${comision.ToString("N0", ar)}"
        };
        if (envio > 0) partes.Add($"envío ${envio.ToString("N0", ar)}");
        partes.Add($"te queda {margen.ToString("0.#", ar)}% (objetivo {piso.ToString("0", ar)}%)");
        return string.Join(" · ", partes);
    }

    private static async Task<int> LeerHoraAsync(AppDbContext db, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == "meli.margen_vigilante.hora_utc", ct);
        return s != null && int.TryParse(s.Value, out var h) && h is >= 0 and <= 23 ? h : 7; // 07 UTC = 04 ARG
    }

    private static async Task<bool> ApagadoAsync(AppDbContext db, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == "meli.margen_vigilante.enabled", ct);
        if (s is null) return false;
        var v = s.Value?.Trim().ToLowerInvariant();
        return v is "false" or "0" or "off";
    }

    private static async Task<bool> CorrioHoyAsync(AppDbContext db, DateTime ahora, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == "meli.margen_vigilante.ultima_corrida", ct);
        if (s?.Value is null) return false;
        return s.Value.Length >= 10 && s.Value[..10] == ahora.ToString("yyyy-MM-dd");
    }

    private static async Task MarcarCorridaAsync(AppDbContext db, DateTime ahora, string detalle, CancellationToken ct)
    {
        var s = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == "meli.margen_vigilante.ultima_corrida", ct);
        var valor = $"{ahora:yyyy-MM-dd HH:mm} UTC · {detalle}";
        if (s is null) db.AppSettings.Add(new AppSetting { Key = "meli.margen_vigilante.ultima_corrida", Value = valor });
        else s.Value = valor;
        await db.SaveChangesAsync(ct);
    }
}
