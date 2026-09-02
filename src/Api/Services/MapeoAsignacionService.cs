using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-02: UN SOLO lugar que sincroniza "quién lleva esta parada" desde el mapa hacia el
/// CELULAR del repartidor.
///
/// El mapa y el celu son dos listas distintas. El mapa guarda el recorrido (MapeoStops); el celu
/// muestra lo que el repartidor tiene CARGADO: Cafe_QrEscaneos para las ventas, Alq_QrEscaneos para
/// los alquileres y MeliShipments.RepartidorAsignadoId para los ME1. Antes las dos listas solo se
/// cruzaban cuando el repartidor escaneaba el QR del comprobante, o cuando se asignaba el chofer
/// parada por parada (y ahí, solo para ventas). Al elegir los choferes por zona al final del ruteo
/// —el flujo normal— la parada quedaba en el mapa pero el celu ni la mostraba.
///
/// Ahora EL MAPA MANDA: asignarle un repartidor a la parada la carga en su celu.
///
/// Regla al DESASIGNAR: solo se deshace lo que hizo el mapa (las cargas con Ip = "mapeo-asignar").
/// Nunca se borra un escaneo de QR del repartidor ni una asignación hecha a mano en otra pantalla,
/// así "limpiar la ruta" en el mapa no le vacía el celu a nadie. Los ME1 no se desasignan desde el
/// mapa: eso se sigue manejando en /meli/me1/entregas.
/// </summary>
public class MapeoAsignacionService
{
    /// <summary>Marca de las cargas hechas por el mapa — es lo único que el mapa puede deshacer.</summary>
    public const string MarcaMapa = "mapeo-asignar";

    private readonly AppDbContext _db;
    public MapeoAsignacionService(AppDbContext db) => _db = db;

    /// <summary>
    /// Sincroniza las paradas indicadas con el celu del repartidor que tengan asignado (o se lo saca,
    /// si quedaron sin chofer). Devuelve cuántas cambiaron algo.
    /// </summary>
    public async Task<int> SincronizarAsync(IReadOnlyCollection<int> stopIds)
    {
        if (stopIds.Count == 0) return 0;
        var stops = await _db.MapeoStops.Where(s => stopIds.Contains(s.Id)).ToListAsync();
        return await SincronizarStopsAsync(stops);
    }

    /// <summary>Igual que <see cref="SincronizarAsync"/> pero con las paradas ya cargadas.</summary>
    public async Task<int> SincronizarStopsAsync(IReadOnlyCollection<MapeoStop> stops)
    {
        if (stops.Count == 0) return 0;

        // Chofer del mapa -> repartidor REAL. Si el chofer del mapa no está vinculado a un repartidor
        // (lo crearon a mano dentro del mapa), no hay celu al que mandarle nada.
        var driverIds = stops.Where(s => s.AssignedDriverId.HasValue)
            .Select(s => s.AssignedDriverId!.Value).Distinct().ToList();
        var repPorDriver = driverIds.Count == 0
            ? new Dictionary<int, int>()
            : await _db.MapeoDrivers
                .Where(d => driverIds.Contains(d.Id) && d.CafeRepartidorId != null)
                .ToDictionaryAsync(d => d.Id, d => d.CafeRepartidorId!.Value);

        var repIds = repPorDriver.Values.Distinct().ToList();
        var repsActivos = repIds.Count == 0
            ? new HashSet<int>()
            : (await _db.CafeRepartidores.Where(r => repIds.Contains(r.Id) && r.IsActive)
                .Select(r => r.Id).ToListAsync()).ToHashSet();

        int cambios = 0;
        foreach (var s in stops)
        {
            int? repId = null;
            if (s.AssignedDriverId.HasValue
                && repPorDriver.TryGetValue(s.AssignedDriverId.Value, out var rid)
                && repsActivos.Contains(rid))
                repId = rid;

            bool cambio = s.Origin switch
            {
                "venta_cafe" => await SyncVentaAsync(s, repId),
                "alquiler" => await SyncAlquilerAsync(s, repId),
                "me1" => await SyncMe1Async(s, repId),
                _ => false   // flex (se cierra en la app de MeLi), manual, favorito, visita: no hay lista que cargar
            };
            if (cambio) cambios++;
        }

        if (cambios > 0) await _db.SaveChangesAsync();
        return cambios;
    }

