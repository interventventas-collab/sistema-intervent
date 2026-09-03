using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Suma una VISITA al mapa de reparto como parada (2026-08-05). Espejo de VentaMapeoService.
/// Ubicación por prioridad: coords snapshot de la visita → coords del cliente → geocoding de la
/// dirección. Si no se resuelve, la agrega igual SIN ubicación (0,0) para localizarla en el mapa.
/// Idempotente por Origin='visita' + OriginRefId = visita.Id.
/// </summary>
public class VisitaMapeoService
{
    private readonly AppDbContext _db;
    private readonly GoogleMapsLinkResolverService _mapsResolver;

    public VisitaMapeoService(AppDbContext db, GoogleMapsLinkResolverService mapsResolver)
    {
        _db = db;
        _mapsResolver = mapsResolver;
    }

    public class Result
    {
        public bool Ok { get; set; }
        public bool YaEstaba { get; set; }
        public string? Mensaje { get; set; }
        public int? StopId { get; set; }
        public bool SinUbicacion { get; set; }
    }

    public async Task<Result> SumarVisitaAsync(Visita v, DateTime? fecha = null)
    {
        decimal? lat = v.MapeoLat, lng = v.MapeoLng;
        bool sinUbicacion = false;

        // Si no trae coords snapshot pero tiene cliente, probar las coords actuales de la ficha.
        if ((lat is null || lng is null) && v.ClienteId is not null)
        {
            var cli = await _db.CafeClientes.FindAsync(v.ClienteId.Value);
            if (cli?.MapeoLat is not null && cli.MapeoLng is not null) { lat = cli.MapeoLat; lng = cli.MapeoLng; }
        }

        // Geocodificar la dirección si todavía no hay coords.
        if ((lat is null || lng is null) && !string.IsNullOrWhiteSpace(v.Direccion))
        {
            var q = v.Direccion + (string.IsNullOrWhiteSpace(v.Localidad) ? "" : ", " + v.Localidad) + ", Argentina";
            var r = await _mapsResolver.TryGeocodeAddressAsync(q);
            if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; }
        }

        if (lat is null || lng is null) { lat = 0m; lng = 0m; sinUbicacion = true; }

        var dia = (fecha ?? DateTime.UtcNow.AddHours(-3)).Date;
        var refId = v.Id.ToString();
        var existente = await _db.MapeoStops.FirstOrDefaultAsync(s => s.Origin == "visita" && s.OriginRefId == refId && s.FechaReparto == dia);
        if (existente is not null)
        {
            // No pisar una ubicación buena con 0,0 si ahora tampoco la tenemos.
            if (!sinUbicacion) { existente.Latitude = lat.Value; existente.Longitude = lng.Value; }
            existente.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            var yaSinUbic = sinUbicacion && existente.Latitude == 0 && existente.Longitude == 0;
            return new Result
            {
                Ok = true,
                YaEstaba = true,
                StopId = existente.Id,
                SinUbicacion = yaSinUbic,
                Mensaje = yaSinUbic
                    ? "Ya está en el mapa SIN ubicación — buscá el domicilio en el mapa."
                    : "Ya estaba en el mapa (actualicé la ubicación)."
            };
        }

        var desc = v.Descripcion?.Trim() ?? "";
        if (desc.Length > 80) desc = desc.Substring(0, 80) + "…";
        var stop = new MapeoStop
        {
            Origin = "visita",
            OriginRefId = refId,
            Alias = v.ClienteNombre,
            Direccion = string.IsNullOrWhiteSpace(v.Direccion) ? v.ClienteNombre : v.Direccion!,
            Localidad = v.Localidad,
            Latitude = lat.Value,
            Longitude = lng.Value,
            ContactName = v.ClienteNombre,
            Telefono = v.Telefono,
            Notas = $"Visita N° {v.Numero:D4}" + (string.IsNullOrWhiteSpace(desc) ? "" : " — " + desc),
            InternalStatus = "pending",
            FechaReparto = dia,
            CreatedAt = DateTime.UtcNow
        };
        _db.MapeoStops.Add(stop);
        await _db.SaveChangesAsync();
        return new Result
        {
            Ok = true,
            YaEstaba = false,
            StopId = stop.Id,
            SinUbicacion = sinUbicacion,
            Mensaje = sinUbicacion
                ? "Agregada al mapa SIN ubicación — buscá el domicilio en el mapa."
                : "Agregada al mapa."
        };
    }
}
