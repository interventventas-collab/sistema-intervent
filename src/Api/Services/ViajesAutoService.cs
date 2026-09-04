using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-04: cuenta SOLO los viajes del repartidor que cobra por entrega (Nacho, que va con su
/// propio auto y cobra $8.500 por cada dirección donde entrega).
///
/// Antes el repartidor tenía que entrar a su link y tipear cuántos viajes había hecho — nadie lo
/// hacía, así que la pantalla de Viajes quedaba en cero. Toda la información ya estaba en el mapa:
/// cada parada sabe de qué día es, a qué chofer se le asignó y si se entregó. Esto la convierte en
/// plata.
///
/// Reglas (las definió el usuario el 04/09):
///   - una entrega = un viaje, con tarifa PLANA (da igual Flex, venta de café, CABA o Provincia);
///   - la que no se pudo entregar NO cuenta;
///   - el viaje cae en el DÍA DEL REPARTO, aunque MercadoLibre confirme la entrega a la noche;
///   - la tarifa se congela al contar el viaje: subirle el precio no recalcula lo viejo;
///   - lo ya liquidado no se toca nunca más.
///
/// Quién sabe si una parada se entregó: <see cref="MapeoEntregasService"/>, el mismo lugar que usan
/// el mapa y el tablero. Ojo: casi ningún repartidor tilda en el celu — las entregas se dan por
/// hechas porque MercadoLibre las confirma o porque la venta de café quedó marcada como entregada.
/// </summary>
public class ViajesAutoService
{
    private readonly AppDbContext _db;
    private readonly MapeoEntregasService _entregas;

    /// <summary>Cuántos días para atrás se revisan en cada sincronización. Alcanza de sobra para
    /// agarrar las confirmaciones tardías de MeLi sin barrer toda la historia en cada pantallazo.</summary>
    private const int DiasVentana = 30;

    public ViajesAutoService(AppDbContext db, MapeoEntregasService entregas)
    {
        _db = db;
        _entregas = entregas;
    }

    public static DateTime HoyAr() => DateTime.UtcNow.AddHours(-3).Date;

    /// <summary>Pone al día los viajes de TODOS los empleados en modo automático.</summary>
    public async Task SincronizarTodosAsync()
    {
        var emps = await _db.ViajesEmpleados
            .Where(e => e.ModoAutomatico && e.MapeoDriverId != null)
            .ToListAsync();
        foreach (var e in emps) await SincronizarAsync(e);
    }

    /// <summary>
    /// Pone al día los viajes de un empleado: agrega los que faltan, borra los que dejaron de estar
    /// entregados (o cuya parada se borró del mapa) y respeta lo ya liquidado.
    /// Devuelve cuántos viajes quedaron agregados menos los quitados (informativo).
    /// </summary>
    public async Task<int> SincronizarAsync(ViajesEmpleado emp)
    {
        if (!emp.ModoAutomatico || emp.MapeoDriverId is null) return 0;

        var hoy = HoyAr();
        var desde = hoy.AddDays(-DiasVentana);

        var stops = await _db.MapeoStops
            .Where(s => s.AssignedDriverId == emp.MapeoDriverId
                     && s.FechaReparto >= desde && s.FechaReparto <= hoy)
            .ToListAsync();

        var entregadas = await _entregas.EntregasAsync(stops);

        // Lo que ya está contado en la ventana (los ajustes a mano no entran: StopId NULL).
        var yaContadas = await _db.ViajesEntregas
            .Where(e => e.EmpleadoId == emp.Id && e.StopId != null && e.Fecha >= desde)
            .ToListAsync();
        var porStop = yaContadas.ToDictionary(e => e.StopId!.Value);

        var ahora = DateTime.UtcNow;
        var cambios = 0;

        foreach (var s in stops)
        {
            var entregadoAt = entregadas.TryGetValue(s.Id, out var at) ? at : null;
            var yaEsta = porStop.TryGetValue(s.Id, out var reg);

            if (entregadoAt is null)
            {
                // Estaba contada y dejó de estarlo (la desmarcaron, MeLi la devolvió): se quita,
                // salvo que ya se le haya pagado — la plata pagada no se deshace sola.
                if (yaEsta && reg!.LiquidadoPagoId is null)
                {
                    _db.ViajesEntregas.Remove(reg);
                    cambios--;
                }
                continue;
            }

            if (!yaEsta)
            {
                _db.ViajesEntregas.Add(new ViajesEntrega
                {
                    EmpleadoId = emp.Id,
                    StopId = s.Id,
                    Fecha = s.FechaReparto.Date,
                    Tarifa = emp.TarifaViaje,
                    Origen = s.Origin,
                    Direccion = Recortar(s.Direccion, 300),
                    Cliente = Recortar(s.ContactName ?? s.Alias, 150),
                    EntregadoAt = entregadoAt,
                    CreatedAt = ahora
                });
                cambios++;
                continue;
            }

            // Ya contada: sólo refrescamos los datos de la ficha (dirección, hora, día del reparto
            // si la movieron de fecha). La TARIFA no se toca — quedó congelada al contarla.
            if (reg!.LiquidadoPagoId is null)
            {
                var fecha = s.FechaReparto.Date;
                var dir = Recortar(s.Direccion, 300);
                var cli = Recortar(s.ContactName ?? s.Alias, 150);
                if (reg.Fecha != fecha || reg.Direccion != dir || reg.Cliente != cli
                    || reg.EntregadoAt != entregadoAt || reg.Origen != s.Origin)
                {
                    reg.Fecha = fecha;
                    reg.Direccion = dir;
                    reg.Cliente = cli;
                    reg.EntregadoAt = entregadoAt;
                    reg.Origen = s.Origin;
                    reg.UpdatedAt = ahora;
                }
            }
        }

        // Paradas borradas del mapa: quedaron contadas pero ya no existen. Se quitan si no se pagaron.
        var vivos = stops.Select(s => s.Id).ToHashSet();
        foreach (var reg in yaContadas)
        {
            if (vivos.Contains(reg.StopId!.Value)) continue;
            if (reg.LiquidadoPagoId is not null) continue;
            _db.ViajesEntregas.Remove(reg);
            cambios--;
        }

        if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync();
        return cambios;
    }

    private static string? Recortar(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length <= max ? s : s[..max];
    }
}
