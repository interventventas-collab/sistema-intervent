using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Lógica compartida para sumar una venta del café al mapa de reparto como parada.
/// La usan: el botón "Sumar al mapa" del listado de ventas (CafeVentasController) y el
/// escáner cuando lee el QR de una factura/cotización (MapeoStopsController).
///
/// Resuelve la ubicación por prioridad: coords del cliente → MapeoLink de la venta →
/// MapeoLink del cliente → geocoding de la dirección. Si el usuario manda una dirección/link
/// (cargar en el momento), los resuelve y los GUARDA en la ficha del cliente para la próxima.
/// Idempotente por Origin='venta_cafe' + OriginRefId = venta.Id.
/// </summary>
public class VentaMapeoService
{
    private readonly AppDbContext _db;
    private readonly GoogleMapsLinkResolverService _mapsResolver;

    public VentaMapeoService(AppDbContext db, GoogleMapsLinkResolverService mapsResolver)
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

    /// <summary>Suma la venta al mapa. La venta debe venir con ClienteNav incluido.</summary>
    public async Task<Result> SumarVentaAsync(CafeVenta v, string? direccion = null, string? link = null)
    {
        var cli = v.ClienteNav;
        var nombre = FirstNonEmpty(v.ClienteNombreSnapshot, cli?.Nombre) ?? "Cliente";
        var dir = FirstNonEmpty(v.ClienteDomicilioEntregaSnapshot, v.ClienteDireccionSnapshot, cli?.DomicilioEntrega, cli?.Direccion);
        var localidad = FirstNonEmpty(v.ClienteLocalidadSnapshot, v.ClienteCiudadSnapshot, cli?.Localidad, cli?.Ciudad);
        var telefono = FirstNonEmpty(v.ClienteTelefonoSnapshot, cli?.Telefono);

        decimal? lat = null, lng = null;
        bool guardarEnCliente = false;

        if (!string.IsNullOrWhiteSpace(link) || !string.IsNullOrWhiteSpace(direccion))
        {
            // Cargar en el momento: el usuario tipeó una dirección o pegó un link.
            if (!string.IsNullOrWhiteSpace(link))
            { var r = await _mapsResolver.TryResolverCoordenadasAsync(link); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; } }
            if (lat is null && !string.IsNullOrWhiteSpace(direccion))
            { var q = direccion + (string.IsNullOrWhiteSpace(localidad) ? "" : ", " + localidad) + ", Argentina";
              var r = await _mapsResolver.TryGeocodeAddressAsync(q); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; } }
            if (lat is null)
                return new Result { Ok = false, Motivo = "no_resuelto", Mensaje = "No pude encontrar esa dirección. Probá con calle + número + localidad, o pegá un link de Google Maps." };
            guardarEnCliente = true;
            if (!string.IsNullOrWhiteSpace(direccion)) dir = direccion.Trim();
        }
        else
        {
            // Resolver automático por prioridad.
            if (cli?.MapeoLat is not null && cli.MapeoLng is not null) { lat = cli.MapeoLat; lng = cli.MapeoLng; }
            if (lat is null && !string.IsNullOrWhiteSpace(v.MapeoLink))
            { var r = await _mapsResolver.TryResolverCoordenadasAsync(v.MapeoLink); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; } }
            if (lat is null && !string.IsNullOrWhiteSpace(cli?.MapeoLink))
            { var r = await _mapsResolver.TryResolverCoordenadasAsync(cli!.MapeoLink); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; guardarEnCliente = true; } }
            if (lat is null && !string.IsNullOrWhiteSpace(dir))
            { var q = dir + (string.IsNullOrWhiteSpace(localidad) ? "" : ", " + localidad) + ", Argentina";
              var r = await _mapsResolver.TryGeocodeAddressAsync(q); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; guardarEnCliente = true; } }

            if (lat is null)
                return new Result { Ok = false, Motivo = "sin_domicilio", Mensaje = "Este cliente no tiene domicilio cargado.",
                    ClienteId = v.ClienteId, Nombre = nombre, DireccionSugerida = dir, Localidad = localidad };
        }

        // Guardar la ubicación en la ficha del cliente para la próxima.
        if (guardarEnCliente && cli is not null && lat is not null && lng is not null)
        {
            cli.MapeoLat = lat;
            cli.MapeoLng = lng;
            if (!string.IsNullOrWhiteSpace(link)) cli.MapeoLink = link.Trim();
        }

        // Crear/actualizar la parada (idempotente).
        var refId = v.Id.ToString();
        var existente = await _db.MapeoStops.FirstOrDefaultAsync(s => s.Origin == "venta_cafe" && s.OriginRefId == refId);
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
            Origin = "venta_cafe",
            OriginRefId = refId,
            Alias = nombre,
            Direccion = string.IsNullOrWhiteSpace(dir) ? nombre : dir!,
            Localidad = localidad,
            Latitude = lat!.Value,
            Longitude = lng!.Value,
            ContactName = nombre,
            Telefono = telefono,
            Notas = $"Venta {v.Numero}",
            InternalStatus = "pending",
            CreatedAt = DateTime.UtcNow
        };
        _db.MapeoStops.Add(stop);
        await _db.SaveChangesAsync();
        return new Result { Ok = true, YaEstaba = false, Mensaje = "Agregado al mapa.", Nombre = nombre, Localidad = localidad, StopId = stop.Id };
    }
}
