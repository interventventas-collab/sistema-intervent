using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Suma una reserva de alquiler al mapa de reparto como parada. Espejo de <see cref="VentaMapeoService"/>
/// pero para <see cref="AlqReserva"/>. La reserva tiene coords propias del EVENTO, así que la prioridad es:
/// LatitudEvento/LongitudEvento de la reserva → MapeoLink de la reserva → coords del cliente →
/// MapeoLink del cliente → geocoding de la dirección del evento.
///
/// Si el usuario manda dirección/link (cargar en el momento), los resuelve y los GUARDA EN LA RESERVA
/// (no en el cliente, porque el evento puede ser en otra dirección que la casa del cliente).
/// Idempotente por Origin='alquiler' + OriginRefId = reserva.Id.
/// </summary>
public class AlqMapeoService
{
    private readonly AppDbContext _db;
    private readonly GoogleMapsLinkResolverService _mapsResolver;

    public AlqMapeoService(AppDbContext db, GoogleMapsLinkResolverService mapsResolver)
    {
        _db = db;
        _mapsResolver = mapsResolver;
    }

    public class Result
    {
        public bool Ok { get; set; }
        public bool YaEstaba { get; set; }
        public string? Motivo { get; set; }         // "sin_domicilio" | "no_resuelto"
        public string? Mensaje { get; set; }
        public string? Nombre { get; set; }
        public string? Localidad { get; set; }
        public int? ClienteId { get; set; }
        public string? DireccionSugerida { get; set; }
        public int? StopId { get; set; }
    }

    private static string? FirstNonEmpty(params string?[] vals) => vals.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    /// <summary>Suma la reserva al mapa. La reserva debe venir con ClienteNav incluido.</summary>
    public async Task<Result> SumarReservaAsync(AlqReserva r, string? direccion = null, string? link = null)
    {
        var cli = r.ClienteNav;
        var nombre = FirstNonEmpty(cli?.Nombre) ?? "Cliente";
        var dir = FirstNonEmpty(r.DireccionEvento, cli?.DomicilioEntrega, cli?.Direccion);
        var localidad = FirstNonEmpty(cli?.Localidad, cli?.Ciudad);
        var telefono = FirstNonEmpty(cli?.Telefono);

        decimal? lat = null, lng = null;
        bool guardarEnReserva = false;

        if (!string.IsNullOrWhiteSpace(link) || !string.IsNullOrWhiteSpace(direccion))
        {
            // Cargar en el momento.
            if (!string.IsNullOrWhiteSpace(link))
            { var x = await _mapsResolver.TryResolverCoordenadasAsync(link); if (x.HasValue) { lat = x.Value.lat; lng = x.Value.lng; } }
            if (lat is null && !string.IsNullOrWhiteSpace(direccion))
            { var q = direccion + (string.IsNullOrWhiteSpace(localidad) ? "" : ", " + localidad) + ", Argentina";
              var x = await _mapsResolver.TryGeocodeAddressAsync(q); if (x.HasValue) { lat = x.Value.lat; lng = x.Value.lng; } }
            if (lat is null)
                return new Result { Ok = false, Motivo = "no_resuelto", Mensaje = "No pude encontrar esa dirección. Probá con calle + número + localidad, o pegá un link de Google Maps." };
            guardarEnReserva = true;
            if (!string.IsNullOrWhiteSpace(direccion)) dir = direccion.Trim();
            if (!string.IsNullOrWhiteSpace(link)) r.MapeoLink = link.Trim();
        }
        else
        {
            // Resolver automático por prioridad (coords del evento primero).
            if (r.LatitudEvento is not null && r.LongitudEvento is not null) { lat = r.LatitudEvento; lng = r.LongitudEvento; }
            if (lat is null && !string.IsNullOrWhiteSpace(r.MapeoLink))
            { var x = await _mapsResolver.TryResolverCoordenadasAsync(r.MapeoLink); if (x.HasValue) { lat = x.Value.lat; lng = x.Value.lng; guardarEnReserva = true; } }
            if (lat is null && cli?.MapeoLat is not null && cli.MapeoLng is not null) { lat = cli.MapeoLat; lng = cli.MapeoLng; }
            if (lat is null && !string.IsNullOrWhiteSpace(cli?.MapeoLink))
            { var x = await _mapsResolver.TryResolverCoordenadasAsync(cli!.MapeoLink); if (x.HasValue) { lat = x.Value.lat; lng = x.Value.lng; } }
            if (lat is null && !string.IsNullOrWhiteSpace(dir))
            { var q = dir + (string.IsNullOrWhiteSpace(localidad) ? "" : ", " + localidad) + ", Argentina";
              var x = await _mapsResolver.TryGeocodeAddressAsync(q); if (x.HasValue) { lat = x.Value.lat; lng = x.Value.lng; guardarEnReserva = true; } }

            if (lat is null)
                return new Result { Ok = false, Motivo = "sin_domicilio", Mensaje = "Esta reserva no tiene domicilio del evento cargado.",
                    ClienteId = r.ClienteId, Nombre = nombre, DireccionSugerida = dir, Localidad = localidad };
        }

        // Cachear las coords resueltas en la propia reserva para la próxima.
        if (guardarEnReserva && lat is not null && lng is not null)
        {
            r.LatitudEvento = lat;
            r.LongitudEvento = lng;
        }

        var refId = r.Id.ToString();
        var existente = await _db.MapeoStops.FirstOrDefaultAsync(s => s.Origin == "alquiler" && s.OriginRefId == refId);
        if (existente is not null)
        {
            existente.Latitude = lat!.Value;
            existente.Longitude = lng!.Value;
            existente.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return new Result { Ok = true, YaEstaba = true, Mensaje = "Ya estaba en el mapa (actualicé la ubicación).", Nombre = nombre, Localidad = localidad, StopId = existente.Id };
        }

        var stop = new MapeoStop
        {
            Origin = "alquiler",
            OriginRefId = refId,
            Alias = nombre,
            Direccion = string.IsNullOrWhiteSpace(dir) ? nombre : dir!,
            Localidad = localidad,
            Latitude = lat!.Value,
            Longitude = lng!.Value,
            ContactName = nombre,
            Telefono = telefono,
            Notas = $"Alquiler {r.Numero}",
            InternalStatus = "pending",
            CreatedAt = DateTime.UtcNow
        };
        _db.MapeoStops.Add(stop);
        await _db.SaveChangesAsync();
        return new Result { Ok = true, YaEstaba = false, Mensaje = "Agregado al mapa.", Nombre = nombre, Localidad = localidad, StopId = stop.Id };
    }
}
