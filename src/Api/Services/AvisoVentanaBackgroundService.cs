using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// 2026-08-13: Robot de "avisos de cierre de ventana de WhatsApp". Cada 2 minutos mira todas las
/// conversaciones de WhatsApp con la ventana de 24hs ABIERTA (último entrante hace menos de 24hs) y,
/// por cada regla activa, avisa cuando el tiempo restante cruza uno de los momentos configurados
/// (12h/6h/2h/1h/15min, editable). Avisa UNA sola vez por momento y por ventana:
///   - si el cliente vuelve a escribir, la ventana se renueva y los avisos se re-arman solos;
///   - si el sistema quedó atrás y cruzó varios momentos de golpe, manda SOLO el más urgente
///     (anti-spam) y marca los demás como ya avisados.
///
/// Mismo andamiaje que MisAlertasBackgroundService (BackgroundService + while + Task.Delay + scope por tick).
/// </summary>
public class AvisoVentanaBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AvisoVentanaBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private const double VENTANA_MIN = 24 * 60; // 24hs en minutos

    public AvisoVentanaBackgroundService(IServiceScopeFactory scopeFactory, ILogger<AvisoVentanaBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "[AvisoVentana] error en el ciclo (no critico)"); }
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static string DedupKey(int reglaId, string numero, string? linea, DateTime inicio, int umbral)
        => $"{reglaId}|{numero}|{linea ?? ""}|{inicio.Ticks}|{umbral}";

    private async Task TickAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var reglas = await db.WhatsAppAvisoVentanaReglas.Where(r => r.Activa).ToListAsync();
        if (reglas.Count == 0) return;

        var ahora = DateTime.UtcNow;
        var desde = ahora.AddHours(-24);

        // 1) Conversaciones con ventana ABIERTA: agrupo los mensajes ENTRANTES de las últimas 24hs por
        //    (Numero, Linea) y me quedo con el más nuevo = inicio de la ventana. (Instagram tiene otra
        //    regla de ventana → lo excluyo.)
        var entrantes = await db.WhatsAppTwilioMensajes.AsNoTracking()
            .Where(m => m.Direccion == "INCOMING" && m.CreatedAt >= desde && !m.Numero.StartsWith("ig:"))
            .Select(m => new { m.Numero, m.LineaPhoneId, m.CreatedAt, m.NombrePerfil })
            .ToListAsync();
        if (entrantes.Count == 0) return;

        var convos = entrantes
            .GroupBy(m => new { m.Numero, m.LineaPhoneId })
            .Select(g =>
            {
                var ordenados = g.OrderByDescending(x => x.CreatedAt).ToList();
                return new Convo(
                    g.Key.Numero,
                    g.Key.LineaPhoneId,
                    ordenados[0].CreatedAt,
                    ordenados.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.NombrePerfil))?.NombrePerfil);
            })
            .ToList();

        // 2) Estados de conversación (para saltear las finalizadas / archivadas sin novedad).
        var estados = await db.WhatsAppConversaciones.AsNoTracking()
            .Select(c => new { c.Numero, c.LineaPhoneId, c.Estado, c.ArchivadoAt })
            .ToListAsync();
        var estadoPorConvo = estados
            .GroupBy(c => $"{c.Numero}|{c.LineaPhoneId ?? ""}")
            .ToDictionary(g => g.Key, g => g.First());

        // 3) Nombres lindos de cada línea (para el comodín {linea}).
        var lineasRaw = await db.AppSettings.AsNoTracking()
            .Where(s => s.Key.StartsWith("whatsapp.linea."))
            .Select(s => new { s.Key, s.Value }).ToListAsync();
        var lineaNombreCfg = await db.WhatsAppLineasConfig.AsNoTracking().ToDictionaryAsync(c => c.LineaId, c => c.Nombre);
        var lineaLabel = new Dictionary<string, string>();
        foreach (var l in lineasRaw)
        {
            var phoneId = l.Key.Substring("whatsapp.linea.".Length);
            var nombre = lineaNombreCfg.TryGetValue(phoneId, out var n) && !string.IsNullOrWhiteSpace(n) ? n : (l.Value ?? phoneId);
            lineaLabel[phoneId] = nombre;
        }

        // 4) Registro anti-repetición ya existente (para no re-avisar).
        var ruleIds = reglas.Select(r => r.Id).ToList();
        var enviados = await db.WhatsAppAvisoVentanaEnviados.AsNoTracking()
            .Where(e => ruleIds.Contains(e.ReglaId)).ToListAsync();
        var yaAvisado = new HashSet<string>(enviados.Select(e =>
            DedupKey(e.ReglaId, e.Numero, e.LineaPhoneId, e.VentanaInicio, e.UmbralMin)));

        // 5) Destinatarios internos por regla (personas de la libretita).
        var destPorRegla = (await db.AutoDestinatarios.AsNoTracking()
                .Where(d => d.AutoKey.StartsWith("waventana:")).ToListAsync())
            .GroupBy(d => d.AutoKey)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PersonaId).ToHashSet());
        var personas = await db.AutoPersonas.AsNoTracking().Where(p => p.Activo).ToListAsync();

        var wa = scope.ServiceProvider.GetRequiredService<WhatsAppOutboundService>();
        var nuevosEnviados = new List<WhatsAppAvisoVentanaEnviado>();

        foreach (var regla in reglas)
        {
            var umbrales = regla.UmbralesMin
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).Distinct().ToList();
            if (umbrales.Count == 0) continue;

            // ¿A qué conversaciones aplica esta regla?
            var soloNumeros = string.IsNullOrWhiteSpace(regla.SoloNumeros)
                ? null
                : regla.SoloNumeros.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(SoloDigitos).Where(x => x.Length > 0).ToHashSet();

            // Destinatarios internos (solo aplica a Destino=INTERNO).
            destPorRegla.TryGetValue($"waventana:{regla.Id}", out var idsDest);
            var recipientes = regla.Destino == "INTERNO"
                ? personas.Where(p => idsDest != null && idsDest.Contains(p.Id) && !string.IsNullOrWhiteSpace(p.WhatsAppNumero)).ToList()
                : new List<AutoPersona>();
            if (regla.Destino == "INTERNO" && recipientes.Count == 0) continue; // nadie a quién avisar

            foreach (var c in convos)
            {
                if (regla.WatchLineaPhoneId != null && c.Linea != regla.WatchLineaPhoneId) continue;
                if (soloNumeros != null && !soloNumeros.Contains(SoloDigitos(c.Numero))) continue;

                // Saltear finalizadas / archivadas (sin mensaje nuevo posterior al archivado).
                if (estadoPorConvo.TryGetValue($"{c.Numero}|{c.Linea ?? ""}", out var est))
                {
                    if (string.Equals(est.Estado, "finalizada", StringComparison.OrdinalIgnoreCase)) continue;
                    if (est.ArchivadoAt != null && est.ArchivadoAt >= c.Inicio) continue;
                }

                var minutosRestan = (int)Math.Floor(VENTANA_MIN - (ahora - c.Inicio).TotalMinutes);
                if (minutosRestan <= 0) continue;

                var cruzados = umbrales.Where(u => minutosRestan <= u).OrderBy(u => u).ToList();
                if (cruzados.Count == 0) continue;

                var pendientes = cruzados.Where(u => !yaAvisado.Contains(DedupKey(regla.Id, c.Numero, c.Linea, c.Inicio, u))).ToList();
                if (pendientes.Count == 0) continue;

                var aEnviar = pendientes.Min(); // el más urgente

                var lineaMostrar = c.Linea != null && lineaLabel.TryGetValue(c.Linea, out var ll) ? ll : "WhatsApp";
                // {tiempo} = el tiempo REAL que queda en la ventana (no el umbral). Así nunca dice
                // "12 horas" cuando en verdad quedan 2h. Redondeado a 5 min para que quede prolijo.
                var texto = regla.Mensaje
                    .Replace("{cliente}", NombreCliente(c))
                    .Replace("{tiempo}", TiempoLindo(minutosRestan))
                    .Replace("{linea}", lineaMostrar);

                try
                {
                    if (regla.Destino == "CLIENTE")
                    {
                        // La ventana está abierta (minutosRestan>0) → texto libre OK; sale por SU misma línea.
                        var (sid, canal, lin) = await wa.SendTextAsync(c.Numero, texto, lineaOverride: c.Linea);
                        if (sid != null)
                            db.WhatsAppTwilioMensajes.Add(Saliente(c.Numero, texto, sid, canal, lin));
                    }
                    else // INTERNO
                    {
                        foreach (var per in recipientes)
                        {
                            var numero = per.WhatsAppNumero!.StartsWith("whatsapp:") ? per.WhatsAppNumero : "whatsapp:" + per.WhatsAppNumero;
                            var (sid, canal, lin) = await wa.SendTextAsync(numero, texto, lineaOverride: regla.SaleLineaPhoneId);
                            if (sid != null)
                                db.WhatsAppTwilioMensajes.Add(Saliente(numero, texto, sid, canal, lin));
                        }
                    }
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[AvisoVentana] no pude enviar aviso de regla {Id} a {Num}", regla.Id, c.Numero); }

                // Marco como avisados TODOS los momentos ya cruzados (así los mayores no disparan después
                // fuera de orden). Los momentos más chicos que todavía no se cruzaron dispararán a su hora.
                foreach (var u in cruzados)
                {
                    var k = DedupKey(regla.Id, c.Numero, c.Linea, c.Inicio, u);
                    if (yaAvisado.Add(k))
                        nuevosEnviados.Add(new WhatsAppAvisoVentanaEnviado
                        {
                            ReglaId = regla.Id, Numero = c.Numero, LineaPhoneId = c.Linea,
                            VentanaInicio = c.Inicio, UmbralMin = u, EnviadoAt = DateTime.UtcNow
                        });
                }
            }
        }

        if (nuevosEnviados.Count > 0) db.WhatsAppAvisoVentanaEnviados.AddRange(nuevosEnviados);
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync();

        // Limpieza: el registro anti-repetición se guarda 7 días (después la ventana ya cerró hace rato).
        try
        {
            var limite = DateTime.UtcNow.AddDays(-7);
            await db.WhatsAppAvisoVentanaEnviados.Where(e => e.EnviadoAt < limite).ExecuteDeleteAsync();
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[AvisoVentana] no pude limpiar registro viejo"); }
    }

    /// <summary>2026-08-13: "línea base" al crear o PRENDER una regla. Marca como YA avisados (SIN enviar)
    /// los umbrales que la regla ya tiene cruzados en las conversaciones abiertas AHORA. Así, al activar una
    /// regla, NO se dispara retroactivamente por todo el backlog de charlas que ya estaban pasadas de un
    /// umbral (eso causaba una avalancha de avisos). Solo se avisará por los cruces que pasen de acá en más.</summary>
    public static async Task BaselineReglaAsync(AppDbContext db, WhatsAppAvisoVentanaRegla regla)
    {
        var umbrales = regla.UmbralesMin
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => int.TryParse(x, out var v) ? v : 0).Where(v => v > 0).Distinct().ToList();
        if (umbrales.Count == 0) return;

        var ahora = DateTime.UtcNow;
        var desde = ahora.AddHours(-24);
        var entrantes = await db.WhatsAppTwilioMensajes.AsNoTracking()
            .Where(m => m.Direccion == "INCOMING" && m.CreatedAt >= desde && !m.Numero.StartsWith("ig:"))
            .Select(m => new { m.Numero, m.LineaPhoneId, m.CreatedAt })
            .ToListAsync();
        if (entrantes.Count == 0) return;

        var convos = entrantes.GroupBy(m => new { m.Numero, m.LineaPhoneId })
            .Select(g => new { g.Key.Numero, g.Key.LineaPhoneId, Inicio = g.Max(x => x.CreatedAt) })
            .ToList();

        var soloNumeros = string.IsNullOrWhiteSpace(regla.SoloNumeros) ? null
            : regla.SoloNumeros.Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(SoloDigitos).Where(x => x.Length > 0).ToHashSet();

        var yaKeys = (await db.WhatsAppAvisoVentanaEnviados.AsNoTracking()
                .Where(e => e.ReglaId == regla.Id)
                .Select(e => new { e.Numero, e.LineaPhoneId, e.VentanaInicio, e.UmbralMin }).ToListAsync())
            .Select(e => DedupKey(regla.Id, e.Numero, e.LineaPhoneId, e.VentanaInicio, e.UmbralMin)).ToHashSet();

        var nuevos = new List<WhatsAppAvisoVentanaEnviado>();
        foreach (var c in convos)
        {
            if (regla.WatchLineaPhoneId != null && c.LineaPhoneId != regla.WatchLineaPhoneId) continue;
            if (soloNumeros != null && !soloNumeros.Contains(SoloDigitos(c.Numero))) continue;
            var minutosRestan = (int)Math.Floor(VENTANA_MIN - (ahora - c.Inicio).TotalMinutes);
            if (minutosRestan <= 0) continue;
            foreach (var u in umbrales.Where(u => minutosRestan <= u))
            {
                var k = DedupKey(regla.Id, c.Numero, c.LineaPhoneId, c.Inicio, u);
                if (yaKeys.Add(k))
                    nuevos.Add(new WhatsAppAvisoVentanaEnviado
                    {
                        ReglaId = regla.Id, Numero = c.Numero, LineaPhoneId = c.LineaPhoneId,
                        VentanaInicio = c.Inicio, UmbralMin = u, EnviadoAt = DateTime.UtcNow
                    });
            }
        }
        if (nuevos.Count > 0) { db.WhatsAppAvisoVentanaEnviados.AddRange(nuevos); await db.SaveChangesAsync(); }
    }

    private static WhatsAppTwilioMensaje Saliente(string numero, string texto, string sid, string canal, string? linea) => new()
    {
        Direccion = "OUTGOING", Numero = numero, Cuerpo = texto,
        TwilioMessageSid = sid, Canal = canal, LineaPhoneId = linea, Procesado = true, CreatedAt = DateTime.UtcNow
    };

    private static string NombreCliente(Convo c)
    {
        if (!string.IsNullOrWhiteSpace(c.Nombre)) return c.Nombre!;
        var n = c.Numero.Replace("whatsapp:", "").Replace("+", "");
        return string.IsNullOrWhiteSpace(n) ? "el cliente" : n;
    }

    /// <summary>Deja solo dígitos (para comparar números escritos con o sin +, espacios, etc.).</summary>
    private static string SoloDigitos(string? s) => new string((s ?? "").Where(char.IsDigit).ToArray());

    /// <summary>Tiempo REAL restante, redondeado a 5 min para que quede prolijo (ej 358→"6 horas",
    /// 132→"2h 10min", 12→"10 minutos"). Nunca baja de 1 min. Público para reusarlo en el "Probar".</summary>
    public static string TiempoLindo(int min)
    {
        var m = (int)Math.Round(min / 5.0) * 5;
        if (m < 5) m = Math.Max(1, min);
        return Humanizar(m);
    }

    /// <summary>Convierte minutos a un texto lindo: 720→"12 horas", 60→"1 hora", 15→"15 minutos".</summary>
    private static string Humanizar(int min)
    {
        if (min % 60 == 0)
        {
            var h = min / 60;
            return h == 1 ? "1 hora" : $"{h} horas";
        }
        if (min < 60) return $"{min} minutos";
        return $"{min / 60}h {min % 60}min";
    }

    private record Convo(string Numero, string? Linea, DateTime Inicio, string? Nombre);
}
