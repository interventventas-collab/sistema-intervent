using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-15: UN SOLO lugar que sabe "¿esta parada ya se entregó, y a qué hora?".
///
/// Cada tipo de parada guarda la entrega en su propia tabla: los envíos de MercadoLibre en
/// MeliShipments, las ventas del café en Cafe_Ventas, los alquileres en Alq_Reservas y las visitas
/// en Visitas. Antes esto se resolvía suelto en el mapa, y por eso el mapa y el dashboard podían
/// mostrar números distintos de lo mismo. Ahora los dos preguntan acá.
///
/// Si mañana aparece un tipo de parada nuevo, se agrega en este archivo y las dos pantallas lo
/// toman solas.
/// </summary>
public class MapeoEntregasService
{
    private readonly AppDbContext _db;
    public MapeoEntregasService(AppDbContext db) => _db = db;

    /// <summary>Por cada parada, cuándo se entregó (null = todavía no).</summary>
    public async Task<Dictionary<int, DateTime?>> EntregasAsync(IReadOnlyCollection<MapeoStop> stops)
    {
        var res = new Dictionary<int, DateTime?>();
        if (stops.Count == 0) return res;
        foreach (var s in stops) res[s.Id] = null;

        static List<long> RefsLong(IEnumerable<MapeoStop> src, params string[] origins) => src
            .Where(s => origins.Contains(s.Origin) && s.OriginRefId != null)
            .Select(s => long.TryParse(s.OriginRefId, out var v) ? v : 0L)
            .Where(v => v != 0L).Distinct().ToList();

        static List<int> RefsInt(IEnumerable<MapeoStop> src, string origin) => src
            .Where(s => s.Origin == origin && s.OriginRefId != null)
            .Select(s => int.TryParse(s.OriginRefId, out var v) ? v : 0)
            .Where(v => v != 0).Distinct().ToList();

        // --- MercadoLibre (Flex y ME1) ---
        var shipRefs = RefsLong(stops, "flex", "me1");
        if (shipRefs.Count > 0)
        {
            var ships = await _db.MeliShipments
                .Where(m => shipRefs.Contains(m.MeliShipmentId))
                .Select(m => new { m.MeliShipmentId, m.Status, m.DateDelivered })
                .ToListAsync();
            var byId = ships.ToDictionary(m => m.MeliShipmentId);
            foreach (var s in stops.Where(x => x.Origin is "flex" or "me1" && x.OriginRefId != null))
            {
                if (!long.TryParse(s.OriginRefId, out var id) || !byId.TryGetValue(id, out var m)) continue;
                // MeLi a veces confirma "delivered" sin mandar la hora exacta: en ese caso la damos
                // por entregada igual, usando la hora en que la parada se actualizó por última vez.
                if (m.DateDelivered.HasValue) res[s.Id] = m.DateDelivered;
                else if (string.Equals(m.Status, "delivered", StringComparison.OrdinalIgnoreCase))
                    res[s.Id] = s.UpdatedAt ?? s.CreatedAt;
            }
        }

        // --- Ventas del café ---
        var ventaRefs = RefsInt(stops, "venta_cafe");
        if (ventaRefs.Count > 0)
        {
            var ventas = await _db.CafeVentas.Where(v => ventaRefs.Contains(v.Id))
                .Select(v => new { v.Id, v.EntregadoAt }).ToListAsync();
            var byId = ventas.ToDictionary(v => v.Id, v => v.EntregadoAt);
            foreach (var s in stops.Where(x => x.Origin == "venta_cafe" && x.OriginRefId != null))
                if (int.TryParse(s.OriginRefId, out var id) && byId.TryGetValue(id, out var at) && at.HasValue)
                    res[s.Id] = at;
        }

        // --- Alquileres ---
        var alqRefs = RefsInt(stops, "alquiler");
        if (alqRefs.Count > 0)
        {
            var alqs = await _db.AlqReservas.Where(r => alqRefs.Contains(r.Id))
                .Select(r => new { r.Id, r.EntregadoAt }).ToListAsync();
            var byId = alqs.ToDictionary(r => r.Id, r => r.EntregadoAt);
            foreach (var s in stops.Where(x => x.Origin == "alquiler" && x.OriginRefId != null))
                if (int.TryParse(s.OriginRefId, out var id) && byId.TryGetValue(id, out var at) && at.HasValue)
                    res[s.Id] = at;
        }

        // --- Visitas ---
        var visitaRefs = RefsInt(stops, "visita");
        if (visitaRefs.Count > 0)
        {
            var vis = await _db.Visitas.Where(v => visitaRefs.Contains(v.Id))
                .Select(v => new { v.Id, v.RealizadaAt }).ToListAsync();
            var byId = vis.ToDictionary(v => v.Id, v => v.RealizadaAt);
            foreach (var s in stops.Where(x => x.Origin == "visita" && x.OriginRefId != null))
                if (int.TryParse(s.OriginRefId, out var id) && byId.TryGetValue(id, out var at) && at.HasValue)
                    res[s.Id] = at;
        }

        // --- Cualquier parada marcada a mano como entregada desde el mapa ---
        foreach (var s in stops)
            if (res[s.Id] is null && string.Equals(s.InternalStatus, "entregado", StringComparison.OrdinalIgnoreCase))
                res[s.Id] = s.UpdatedAt ?? s.CreatedAt;

        return res;
    }

