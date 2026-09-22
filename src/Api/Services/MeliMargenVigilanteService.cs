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
                    await RevisarAsync(scope.ServiceProvider, db, ahora, stoppingToken);
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

    // 2026-09-22 — "MANTENER EL N%" DE VERDAD. Osmar: *"si dice mantener 50% el sistema lo tiene que
    // mantener en 50%"*. Hasta hoy el precio sólo se recalculaba cuando cambiaba el costo del producto;
    // si lo que cambiaba era lo que cobra MeLi (comisión, envío) la publicación quedaba abajo y la
    // pantalla seguía diciendo "mantener 50%" (caso real: cajas Megacol dejando 7%).
    // Ahora, a las que tienen el sincro de precio prendido con objetivo, y quedaron más de
    // TOLERANCIA_PUNTOS abajo, se les vuelve a aplicar el precio con el motor de siempre (nunca abajo
    // del OEM, con el candado anti-precio-absurdo y el escalón del envío). Las que están en promoción
    // NO se tocan (cambiar el precio les saca el descuento): de esas sólo se avisa, como antes.
    // KILL SWITCH: AppSettings["meli.mantener_objetivo.enabled"] = "true" para prenderlo (default apagado).
    private const decimal TOLERANCIA_PUNTOS = 2m;
    private const int MAX_CORRECCIONES_POR_NOCHE = 300;
    // Si para volver al % habría que subir más de esto, casi siempre es un dato mal cargado (costo,
    // envío): no se toca sola y se avisa para revisar. Medido el 22/09: 2 de 73 (una caja +73%).
    private const decimal MAX_SUBA_AUTOMATICA = 0.30m;

    private static async Task<bool> MantenerPrendidoAsync(AppDbContext db, CancellationToken ct)
    {
        var s = await db.AppSettings.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Key == "meli.mantener_objetivo.enabled", ct);
        var v = s?.Value?.Trim().ToLowerInvariant();
        return v is "true" or "1" or "on";
    }

    private async Task RevisarAsync(IServiceProvider sp, AppDbContext db, DateTime ahora, CancellationToken ct)
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
                m.Id, m.MeliItemId, m.MeliAccountId, m.Sku, m.Title, m.Price,
                m.SaleFeeAmount, m.SaleFeeShippingCost, m.CafeProductoId, m.PromoPrecio,
                m.SaleFeePercentageFee, m.SaleFeeFixedFee
            })
            .ToListAsync(ct);

        // Las vinculadas directo a un producto (sin receta) también tienen costo.
        var idsDirectos = activas.Where(a => a.CafeProductoId.HasValue && !costos.ContainsKey(a.MeliItemId))
            .Select(a => a.CafeProductoId!.Value).Distinct().ToList();
        var costoDirecto = idsDirectos.Count == 0 ? new Dictionary<int, decimal>()
            : await db.CafeProductos.AsNoTracking().Where(p => idsDirectos.Contains(p.Id))
                .ToDictionaryAsync(p => p.Id, p => p.Costo, ct);
        foreach (var a in activas)
            if (!costos.ContainsKey(a.MeliItemId) && a.CafeProductoId is int pid && costoDirecto.TryGetValue(pid, out var cd))
                costos[a.MeliItemId] = cd;

        var mantener = await MantenerPrendidoAsync(db, ct);
        var conSincro = mantener
            ? (await db.MeliItemSyncConfigs.AsNoTracking()
                .Where(c => c.SyncPrecio && c.GananciaObjetivoPct != null && c.GananciaObjetivoPct > 0)
                .Select(c => c.MeliItemId).ToListAsync(ct)).ToHashSet()
            : new HashSet<string>();
        var aCorregir = new List<(int Id, string Mla, int Cuenta, string? Sku, string? Titulo, decimal Precio, decimal Margen, decimal Objetivo, decimal Costo)>();

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

            // Tiene "Mantener el N%" y se corrió para abajo: se corrige (salvo promoción).
            if (conSincro.Contains(m.MeliItemId) && margen < piso - TOLERANCIA_PUNTOS && m.PromoPrecio is not > 0)
            {
                // Estimación rápida con la comisión guardada, sólo para el freno de la suba.
                var pct = (m.SaleFeePercentageFee ?? 0m) / 100m;
                var estimado = pct < 0.95m
                    ? (costo * (1 + piso / 100m) * IVA + (m.SaleFeeFixedFee ?? 0m) + (m.SaleFeeShippingCost ?? 0m)) / (1 - pct)
                    : decimal.MaxValue;
                if (estimado <= m.Price * (1 + MAX_SUBA_AUTOMATICA))
                {
                    aCorregir.Add((m.Id, m.MeliItemId, m.MeliAccountId, m.Sku, m.Title, m.Price, margen, piso, costo));
                    continue;
                }
                // Sube demasiado: cae al aviso de siempre (abajo), con la aclaración.
            }
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

        // Correcciones de "Mantener el N%": las más lejos del objetivo primero.
        int corregidas = 0, fallidas = 0;
        if (aCorregir.Count > 0)
        {
            var push = sp.GetRequiredService<MeliPricePushService>();
            foreach (var c in aCorregir.OrderBy(x => x.Margen - x.Objetivo).Take(MAX_CORRECCIONES_POR_NOCHE))
            {
                if (ct.IsCancellationRequested) break;
                MeliPricePushService.PushResult r;
                try { r = await push.PushPrecioForItemAsync(c.Id, markAsClaimed: false, ct); }
                catch (Exception ex) { r = new MeliPricePushService.PushResult(false, ex.Message); }

                var arS = new System.Globalization.CultureInfo("es-AR");
                if (r.Ok && r.PushedPrice is decimal nuevo)
                {
                    corregidas++;
                    db.MeliCambiosDetectados.Add(new MeliCambioDetectado
                    {
                        MeliItemId = c.Mla, MeliAccountId = c.Cuenta, Sku = c.Sku, Title = c.Titulo,
                        Tipo = "PRECIO_MANTENIDO",
                        ValorAnterior = c.Precio.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        ValorNuevo = nuevo.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Delta = c.Objetivo, DeltaPct = c.Margen,
                        Source = "mantener", DetectedAt = ahora,
                        Notes = $"Dejaba {c.Margen.ToString("0.#", arS)}% y tiene «Mantener el {c.Objetivo.ToString("0.#", arS)}%»: "
                              + $"${c.Precio.ToString("N0", arS)} → ${nuevo.ToString("N0", arS)}"
                    });
                }
                else
                {
                    fallidas++;
                    db.MeliCambiosDetectados.Add(new MeliCambioDetectado
                    {
                        MeliItemId = c.Mla, MeliAccountId = c.Cuenta, Sku = c.Sku, Title = c.Titulo,
                        Tipo = "MARGEN_BAJO",
                        ValorAnterior = c.Objetivo.ToString("0", System.Globalization.CultureInfo.InvariantCulture),
                        ValorNuevo = c.Precio.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Delta = c.Costo, DeltaPct = c.Margen, Source = "mantener", DetectedAt = ahora,
                        Notes = $"Tiene «Mantener» pero no se pudo corregir el precio: {r.Message}"
                    });
                }
                await db.SaveChangesAsync(ct);
                try { await Task.Delay(1500, ct); } catch (OperationCanceledException) { break; }
            }
            _logger.LogWarning("[Mantener objetivo] {Ok} corregidas, {Err} no se pudieron (de {Total} abajo)",
                corregidas, fallidas, aCorregir.Count);
        }

        var resumen = $"{nuevos.Count} abajo del piso, {aAvisar.Count} avisadas"
                      + (mantener ? $", mantener: {corregidas} corregidas / {fallidas} con error" : "");
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
