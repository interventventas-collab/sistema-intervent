using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// 2026-09-03: arma solo el mapa de MAÑANA con los envíos de MercadoLibre que ellos prometen para
/// mañana. Pedido del usuario: "¿hay manera de que a medida que entran ventas se vaya armando el
/// mapa de mañana?".
///
/// Se puede porque MeLi nos dice para cuándo promete cada envío (EstimatedDeliveryLimit) y le
/// acierta: sobre 2.101 envíos entregados en 60 días, 1.963 llegaron exactamente el día prometido,
/// 82 un día antes y 42 uno después. O sea, 19 de cada 20.
///
/// ⚠ ARRANCA APAGADO, A PROPÓSITO. Un robot que mete paradas solo es cómodo pero también es la
/// clase de cosa que a las 6 de la mañana te llena el mapa de algo que no esperabas. Se prende
/// poniendo la clave 'mapeo.autoarmar.activo' = "true" en AppSettings (hay un botón en el mapa).
///
/// Nunca toca el día de HOY: solo agrega en el mapa de mañana, y solo lo que falta. Si una parada ya
/// está, no la duplica; si el envío se cancela después, la parada queda marcada con la cruz roja
/// (eso lo resuelve MapeoEntregasService), nunca se borra en silencio.
/// </summary>
public class MapeoAutoArmarBackgroundService : BackgroundService
{
    public const string SettingKey = "mapeo.autoarmar.activo";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MapeoAutoArmarBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(5);

    public MapeoAutoArmarBackgroundService(IServiceScopeFactory scopeFactory, ILogger<MapeoAutoArmarBackgroundService> logger)
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
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                var activo = (await db.AppSettings.FindAsync(new object?[] { SettingKey }, stoppingToken))?.Value;
                if (string.Equals(activo, "true", StringComparison.OrdinalIgnoreCase))
                {
                    var creadas = await ArmarDiaAsync(db, DateTime.UtcNow.AddHours(-3).Date.AddDays(1), stoppingToken);
                    if (creadas > 0) _logger.LogInformation("Mapeo auto-armar: {N} paradas nuevas en el mapa de mañana", creadas);
                }
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Mapeo auto-armar: falló una vuelta, reintento en la próxima"); }

            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Suma al mapa de ese día los envíos de MeLi prometidos para ese día que todavía no están.
    /// Devuelve cuántas paradas creó. Es idempotente: llamarlo dos veces no duplica nada.
    /// </summary>
    public static async Task<int> ArmarDiaAsync(AppDbContext db, DateTime dia, CancellationToken ct = default)
    {
        var d1 = dia.Date.AddHours(3);                 // 00:00 de ese día, en UTC
        var d2 = dia.Date.AddDays(1).AddHours(3);

        var candidatos = await db.MeliShipments
            .Where(s => (s.LogisticType == "self_service" || s.Mode == "me1")
                     && s.Latitude != null && s.Longitude != null
                     && s.Status != "delivered" && s.Status != "cancelled" && s.Status != "not_delivered"
                     && (s.EstimatedDeliveryLimit ?? s.DateCreated) >= d1
                     && (s.EstimatedDeliveryLimit ?? s.DateCreated) < d2)
            .ToListAsync(ct);
        if (candidatos.Count == 0) return 0;

        var refs = candidatos.Select(s => s.MeliShipmentId.ToString()).ToList();
        var yaEstan = await db.MapeoStops
            .Where(s => (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null
                     && refs.Contains(s.OriginRefId) && s.FechaReparto == dia.Date)
            .Select(s => s.OriginRefId!)
            .ToListAsync(ct);
        var set = yaEstan.ToHashSet();

        int creadas = 0;
        foreach (var sh in candidatos)
        {
            var refId = sh.MeliShipmentId.ToString();
            if (set.Contains(refId)) continue;
            db.MapeoStops.Add(new MapeoStop
            {
                Origin = string.Equals(sh.Mode, "me1", StringComparison.OrdinalIgnoreCase) ? "me1" : "flex",
                OriginRefId = refId,
                Alias = sh.ReceiverName,
                Direccion = sh.AddressLine ?? $"{sh.City} CP {sh.ZipCode}",
                Localidad = string.IsNullOrWhiteSpace(sh.City) ? null : sh.City,
                Latitude = sh.Latitude!.Value,
                Longitude = sh.Longitude!.Value,
                ContactName = sh.ReceiverName,
                Telefono = sh.ReceiverPhone,
                Notas = sh.Comment,
                InternalStatus = "pending",
                FechaReparto = dia.Date,
                CreatedAt = DateTime.UtcNow
            });
            creadas++;
        }
        if (creadas > 0) await db.SaveChangesAsync(ct);
        return creadas;
    }
}