    /// <summary>
    /// 2026-09-02: paradas CERRADAS SIN ENTREGAR — ya no hay nada que hacer con ellas hoy, pero
    /// nunca llegaron al cliente. Cuentan como cerradas para decidir si el repartidor terminó su
    /// recorrido, pero NO se suman a "entregadas": los números tienen que seguir diciendo la verdad.
    ///
    /// Son tres casos:
    ///   - la marcaron a mano en el mapa como cancelada o "no encontró";
    ///   - MercadoLibre avisó que el envío no se entregó (not_delivered / returning_to_sender) o
    ///     que se canceló — son estados FINALES, el envío vuelve al remitente;
    ///   - la venta se anuló.
    /// </summary>
    public async Task<HashSet<int>> NoEntregadasAsync(IReadOnlyCollection<MapeoStop> stops)
    {
        var res = new HashSet<int>();
        if (stops.Count == 0) return res;

        // --- Marcadas a mano en el mapa ---
        foreach (var s in stops)
            if (string.Equals(s.InternalStatus, "cancelado", StringComparison.OrdinalIgnoreCase)
             || string.Equals(s.InternalStatus, "no_encontrado", StringComparison.OrdinalIgnoreCase))
                res.Add(s.Id);

        // --- MercadoLibre avisó que no se entregó ---
        var shipRefs = stops
            .Where(s => (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null)
            .Select(s => long.TryParse(s.OriginRefId, out var v) ? v : 0L)
            .Where(v => v != 0L).Distinct().ToList();
        if (shipRefs.Count > 0)
        {
            var ships = await _db.MeliShipments
                .Where(m => shipRefs.Contains(m.MeliShipmentId))
                .Select(m => new { m.MeliShipmentId, m.Status, m.Substatus })
                .ToListAsync();
            var cerrados = ships
                .Where(m => (m.Status != null && FinalSinEntregar.Contains(m.Status!.ToLowerInvariant()))
                         || VisitaFallida(m.Substatus))
                .Select(m => m.MeliShipmentId).ToHashSet();
            foreach (var s in stops.Where(x => x.Origin is "flex" or "me1" && x.OriginRefId != null))
                if (long.TryParse(s.OriginRefId, out var id) && cerrados.Contains(id))
                    res.Add(s.Id);
        }

        // --- Ventas anuladas ---
        var ventaRefs = stops
            .Where(s => s.Origin == "venta_cafe" && s.OriginRefId != null)
            .Select(s => int.TryParse(s.OriginRefId, out var v) ? v : 0)
            .Where(v => v != 0).Distinct().ToList();
        if (ventaRefs.Count > 0)
        {
            var anuladas = await _db.CafeVentas
                .Where(v => ventaRefs.Contains(v.Id) && v.Estado == "anulado")
                .Select(v => v.Id).ToListAsync();
            var set = anuladas.ToHashSet();
            foreach (var s in stops.Where(x => x.Origin == "venta_cafe" && x.OriginRefId != null))
                if (int.TryParse(s.OriginRefId, out var id) && set.Contains(id))
                    res.Add(s.Id);
        }

        return res;
    }

    /// <summary>Estados de MercadoLibre que cierran el envío sin que haya llegado al cliente.</summary>
    private static readonly HashSet<string> FinalSinEntregar = new()
        { "cancelled", "not_delivered", "returning_to_sender" };

    /// <summary>
    /// 2026-09-02: el detalle con el que MercadoLibre cuenta que el repartidor PASÓ y no pudo
    /// entregar. Ojo: para MeLi el envío sigue "en camino" (lo reintentan), así que el estado
    /// general no alcanza — hay que mirar este detalle. Sin esto, el envío del que ya se sabe que
    /// falló se quedaba contando como pendiente y el recorrido del repartidor nunca cerraba.
    ///
    /// No se escribe nada: es una LECTURA. Si mañana MeLi dice "entregado" porque lo reintentaron,
    /// la parada pasa sola a entregada sin que nadie tenga que corregir nada.
    /// </summary>
    /// 2026-09-02 (mismo día, mirando un caso real): MeLi encadena los estados. El envío de Helguera
    /// pasó de "receiver_absent" a "rescheduled_by_meli" en el mismo día: fueron, no había nadie, y
    /// ellos lo reprogramaron para otro día. Para la ruta de HOY las dos cosas significan lo mismo —
    /// no se entregó y el repartidor no vuelve — así que las dos cierran la parada.
    private static readonly HashSet<string> VisitaFallidaSubstatus = new()
        { "receiver_absent", "buyer_absent", "not_localized", "refused_delivery", "delivery_failed",
          "rescheduled_by_meli", "buyer_rescheduled" };

    public static bool VisitaFallida(string? substatus)
        => substatus != null && VisitaFallidaSubstatus.Contains(substatus.ToLowerInvariant());

    /// <summary>Cómo contarle al usuario, en castellano, por qué MercadoLibre la dio por fallida.</summary>
    public static string? MotivoMeli(string? status, string? substatus)
    {
        var sub = substatus?.ToLowerInvariant();
        var est = status?.ToLowerInvariant();
        if (sub is "receiver_absent" or "buyer_absent") return "MercadoLibre: no había nadie";
        if (sub == "not_localized") return "MercadoLibre: no encontró el domicilio";
        if (sub == "refused_delivery") return "MercadoLibre: no la quisieron recibir";
        if (sub == "delivery_failed") return "MercadoLibre: no se pudo entregar";
        if (sub == "rescheduled_by_meli") return "MercadoLibre: reprogramado, lo reintentan";
        if (sub == "buyer_rescheduled") return "El comprador pidió reprogramarlo";
        if (sub == "returning_to_sender" || est == "not_delivered") return "MercadoLibre: vuelve al remitente";
        if (est == "cancelled") return "MercadoLibre: cancelado";
        return null;
    }
}
