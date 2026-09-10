using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Suma una reserva de alquiler al mapa de reparto como parada. Espejo de <see cref="VentaMapeoService"/>
/// pero para <see cref="AlqReserva"/>. La reserva tiene coords propias del EVENTO, así que la prioridad es:
/// LatitudEvento/LongitudEvento de la reserva → MapeoLink de la reserva → domicilio de entrega del
/// cliente que coincida con la dirección del evento → coords del cliente → MapeoLink del cliente →
/// geocoding de la dirección del evento.
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

    /// <summary>
    /// 2026-09-10: busca, entre los domicilios de entrega del cliente, el que coincide con la dirección
    /// del evento. Se reconoce igual que en las ventas: la dirección del evento arranca con el texto de
    /// ese domicilio. Devuelve null si el evento es en otro lado (lo normal en un alquiler) o si el
    /// cliente no tiene domicilios cargados.
    /// </summary>
    private async Task<CafeClienteDireccion?> BuscarDomicilioPorDireccionAsync(CafeCliente? cli, string? direccionEvento)
    {
        if (cli is null || string.IsNullOrWhiteSpace(direccionEvento)) return null;
        var alts = await _db.CafeClienteDirecciones.Where(d => d.ClienteId == cli.Id && d.IsActive).ToListAsync();
        return alts.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Direccion)
            && direccionEvento!.StartsWith(d.Direccion, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Suma la reserva al mapa. La reserva debe venir con ClienteNav incluido.</summary>
    public async Task<Result> SumarReservaAsync(AlqReserva r, string? direccion = null, string? link = null, DateTime? fecha = null)
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
            // 2026-09-10: los DOMICILIOS DE ENTREGA del cliente (Cafe_ClienteDirecciones) tienen su propia
            // ubicación cargada y hasta hoy el mapa no los miraba — igual que pasaba con las ventas. Si la
            // dirección del evento es uno de esos domicilios, usamos SU ubicación: es más precisa que la
            // del domicilio de siempre, que puede estar en la otra punta.
            var alt = lat is null ? await BuscarDomicilioPorDireccionAsync(cli, r.DireccionEvento) : null;
            if (lat is null && alt?.MapeoLat is not null && alt.MapeoLng is not null) { lat = alt.MapeoLat; lng = alt.MapeoLng; }
            if (lat is null && !string.IsNullOrWhiteSpace(alt?.MapeoLink))
            { var x = await _mapsResolver.TryResolverCoordenadasAsync(alt!.MapeoLink); if (x.HasValue) { lat = x.Value.lat; lng = x.Value.lng; } }
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

        // 2026-09-03: el alquiler es el caso que mejor se acomoda solo — la reserva YA sabe para qué
        // día es. Si no nos dicen otra cosa, la parada nace en el día de ENTREGA de la reserva (y si
        // esa fecha ya pasó, hoy: sirve para el "me lo olvidé, ponelo igual").
        var hoyAr = DateTime.UtcNow.AddHours(-3).Date;
        var diaEntrega = r.FechaEntrega.Date;
        var dia = (fecha?.Date) ?? (diaEntrega >= hoyAr ? diaEntrega : hoyAr);
        var refId = r.Id.ToString();
        var existente = await _db.MapeoStops.FirstOrDefaultAsync(s => s.Origin == "alquiler" && s.OriginRefId == refId && s.FechaReparto == dia);
        if (existente is not null)
        {
            existente.Latitude = lat!.Value;
            existente.Longitude = lng!.Value;
            existente.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return new Result { Ok = true, YaEstaba = true, Mensaje = $"Ya estaba en el mapa del {dia:dd/MM} (actualicé la ubicación).", Nombre = nombre, Localidad = localidad, StopId = existente.Id };
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
            FechaReparto = dia,
            CreatedAt = DateTime.UtcNow
        };
        _db.MapeoStops.Add(stop);
        await _db.SaveChangesAsync();
        return new Result { Ok = true, YaEstaba = false,
            Mensaje = dia == hoyAr ? "Agregado al mapa de hoy." : $"Agregado al mapa del {dia:dddd dd/MM}.",
            Nombre = nombre, Localidad = localidad, StopId = stop.Id };
    }
}