    /// <summary>
    /// Venta de café (cotización X, FA, FB o FC — es lo mismo). Mismo criterio que el escaneo del QR:
    /// "el último que la agarra se la queda". No toca ventas ya entregadas.
    /// </summary>
    private async Task<bool> SyncVentaAsync(MapeoStop s, int? repId)
    {
        if (!int.TryParse(s.OriginRefId, out var ventaId)) return false;
        var venta = await _db.CafeVentas.FirstOrDefaultAsync(v => v.Id == ventaId);
        if (venta is null || venta.EntregadoPorRepartidorId.HasValue) return false;

        var cargados = await _db.CafeQrEscaneos
            .Where(e => e.VentaId == ventaId && e.Accion == "cargado")
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .ToListAsync();

        if (repId is null)
        {
            var delMapa = cargados.Where(e => e.Ip == MarcaMapa).ToList();
            if (delMapa.Count == 0) return false;
            _db.CafeQrEscaneos.RemoveRange(delMapa);
            return true;
        }

        if (cargados.FirstOrDefault()?.RepartidorId == repId) return false; // ya es suya
        _db.CafeQrEscaneos.Add(new CafeQrEscaneo
        {
            VentaId = ventaId,
            RepartidorId = repId.Value,
            Accion = "cargado",
            CreatedAt = DateTime.UtcNow,
            Ip = MarcaMapa
        });
        return true;
    }

    /// <summary>
    /// Alquiler. Son DOS etapas con dueño propio: mientras no se entregó se asigna la ENTREGA
    /// ('cargado'); una vez entregado, lo que se asigna es el RETIRO ('cargado_retiro'). Mismo
    /// criterio que el botón "asignar reparto" del panel de alquileres.
    /// </summary>
    private async Task<bool> SyncAlquilerAsync(MapeoStop s, int? repId)
    {
        if (!int.TryParse(s.OriginRefId, out var reservaId)) return false;
        var r = await _db.AlqReservas.FirstOrDefaultAsync(x => x.Id == reservaId);
        if (r is null) return false;

        var esRetiro = r.EntregadoPorRepartidorId.HasValue;
        if (esRetiro && r.RetiradoPorRepartidorId.HasValue) return false; // entregada y retirada: terminó
        var accion = esRetiro ? "cargado_retiro" : "cargado";

        var cargados = await _db.AlqQrEscaneos
            .Where(e => e.ReservaId == reservaId && e.Accion == accion)
            .OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id)
            .ToListAsync();

        if (repId is null)
        {
            var delMapa = cargados.Where(e => e.Ip == MarcaMapa).ToList();
            if (delMapa.Count == 0) return false;
            _db.AlqQrEscaneos.RemoveRange(delMapa);
            return true;
        }

        if (cargados.FirstOrDefault()?.RepartidorId == repId) return false;
        _db.AlqQrEscaneos.Add(new AlqQrEscaneo
        {
            ReservaId = reservaId,
            RepartidorId = repId.Value,
            Accion = accion,
            CreatedAt = DateTime.UtcNow,
            Ip = MarcaMapa
        });
        return true;
    }

    /// <summary>
    /// ME1 de MercadoLibre. El mapa manda sobre la asignación (pisa lo elegido en /meli/me1/entregas),
    /// pero NO desasigna: no hay forma de distinguir una asignación hecha a mano en esa pantalla, y
    /// borrarla dejaría el envío sin dueño sin que nadie se entere.
    /// </summary>
    private async Task<bool> SyncMe1Async(MapeoStop s, int? repId)
    {
        if (repId is null) return false;
        if (!long.TryParse(s.OriginRefId, out var shipmentId)) return false;
        var ship = await _db.MeliShipments.FirstOrDefaultAsync(m => m.MeliShipmentId == shipmentId);
        if (ship is null) return false;
        if (ship.EntregadoPorRepartidorId.HasValue) return false;
        if (string.Equals(ship.Status, "delivered", StringComparison.OrdinalIgnoreCase)) return false;
        if (ship.RepartidorAsignadoId == repId) return false;

        ship.RepartidorAsignadoId = repId;
        return true;
    }
}
