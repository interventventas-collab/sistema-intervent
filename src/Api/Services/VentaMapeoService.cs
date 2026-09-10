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
        public bool SinUbicacion { get; set; }      // se agregó pero SIN ubicación (a resolver en el mapa)
    }

    private static string? FirstNonEmpty(params string?[] vals) => vals.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

    /// <summary>
    /// 2026-09-10: vuelve a fijar las paradas de ventas que quedaron SIN ubicación (0,0).
    ///
    /// Una parada entra sin ubicación cuando, en ese momento, no había de dónde sacarla. Si después el
    /// operador carga la ubicación en la ficha del cliente (o en el domicilio de entrega), la parada
    /// que ya estaba en el mapa no se enteraba nunca y quedaba muerta: se veía en el listado pero sin
    /// chinche en el mapa. Esto la acomoda sola al abrir el mapa.
    ///
    /// Usa SOLO lo que ya está guardado en la base (las coordenadas de la ficha) — no resuelve links ni
    /// le pregunta a Google, porque corre en cada apertura del mapa y tiene que ser barato. Los links de
    /// las fichas ya se resuelven a coordenadas cuando se guardan.
    /// </summary>
    public async Task<int> RefrescarSinUbicacionAsync(DateTime dia)
    {
        var pendientes = await _db.MapeoStops
            .Where(s => s.FechaReparto == dia && s.Origin == "venta_cafe" && s.OriginRefId != null
                     && s.Latitude == 0 && s.Longitude == 0)
            .ToListAsync();
        if (pendientes.Count == 0) return 0;

        var arregladas = 0;
        foreach (var stop in pendientes)
        {
            if (!int.TryParse(stop.OriginRefId, out var ventaId)) continue;
            var v = await _db.CafeVentas.Include(x => x.ClienteNav).FirstOrDefaultAsync(x => x.Id == ventaId);
            if (v is null) continue;
            var cli = v.ClienteNav;

            decimal? lat = null, lng = null;
            var alt = await BuscarDomicilioEntregaAsync(v, cli);
            if (alt?.MapeoLat is not null && alt.MapeoLng is not null) { lat = alt.MapeoLat; lng = alt.MapeoLng; }
            if (lat is null && cli?.MapeoLat is not null && cli.MapeoLng is not null) { lat = cli.MapeoLat; lng = cli.MapeoLng; }
            if (lat is null || lng is null || (lat == 0m && lng == 0m)) continue;

            stop.Latitude = lat.Value;
            stop.Longitude = lng.Value;
            stop.UpdatedAt = DateTime.UtcNow;
            arregladas++;
        }
        if (arregladas > 0) await _db.SaveChangesAsync();
        return arregladas;
    }

    /// <summary>
    /// 2026-09-10: cuál de los domicilios de entrega del cliente (Cafe_ClienteDirecciones) es el que se
    /// eligió en ESTA venta. Se reconoce igual que al guardar una ubicación desde el mapa: primero por el
    /// link de Maps que la venta se copió del domicilio, y si no, porque el domicilio de entrega escrito
    /// en la venta arranca con el texto de esa dirección. Devuelve null si la venta va al domicilio de
    /// siempre (o si el cliente no tiene domicilios alternativos).
    /// </summary>
    private async Task<CafeClienteDireccion?> BuscarDomicilioEntregaAsync(CafeVenta v, CafeCliente? cli)
    {
        if (cli is null) return null;
        var alts = await _db.CafeClienteDirecciones.Where(d => d.ClienteId == cli.Id && d.IsActive).ToListAsync();
        if (alts.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(v.MapeoLink))
        {
            var porLink = alts.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.MapeoLink) && d.MapeoLink == v.MapeoLink);
            if (porLink is not null) return porLink;
        }
        var snapshot = v.ClienteDomicilioEntregaSnapshot;
        if (!string.IsNullOrWhiteSpace(snapshot))
            return alts.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Direccion)
                && snapshot!.StartsWith(d.Direccion, StringComparison.OrdinalIgnoreCase));
        return null;
    }

    /// <summary>Suma la venta al mapa. La venta debe venir con ClienteNav incluido.</summary>
    public async Task<Result> SumarVentaAsync(CafeVenta v, string? direccion = null, string? link = null, DateTime? fecha = null)
    {
        var cli = v.ClienteNav;
        var nombre = FirstNonEmpty(v.ClienteNombreSnapshot, cli?.Nombre) ?? "Cliente";
        var dir = FirstNonEmpty(v.ClienteDomicilioEntregaSnapshot, v.ClienteDireccionSnapshot, cli?.DomicilioEntrega, cli?.Direccion);
        var localidad = FirstNonEmpty(v.ClienteLocalidadSnapshot, v.ClienteCiudadSnapshot, cli?.Localidad, cli?.Ciudad);
        var telefono = FirstNonEmpty(v.ClienteTelefonoSnapshot, cli?.Telefono);

        decimal? lat = null, lng = null;
        bool guardarEnCliente = false;
        bool sinUbicacion = false;

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
            // Resolver automático por prioridad. El MapeoLink PROPIO de la venta refleja el domicilio
            // elegido en la venta (el principal o un domicilio alternativo), así que GANA sobre las
            // coords cacheadas del cliente. Si no, una venta a un domicilio alternativo caería en el
            // principal. Solo cae a las coords del cliente cuando la venta no trae link propio.
            if (!string.IsNullOrWhiteSpace(v.MapeoLink))
            { var r = await _mapsResolver.TryResolverCoordenadasAsync(v.MapeoLink); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; } }
            // 2026-09-10: el DOMICILIO DE ENTREGA elegido en la venta (Cafe_ClienteDirecciones) tiene su
            // propia ubicación, y hasta hoy el mapa nunca la miraba: solo sabía del domicilio "de siempre"
            // de la ficha. Un cliente que recibe en otra puerta (un hotel, un depósito) podía tener la
            // ubicación perfectamente cargada y la parada igual salía sin chinche. Va DESPUÉS del link
            // propio de la venta y ANTES del domicilio de siempre, porque es el domicilio de ESTA venta.
            var alt = await BuscarDomicilioEntregaAsync(v, cli);
            if (lat is null && alt?.MapeoLat is not null && alt.MapeoLng is not null) { lat = alt.MapeoLat; lng = alt.MapeoLng; }
            if (lat is null && !string.IsNullOrWhiteSpace(alt?.MapeoLink))
            { var r = await _mapsResolver.TryResolverCoordenadasAsync(alt!.MapeoLink); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; } }
            if (lat is null && cli?.MapeoLat is not null && cli.MapeoLng is not null) { lat = cli.MapeoLat; lng = cli.MapeoLng; }
            if (lat is null && !string.IsNullOrWhiteSpace(cli?.MapeoLink))
            { var r = await _mapsResolver.TryResolverCoordenadasAsync(cli!.MapeoLink); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; guardarEnCliente = true; } }
            if (lat is null && !string.IsNullOrWhiteSpace(dir))
            { var q = dir + (string.IsNullOrWhiteSpace(localidad) ? "" : ", " + localidad) + ", Argentina";
              var r = await _mapsResolver.TryGeocodeAddressAsync(q); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; guardarEnCliente = true; } }

            if (lat is null)
            {
                // Sin domicilio: lo agregamos IGUAL pero SIN ubicación (0,0). La cargás después desde el
                // mapa con el buscador (y ahí se guarda en la ficha del cliente). Mejor que adivinar mal.
                lat = 0m; lng = 0m; sinUbicacion = true;
            }
        }

        // Guardar la ubicación en la ficha del cliente para la próxima.
        if (guardarEnCliente && cli is not null && lat is not null && lng is not null)
        {
            cli.MapeoLat = lat;
            cli.MapeoLng = lng;
            if (!string.IsNullOrWhiteSpace(link)) cli.MapeoLink = link.Trim();
        }

        // Crear/actualizar la parada (idempotente DENTRO DEL DÍA). 2026-09-03: el mapa tiene días;
        // la misma venta puede haber estado en el mapa de ayer (historial) y entrar de nuevo hoy.
        var dia = (fecha ?? DateTime.UtcNow.AddHours(-3)).Date;
        var refId = v.Id.ToString();
        var existente = await _db.MapeoStops.FirstOrDefaultAsync(s => s.Origin == "venta_cafe" && s.OriginRefId == refId && s.FechaReparto == dia);
        if (existente is not null)
        {
            // Si ya estaba y ahora tampoco tenemos ubicación, NO le pisamos una ubicación buena con 0,0.
            if (!sinUbicacion) { existente.Latitude = lat!.Value; existente.Longitude = lng!.Value; }
            existente.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            var yaSinUbic = sinUbicacion && existente.Latitude == 0 && existente.Longitude == 0;
            return new Result { Ok = true, YaEstaba = true, Mensaje = yaSinUbic ? "Ya está en el mapa SIN ubicación — buscá el domicilio en el mapa." : "Ya estaba en el mapa (actualicé la ubicación).", Nombre = nombre, Localidad = localidad, StopId = existente.Id, SinUbicacion = yaSinUbic };
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
            FechaReparto = dia,
            CreatedAt = DateTime.UtcNow
        };
        _db.MapeoStops.Add(stop);
        await _db.SaveChangesAsync();
        return new Result { Ok = true, YaEstaba = false, Mensaje = sinUbicacion ? "Agregado al mapa SIN ubicación — buscá el domicilio en el mapa." : "Agregado al mapa.", Nombre = nombre, Localidad = localidad, StopId = stop.Id, SinUbicacion = sinUbicacion };
    }
}
