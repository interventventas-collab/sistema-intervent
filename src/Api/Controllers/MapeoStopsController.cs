using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/mapeo/stops")]
[Authorize]
public class MapeoStopsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly GoogleRoutesService _routes;
    private readonly VentaMapeoService _ventaMapeo;
    private readonly AlqMapeoService _alqMapeo;
    private readonly VisitaMapeoService _visitaMapeo;
    private readonly GoogleMapsLinkResolverService _mapsResolver;
    private readonly MeliShipmentService _shipmentSvc;
    private readonly MeliOrderService _orderSvc;
    private readonly MapeoRutaPdfService _rutaPdf;
    private readonly MapeoEntregasService _entregas;
    private readonly MapeoAsignacionService _asignacion;
    private readonly ILogger<MapeoStopsController> _logger;
    public MapeoStopsController(AppDbContext db, GoogleRoutesService routes, VentaMapeoService ventaMapeo, AlqMapeoService alqMapeo, VisitaMapeoService visitaMapeo, GoogleMapsLinkResolverService mapsResolver, MeliShipmentService shipmentSvc, MeliOrderService orderSvc, MapeoRutaPdfService rutaPdf, MapeoEntregasService entregas, MapeoAsignacionService asignacion, ILogger<MapeoStopsController> logger)
    { _db = db; _routes = routes; _ventaMapeo = ventaMapeo; _alqMapeo = alqMapeo; _visitaMapeo = visitaMapeo; _mapsResolver = mapsResolver; _shipmentSvc = shipmentSvc; _orderSvc = orderSvc; _rutaPdf = rutaPdf; _entregas = entregas; _asignacion = asignacion; _logger = logger; }

    public record StopDto(int Id, string Origin, string? OriginRefId, string? Alias, string Direccion,
        decimal Latitude, decimal Longitude, string? ContactName, string? Telefono, string? Notas,
        string InternalStatus, int? AssignedDriverId, string? AssignedDriverName, string? AssignedDriverColor,
        int? AssignedVehicleSlot, int? OrderInRoute, DateTime CreatedAt,
        string? Localidad = null,
        // 2026-09-03: para qué día es esta parada.
        DateTime? FechaReparto = null,
        // Datos del envío de MeLi enlazado (para paradas Flex/ME1 escaneadas): usuario, nº venta, entregado.
        long? MeliOrderId = null, string? BuyerNickname = null, string? MeliStatus = null,
        // 2026-09-02: el detalle de MeLi ("destinatario ausente" y compañía). El estado general dice
        // "en camino" aunque el repartidor ya haya pasado y no haya podido: el motivo está acá.
        string? MotivoMeli = null,
        // 2026-09-02: domicilio COMERCIAL (lo que sale en la etiqueta del Flex). Se ve en el mapa
        // sin abrir nada, porque cambia con qué se encuentra el repartidor y a qué hora conviene ir.
        bool EsComercial = false,
        DateTime? DateDelivered = null, string? ReceiverName = null);

    // ══════════════════════════════════════════════════════════════════════════════
    // 2026-09-03: EL MAPA AHORA TIENE DÍAS.
    //
    // Antes había un solo mapa —el de ahora— y al abrirlo se borraba todo lo de días anteriores.
    // Ahora cada parada sabe para qué día es (MapeoStop.FechaReparto), así se puede mirar lo que
    // pasó los días pasados y armar los que vienen sin ensuciar el de hoy.
    //
    // ⚠ REGLA DE ORO: al celular del repartidor le llega SOLO el día de hoy. Ver
    // CafeRepartidorPublicController y DashboardController, que filtran por HoyAr().
    // ══════════════════════════════════════════════════════════════════════════════

    /// <summary>Hoy en Argentina, sin hora. Es "el día" del mapa.</summary>
    private static DateTime HoyAr() => DateTime.UtcNow.AddHours(-3).Date;

    /// <summary>La fecha pedida, o hoy si no vino ninguna. Nunca devuelve hora.</summary>
    private static DateTime FechaDelMapa(DateTime? fecha) => (fecha ?? HoyAr()).Date;

    private static StopDto Map(MapeoStop s) => new(
        s.Id, s.Origin, s.OriginRefId, s.Alias, s.Direccion, s.Latitude, s.Longitude,
        s.ContactName, s.Telefono, s.Notas, s.InternalStatus,
        s.AssignedDriverId, s.AssignedDriver?.Nombre, s.AssignedDriver?.Color,
        s.AssignedVehicleSlot, s.OrderInRoute, s.CreatedAt, s.Localidad, s.FechaReparto);

    /// <summary>Las paradas de UN día. Sin `fecha` devuelve las de hoy, que es lo que hacía siempre.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int? driverId = null, [FromQuery] string? internalStatus = null,
        [FromQuery] DateTime? fecha = null)
    {
        var dia = FechaDelMapa(fecha);
        var q = _db.MapeoStops.Include(s => s.AssignedDriver).Where(s => s.FechaReparto == dia).AsQueryable();
        if (driverId.HasValue) q = q.Where(s => s.AssignedDriverId == driverId.Value);
        if (!string.IsNullOrWhiteSpace(internalStatus)) q = q.Where(s => s.InternalStatus == internalStatus);
        var list = await q.OrderBy(s => s.AssignedDriverId).ThenBy(s => s.OrderInRoute ?? int.MaxValue).ThenBy(s => s.Id).ToListAsync();
        // Enriquecemos las paradas Flex/ME1 con datos del envío de MeLi (usuario, nº venta, entregado).
        var refs = list.Where(s => (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null)
                       .Select(s => long.TryParse(s.OriginRefId, out var v) ? v : 0L)
                       .Where(v => v != 0L).Distinct().ToList();
        var ships = refs.Count == 0
            ? new Dictionary<long, MeliShipment>()
            : await _db.MeliShipments.Where(m => refs.Contains(m.MeliShipmentId)).ToDictionaryAsync(m => m.MeliShipmentId);
        // 2026-08-15: la hora real de entrega de CUALQUIER tipo de parada (envío de MeLi, venta del
        // café, alquiler o visita) la resuelve un solo servicio, que es el mismo que usa el dashboard.
        // Antes esto estaba suelto acá y solo cubría MeLi y ventas: alquileres y visitas nunca se
        // tildaban en el mapa, y parecía que el repartidor no había pasado.
        var entregas = await _entregas.EntregasAsync(list);
        return Ok(list.Select(s =>
        {
            var dto = Map(s);
            if ((s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null
                && long.TryParse(s.OriginRefId, out var sid) && ships.TryGetValue(sid, out var m))
            {
                dto = dto with
                {
                    MeliOrderId = m.MeliOrderId,
                    BuyerNickname = m.BuyerNickname,
                    MeliStatus = m.Status,
                    MotivoMeli = MapeoEntregasService.MotivoMeli(m.Status, m.Substatus),
                    EsComercial = string.Equals(m.DeliveryPreference, "business", StringComparison.OrdinalIgnoreCase),
                    ReceiverName = m.ReceiverName
                };
            }
            // La hora de entrega sale del servicio (vale para todos los tipos de parada).
            if (entregas.TryGetValue(s.Id, out var entregadoAt) && entregadoAt.HasValue)
                dto = dto with { DateDelivered = entregadoAt };
            return dto;
        }));
    }

    /// <summary>Para el globito del mapa de una parada Flex/ME1: dado el nº de ENVÍO de MeLi
    /// (MeliShipmentId, que es lo que la parada guarda en OriginRefId), devuelve QUÉ compró el
    /// cliente (productos de la venta, desde la base local — instantáneo) y los MENSAJES de la
    /// venta que escribió el comprador (EN VIVO desde MeLi, best-effort igual que en Preparación).
    /// Se pide recién al abrir el globito para no frenar el mapa. Si no encuentra nada, devuelve
    /// listas vacías y el globito queda como estaba.</summary>
    [HttpGet("venta-info")]
    public async Task<IActionResult> VentaInfo([FromQuery] long shipmentId)
    {
        if (shipmentId <= 0) return Ok(new { ok = false });

        // Productos de la venta = líneas de MeliOrder del mismo envío (base LOCAL, sin llamar a MeLi).
        var productos = await _db.MeliOrders.Where(o => o.ShippingId == shipmentId).ToListAsync();
        if (productos.Count == 0)
        {
            // Fallback: el número matchea un envío conocido (MeliShipments) → su orden → hermanos del mismo envío.
            var sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == shipmentId);
            if (sh?.MeliOrderId is not null)
            {
                var ord = await _db.MeliOrders.FirstOrDefaultAsync(o => o.MeliOrderId == sh.MeliOrderId);
                if (ord?.ShippingId is not null)
                    productos = await _db.MeliOrders.Where(o => o.ShippingId == ord.ShippingId).ToListAsync();
                else if (ord is not null)
                    productos = new List<MeliOrder> { ord };
            }
        }
        if (productos.Count == 0)
            return Ok(new { ok = true, productos = Array.Empty<object>(), mensajes = Array.Empty<object>() });

        var primero = productos[0];
        var buyerId = primero.BuyerId;

        // SKU + fotito de la publicación (join por ItemId contra MeliItems).
        var itemIds = productos.Select(p => p.ItemId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        var itemInfo = (await _db.MeliItems
                .Where(m => itemIds.Contains(m.MeliItemId))
                .Select(m => new { m.MeliItemId, m.Sku, m.Thumbnail })
                .ToListAsync())
            .GroupBy(m => m.MeliItemId)
            .ToDictionary(g => g.Key, g => g.First());

        var productosOut = productos
            .OrderBy(p => p.ItemTitle)
            .Select(p =>
            {
                itemInfo.TryGetValue(p.ItemId ?? "", out var mi);
                var thumb = mi?.Thumbnail;
                if (!string.IsNullOrEmpty(thumb) && thumb.StartsWith("http://"))
                    thumb = "https://" + thumb.Substring("http://".Length);
                return new { titulo = p.ItemTitle, cantidad = p.Quantity, sku = mi?.Sku, thumbnail = thumb };
            }).ToList();

        // MENSAJES de la venta (post-venta), EN VIVO desde MeLi. Best-effort: si MeLi no deja leer, lista vacía.
        var mensajes = new List<object>();
        try
        {
            var account = await _db.MeliAccounts.FirstOrDefaultAsync(a => a.Id == primero.MeliAccountId);
            if (account is not null)
            {
                var packOrOrder = primero.PackId ?? primero.MeliOrderId;
                var msgs = await _orderSvc.GetPackMessagesAsync(packOrOrder, account);
                mensajes = msgs.Select(mm => (object)new
                {
                    de = mm.FromUserId == buyerId ? "comprador" : "vendedor",
                    texto = mm.Text,
                    fecha = mm.Date
                }).ToList();
            }
        }
        catch { /* best-effort: si MeLi no responde, el globito muestra solo lo que compró */ }

        return Ok(new { ok = true, productos = productosOut, mensajes });
    }

    public record CreateStopRequest(string Origin, string? OriginRefId, string? Alias, string Direccion,
        decimal Latitude, decimal Longitude, string? ContactName, string? Telefono, string? Notas,
        string? Localidad = null,
        // 2026-09-03: para qué día es. Si no viene, hoy — que es lo que hacía siempre.
        DateTime? Fecha = null);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateStopRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Direccion)) return BadRequest(new { error = "Dirección obligatoria" });
        // Si ya existe una parada con mismo origin+ref (ej: 2 veces el mismo favorito), permitimos duplicar — el usuario sabrá.
        var s = new MapeoStop
        {
            Origin = string.IsNullOrWhiteSpace(req.Origin) ? "manual" : req.Origin.ToLower(),
            OriginRefId = req.OriginRefId,
            Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim(),
            Direccion = req.Direccion.Trim(),
            Localidad = string.IsNullOrWhiteSpace(req.Localidad) ? null : req.Localidad.Trim(),
            Latitude = req.Latitude,
            Longitude = req.Longitude,
            ContactName = string.IsNullOrWhiteSpace(req.ContactName) ? null : req.ContactName.Trim(),
            Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim(),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            InternalStatus = "pending",
            FechaReparto = FechaDelMapa(req.Fecha),
            CreatedAt = DateTime.UtcNow
        };
        _db.MapeoStops.Add(s);
        await _db.SaveChangesAsync();
        await _db.Entry(s).Reference(x => x.AssignedDriver).LoadAsync();
        return Ok(Map(s));
    }

    public record UpdateStopRequest(string? Alias, string? ContactName, string? Telefono, string? Notas,
        string? InternalStatus, int? AssignedDriverId, int? OrderInRoute);

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateStopRequest req)
    {
        var s = await _db.MapeoStops.Include(x => x.AssignedDriver).FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound(new { error = "Parada no encontrada" });
        if (req.Alias is not null) s.Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim();
        if (req.ContactName is not null) s.ContactName = string.IsNullOrWhiteSpace(req.ContactName) ? null : req.ContactName.Trim();
        if (req.Telefono is not null) s.Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim();
        if (req.Notas is not null) s.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        if (req.InternalStatus is not null) s.InternalStatus = req.InternalStatus.Trim().ToLower();
        if (req.AssignedDriverId.HasValue)
        {
            s.AssignedDriverId = req.AssignedDriverId.Value > 0 ? req.AssignedDriverId.Value : null;
            await _db.Entry(s).Reference(x => x.AssignedDriver).LoadAsync();
        }
        if (req.OrderInRoute.HasValue) s.OrderInRoute = req.OrderInRoute.Value > 0 ? req.OrderInRoute.Value : null;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        // Si cambió el chofer, llevamos la parada al celu de ese repartidor (o se la sacamos, si quedó
        // sin chofer). Vale para ventas, alquileres y ME1 — ver MapeoAsignacionService.
        if (req.AssignedDriverId.HasValue)
            await _asignacion.SincronizarStopsAsync(new[] { s });
        return Ok(Map(s));
    }

    /// <summary>GuardarEnCliente=true (definitivo): además de la parada, guarda las coords en el domicilio
    /// del cliente que se usó en la venta (el alternativo si fue a uno, o el de siempre). false (solo esta
    /// vez): corrige solo esta entrega, sin tocar la ficha del cliente.</summary>
    public record SetUbicacionRequest(double Lat, double Lng, string? Direccion, bool GuardarEnCliente = true);

    /// <summary>Fija la ubicación de una parada (elegida en el buscador o arrastrando el pin). Corrige
    /// domicilios sin salir del mapa — mejor que el geocoder que a veces adivina mal.</summary>
    [HttpPost("{id:int}/ubicacion")]
    public async Task<IActionResult> SetUbicacion(int id, [FromBody] SetUbicacionRequest req)
    {
        var s = await _db.MapeoStops.FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound(new { error = "Parada no encontrada" });
        var dirTxt = string.IsNullOrWhiteSpace(req.Direccion) ? null : req.Direccion.Trim();
        s.Latitude = (decimal)req.Lat;
        s.Longitude = (decimal)req.Lng;
        if (dirTxt != null) s.Direccion = dirTxt;
        s.UpdatedAt = DateTime.UtcNow;

        if (s.Origin == "venta_cafe" && int.TryParse(s.OriginRefId, out var ventaId))
        {
            var venta = await _db.CafeVentas.Include(v => v.ClienteNav).FirstOrDefaultAsync(v => v.Id == ventaId);
            if (venta is not null)
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var link = $"https://www.google.com/maps/search/?api=1&query={req.Lat.ToString(ci)},{req.Lng.ToString(ci)}";
                // Matcheamos el domicilio usado con los valores PREVIOS (antes de pisar link/snapshot).
                var linkPrevio = venta.MapeoLink;
                var snapshotPrevio = venta.ClienteDomicilioEntregaSnapshot;

                // Definitivo: guardar coords Y texto en el domicilio del cliente que realmente se usó.
                var cli = venta.ClienteNav;
                if (req.GuardarEnCliente && cli is not null)
                {
                    var alts = await _db.CafeClienteDirecciones.Where(d => d.ClienteId == cli.Id && d.IsActive).ToListAsync();
                    CafeClienteDireccion? alt = null;
                    if (!string.IsNullOrWhiteSpace(linkPrevio))
                        alt = alts.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.MapeoLink) && d.MapeoLink == linkPrevio);
                    if (alt is null && !string.IsNullOrWhiteSpace(snapshotPrevio))
                        alt = alts.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.Direccion)
                            && snapshotPrevio!.StartsWith(d.Direccion, StringComparison.OrdinalIgnoreCase));

                    if (alt is not null)
                    {
                        alt.MapeoLat = (decimal)req.Lat; alt.MapeoLng = (decimal)req.Lng;
                        alt.MapeoLink = link; alt.UpdatedAt = DateTime.UtcNow;
                        if (dirTxt != null) alt.Direccion = dirTxt;      // corrige también el texto del domicilio alternativo
                    }
                    else
                    {
                        cli.MapeoLat = (decimal)req.Lat; cli.MapeoLng = (decimal)req.Lng; cli.MapeoLink = link;
                        if (dirTxt != null) cli.DomicilioEntrega = dirTxt; // corrige el domicilio de siempre
                    }
                }

                // El comprobante de ESTA venta refleja el texto corregido y el link corregido SIEMPRE
                // (aunque sea "solo esta vez").
                if (dirTxt != null) venta.ClienteDomicilioEntregaSnapshot = dirTxt;
                venta.MapeoLink = link;
            }
        }
        await _db.SaveChangesAsync();
        return Ok(Map(s));
    }

    public record AsignarClienteRequest(int ClienteId, bool GuardarUbicacion = false);

    /// <summary>2026-08-15: vincula una parada suelta (la que sale de buscar una dirección en el mapa)
    /// con un CLIENTE del sistema. La parada pasa a llamarse como el cliente y hereda su teléfono si no
    /// tenía. Con GuardarUbicacion=true además le guarda estas coordenadas al cliente en su ficha, así
    /// la próxima vez aparece solo en "Clientes mapeados" y no hay que volver a buscar la dirección.
    /// No pisa la ubicación de un cliente que ya la tenía salvo que se pida expresamente.</summary>
    [HttpPost("{id:int}/asignar-cliente")]
    public async Task<IActionResult> AsignarCliente(int id, [FromBody] AsignarClienteRequest req)
    {
        var s = await _db.MapeoStops.Include(x => x.AssignedDriver).FirstOrDefaultAsync(x => x.Id == id);
        if (s is null) return NotFound(new { error = "Parada no encontrada" });
        var cli = await _db.CafeClientes.FirstOrDefaultAsync(c => c.Id == req.ClienteId);
        if (cli is null) return NotFound(new { error = "Cliente no encontrado" });

        s.Origin = "cliente-cafe";
        s.OriginRefId = cli.Id.ToString();
        s.Alias = cli.CodigoInterno.HasValue ? $"#{cli.CodigoInterno} · {cli.Nombre}" : cli.Nombre;
        if (string.IsNullOrWhiteSpace(s.Telefono) && !string.IsNullOrWhiteSpace(cli.Telefono))
            s.Telefono = cli.Telefono;
        s.UpdatedAt = DateTime.UtcNow;

        var guardado = false;
        if (req.GuardarUbicacion)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            cli.MapeoLat = s.Latitude;
            cli.MapeoLng = s.Longitude;
            cli.MapeoLink = $"https://www.google.com/maps/search/?api=1&query={((double)s.Latitude).ToString(ci)},{((double)s.Longitude).ToString(ci)}";
            // El domicilio de entrega solo se completa si estaba vacío: no le pisamos al operador
            // un domicilio que ya venía cargado en la ficha.
            if (string.IsNullOrWhiteSpace(cli.DomicilioEntrega)) cli.DomicilioEntrega = s.Direccion;
            cli.UpdatedAt = DateTime.UtcNow;
            guardado = true;
        }
        await _db.SaveChangesAsync();
        return Ok(new { stop = Map(s), clienteNombre = cli.Nombre, ubicacionGuardada = guardado });
    }

    public record ResolverLinkRequest(string? Link);

    /// <summary>2026-08-13: resuelve un link de Google Maps (incluido el corto maps.app.goo.gl) a
    /// coordenadas, para el modo "Corregir ubicación" del mapa: el operador pega el link y el pin salta ahí.
    /// No guarda nada; solo devuelve lat/lng para que después elija cómo guardarlo.</summary>
    [HttpPost("resolver-link")]
    public async Task<IActionResult> ResolverLink([FromBody] ResolverLinkRequest req)
    {
        if (string.IsNullOrWhiteSpace(req?.Link))
            return Ok(new { ok = false, mensaje = "Pegá un link de Google Maps." });
        var r = await _mapsResolver.TryResolverCoordenadasAsync(req.Link);
        if (r is null)
            return Ok(new { ok = false, mensaje = "No pude sacar la ubicación de ese link. Copiá el link desde 'Compartir' en Google Maps." });
        return Ok(new { ok = true, lat = (double)r.Value.lat, lng = (double)r.Value.lng });
    }

    public record ReorderRequest(int[] StopIds);

    /// <summary>
    /// Reordena la ruta: recibe los IDs de las paradas en el orden deseado y les escribe
    /// OrderInRoute = 1, 2, 3, ... en ese orden. Se usa para arrastrar/subir/bajar desde el listado.
    /// </summary>
    [HttpPost("reorder")]
    public async Task<IActionResult> Reorder([FromBody] ReorderRequest req)
    {
        if (req?.StopIds is null || req.StopIds.Length == 0) return Ok(new { updated = 0 });
        var ids = req.StopIds.ToList();
        var stops = await _db.MapeoStops.Where(s => ids.Contains(s.Id)).ToListAsync();
        var now = DateTime.UtcNow;
        int order = 1;
        foreach (var id in ids) // respetamos el orden recibido
        {
            var s = stops.FirstOrDefault(x => x.Id == id);
            if (s is null) continue;
            s.OrderInRoute = order++;
            s.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();

        // Normalizamos cada ZONA tocada para que quede 1..N contiguo SIN dejar paradas afuera: primero las
        // que ya tenían orden (respetando el orden recién guardado), y al final las que estaban sin numerar.
        // Antes, si al armar la ruta quedaba alguna parada sin tocar, quedaba sin número Y afuera de la línea
        // (la ruta la salteaba). Ahora entra numerada al final.
        var zonasTocadas = stops
            .Select(s => s.AssignedDriverId.HasValue ? $"d{s.AssignedDriverId.Value}"
                        : (s.AssignedVehicleSlot.HasValue ? $"v{s.AssignedVehicleSlot.Value}" : null))
            .Where(k => k != null).Select(k => k!).Distinct().ToList();
        // El día sale de las propias paradas que se reordenaron (todas son del mismo día del mapa).
        var diaReorder = stops.Count > 0 ? stops[0].FechaReparto.Date : HoyAr();
        if (zonasTocadas.Count > 0) await NormalizarZonasAsync(zonasTocadas, now, diaReorder);

        return Ok(new { updated = order - 1 });
    }

    /// <summary>
    /// Renumera las zonas dadas (clave "d{driverId}" o "v{slot}") a 1..N contiguo: primero las paradas que
    /// ya tienen orden (por ese orden), después las que estaban sin numerar (por Id). Así ninguna parada de
    /// una zona en uso queda sin número ni afuera de la ruta. Idempotente.
    /// </summary>
    private async Task NormalizarZonasAsync(List<string> zonaKeys, DateTime now, DateTime dia)
    {
        foreach (var key in zonaKeys)
        {
            List<MapeoStop> zona;
            if (key.StartsWith("d") && int.TryParse(key[1..], out var did))
                zona = await _db.MapeoStops.Where(s => s.AssignedDriverId == did && s.FechaReparto == dia).ToListAsync();
            else if (key.StartsWith("v") && int.TryParse(key[1..], out var slot))
                zona = await _db.MapeoStops.Where(s => s.AssignedVehicleSlot == slot && s.AssignedDriverId == null && s.FechaReparto == dia).ToListAsync();
            else continue;

            var ordenadas = zona.OrderBy(s => s.OrderInRoute ?? int.MaxValue).ThenBy(s => s.Id).ToList();
            int n = 1;
            foreach (var s in ordenadas)
            {
                if (s.OrderInRoute != n) { s.OrderInRoute = n; s.UpdatedAt = now; }
                n++;
            }
        }
        await _db.SaveChangesAsync();
    }

    public record PonerEnPuestoRequest(int Puesto);

    /// <summary>
    /// "👉 Poner acá" del globito: mete la parada en el puesto pedido y renumera TODA su zona
    /// (repartidor si tiene; si no, el vehículo) del 1 al N en UN SOLO guardado atómico.
    /// Al renumerar todo de una, además auto-corrige cualquier repetido o salto que hubiera quedado
    /// de antes. Antes esto lo hacía el navegador con muchos guardados sueltos y, si se cortaba a
    /// mitad, quedaban números repetidos (ej: tres paradas en el puesto 13).
    /// </summary>
    [HttpPost("{id:int}/poner-en-puesto")]
    public async Task<IActionResult> PonerEnPuesto(int id, [FromBody] PonerEnPuestoRequest req)
    {
        var s = await _db.MapeoStops.FindAsync(id);
        if (s is null) return NotFound(new { error = "Parada no encontrada" });

        // Zona a renumerar: preferimos el repartidor; si no tiene, el vehículo (sólo las sin repartidor).
        List<MapeoStop> zona;
        if (s.AssignedDriverId.HasValue)
            zona = await _db.MapeoStops.Where(x => x.AssignedDriverId == s.AssignedDriverId && x.FechaReparto == s.FechaReparto).ToListAsync();
        else if (s.AssignedVehicleSlot.HasValue)
            zona = await _db.MapeoStops.Where(x => x.AssignedVehicleSlot == s.AssignedVehicleSlot && x.AssignedDriverId == null && x.FechaReparto == s.FechaReparto).ToListAsync();
        else
            return BadRequest(new { error = "Primero asigná este envío a un repartidor (o a un vehículo) para poder darle un puesto." });

        // Las demás numeradas, en su orden actual (desempatando por Id para que sea estable).
        var numeradas = zona.Where(x => x.Id != s.Id && x.OrderInRoute.HasValue)
                            .OrderBy(x => x.OrderInRoute!.Value).ThenBy(x => x.Id)
                            .ToList();

        // Puesto destino acotado a [1 .. cantidad+1] (la parada que movemos siempre queda numerada).
        int destino = req?.Puesto ?? 1;
        if (destino < 1) destino = 1;
        if (destino > numeradas.Count + 1) destino = numeradas.Count + 1;

        // Insertamos la parada en la posición pedida y renumeramos limpio 1..N.
        numeradas.Insert(destino - 1, s);
        var now = DateTime.UtcNow;
        int order = 1;
        foreach (var st in numeradas)
        {
            st.OrderInRoute = order++;
            st.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();
        return Ok(new { puesto = s.OrderInRoute, total = numeradas.Count });
    }

    public record FijaMedio(int StopId, int Puesto);
    public record ArmarRutaGuiadaRequest(int PrimeraId, int UltimaId, List<FijaMedio>? FijasMedio);

    /// <summary>
    /// "Armar ruta guiada" (método nuevo): el usuario fija la PRIMERA y la ÚLTIMA parada de la zona
    /// (y, opcional, algunas del medio en un puesto fijo). El sistema ordena TODAS las del medio por
    /// calles reales con Google (si no hay clave / hay más de 25 / falla, usa el respaldo en línea recta
    /// vecino-más-cercano desde la primera). Escribe OrderInRoute 1..N en UN SOLO guardado atómico.
    /// La zona se toma de la parada PRIMERA (repartidor si tiene; si no, el vehículo sin repartidor),
    /// así se puede armar el orden ANTES de asignar choferes.
    /// </summary>
    [HttpPost("armar-ruta-guiada")]
    public async Task<IActionResult> ArmarRutaGuiada([FromBody] ArmarRutaGuiadaRequest req)
    {
        if (req is null) return BadRequest(new { error = "Faltan datos." });
        if (req.PrimeraId == req.UltimaId) return BadRequest(new { error = "La primera y la última no pueden ser la misma parada." });

        var primera = await _db.MapeoStops.FindAsync(req.PrimeraId);
        var ultima = await _db.MapeoStops.FindAsync(req.UltimaId);
        if (primera is null || ultima is null) return NotFound(new { error = "No encontré la parada primera o la última." });

        // Zona a ordenar: la de la PRIMERA (repartidor si tiene; si no, el vehículo sin repartidor).
        List<MapeoStop> zona;
        if (primera.AssignedDriverId.HasValue)
            zona = await _db.MapeoStops.Where(x => x.AssignedDriverId == primera.AssignedDriverId && x.FechaReparto == primera.FechaReparto).ToListAsync();
        else if (primera.AssignedVehicleSlot.HasValue)
            zona = await _db.MapeoStops.Where(x => x.AssignedVehicleSlot == primera.AssignedVehicleSlot && x.AssignedDriverId == null && x.FechaReparto == primera.FechaReparto).ToListAsync();
        else
            return BadRequest(new { error = "La zona no está armada todavía (la parada no está en ningún vehículo ni repartidor)." });

        if (!zona.Any(x => x.Id == ultima.Id))
            return BadRequest(new { error = "La última parada es de otra zona." });

        int n = zona.Count;
        var puestos = new MapeoStop?[n + 1]; // 1-based; puestos[1..n]
        puestos[1] = primera;
        puestos[n] = ultima;

        // Fijas del medio (opcional): las clavamos en su puesto pedido, acotado a [2..n-1]; si el puesto
        // ya está ocupado, buscamos el más cercano libre. Ignoramos las que sean la primera/última.
        var fijadas = new HashSet<int> { primera.Id, ultima.Id };
        if (req.FijasMedio != null)
        {
            foreach (var f in req.FijasMedio.OrderBy(f => f.Puesto))
            {
                if (fijadas.Contains(f.StopId)) continue;
                var st = zona.FirstOrDefault(x => x.Id == f.StopId);
                if (st is null) continue;
                int p = Math.Clamp(f.Puesto, 2, Math.Max(2, n - 1));
                if (puestos[p] != null) { int q = p; while (q <= n - 1 && puestos[q] != null) q++; if (q > n - 1) continue; p = q; }
                puestos[p] = st; fijadas.Add(st.Id);
            }
        }

        // Libres = las del medio que Google va a ordenar.
        var libres = zona.Where(x => !fijadas.Contains(x.Id)).ToList();

        List<MapeoStop> libresOrdenadas;
        bool porGoogle = false;
        var ordenIdx = await _routes.OptimizeWaypointOrderAsync(
            ((double)primera.Latitude, (double)primera.Longitude),
            ((double)ultima.Latitude, (double)ultima.Longitude),
            libres.Select(x => ((double)x.Latitude, (double)x.Longitude)).ToList());
        if (ordenIdx != null && ordenIdx.Length == libres.Count && EsPermutacionValida(ordenIdx, libres.Count))
        {
            libresOrdenadas = ordenIdx.Select(i => libres[i]).ToList();
            porGoogle = true;
        }
        else
        {
            // Respaldo: vecino más cercano en línea recta arrancando desde la primera.
            libresOrdenadas = new List<MapeoStop>();
            var rem = new List<MapeoStop>(libres);
            double curLat = (double)primera.Latitude, curLng = (double)primera.Longitude;
            while (rem.Count > 0)
            {
                var next = rem.OrderBy(s => Hav(curLat, curLng, (double)s.Latitude, (double)s.Longitude)).First();
                libresOrdenadas.Add(next);
                curLat = (double)next.Latitude; curLng = (double)next.Longitude;
                rem.Remove(next);
            }
        }

        // Rellenamos los puestos libres (los que no quedaron fijados) con las libres ya ordenadas.
        int li = 0;
        for (int p = 1; p <= n && li < libresOrdenadas.Count; p++)
            if (puestos[p] == null) puestos[p] = libresOrdenadas[li++];

        var now = DateTime.UtcNow;
        for (int p = 1; p <= n; p++)
        {
            if (puestos[p] == null) continue;
            puestos[p]!.OrderInRoute = p;
            puestos[p]!.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();
        return Ok(new { total = n, porGoogle });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var s = await _db.MapeoStops.FindAsync(id);
        if (s is null) return NotFound(new { error = "Parada no encontrada" });
        _db.MapeoStops.Remove(s);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpDelete]
    public async Task<IActionResult> ClearAll([FromQuery] DateTime? fecha = null)
    {
        var dia = FechaDelMapa(fecha);
        // Snapshot automático antes de borrar — para no perder lo que armó el usuario
        try
        {
            var snap = await MapeoSnapshotsController.BuildSnapshotAsync(_db, "Auto-guardado antes de Empezar desde cero",
                User.Identity?.IsAuthenticated == true ? User.Identity?.Name : null, dia);
            if (snap is not null)
            {
                _db.MapeoRouteSnapshots.Add(snap);
                await _db.SaveChangesAsync();
            }
        }
        catch { /* tolerar — preferimos limpiar igual */ }

        // 2026-09-03: vacía SOLO el día que se está mirando. Antes borraba el mapa entero, que era lo
        // mismo porque había un mapa solo; ahora borraría también lo que preparaste para mañana y el
        // historial de días pasados.
        await _db.MapeoStops.Where(s => s.FechaReparto == dia).ExecuteDeleteAsync();
        return Ok(new { ok = true });
    }

    /// <summary>
    /// Limpia las paradas que quedaron de dias ANTERIORES (creadas antes de hoy, hora Argentina),
    /// para que el mapa arranque limpio cada dia. Lo de HOY se mantiene (no se pierde lo que se esta
    /// cargando ahora aunque se recargue la pagina). Hace un respaldo (snapshot) antes de borrar.
    /// Se llama automaticamente al abrir el mapa.
    /// </summary>
    /// <summary>
    /// 2026-09-03: YA NO BORRA NADA. Antes, al abrir el mapa, esto borraba todas las paradas de días
    /// anteriores (guardaba una foto y las eliminaba) porque el mapa era uno solo y había que
    /// limpiarlo. Con los días, esas paradas SON el historial: el usuario las quiere para mirar lo
    /// que pasó y sacar estadísticas, y ocupan nada (unos 9 MB por año).
    ///
    /// Se deja el endpoint respondiendo OK para no romper a quien lo llame, pero no borra.
    /// Si alguna vez hace falta purgar de verdad, que sea un pedido explícito y por fecha, nunca
    /// algo que se dispare solo al abrir una pantalla.
    /// </summary>
    [HttpPost("clear-stale")]
    public IActionResult ClearStale() => Ok(new { cleared = 0, mensaje = "Las paradas de días anteriores ya no se borran: son el historial." });

    // ══════════════════════════════════════════════════════════════════════════════
    // 2026-09-03: LOS TRES BOTONES DE "TRAER". Idea del usuario: en vez de ir a buscar las cosas a
    // cada pantalla, que el mapa las traiga de un toque, para el día en el que estás parado.
    //
    // ⚠ El corte de las VENTAS lo eligió él y tiene razón de ser: "pendientes" a secas son 782, de
    // las cuales 683 son cotizaciones viejas de todo el año que nadie va a entregar (ni siquiera
    // están cargadas a un repartidor). Con "hoy y ayer" quedan las que de verdad hay que repartir.
    // ══════════════════════════════════════════════════════════════════════════════

    private const int VentasDiasAtras = 1;   // hoy y ayer

    /// <summary>Cuántos hay para traer de cada cosa, sin traer nada. Alimenta el número de los botones.</summary>
    [HttpGet("traer/estado")]
    public async Task<IActionResult> TraerEstado([FromQuery] DateTime? fecha = null)
    {
        var dia = FechaDelMapa(fecha);
        var yaEnElDia = await _db.MapeoStops.Where(s => s.FechaReparto == dia)
            .Select(s => new { s.Origin, s.OriginRefId }).ToListAsync();
        var refsMeli = yaEnElDia.Where(x => x.Origin is "flex" or "me1" && x.OriginRefId != null).Select(x => x.OriginRefId!).ToHashSet();
        var refsVenta = yaEnElDia.Where(x => x.Origin == "venta_cafe" && x.OriginRefId != null).Select(x => x.OriginRefId!).ToHashSet();

        var flex = (await FlexDelDiaAsync(dia)).Count(x => !refsMeli.Contains(x.MeliShipmentId.ToString()));
        var me1 = (await Me1PendientesAsync()).Count(x => !refsMeli.Contains(x.MeliShipmentId.ToString()));
        var ventas = (await VentasRecientesAsync(dia)).Count(v => !refsVenta.Contains(v.Id.ToString()));
        var atrasados = (await FlexAtrasadosAsync(dia)).Count(x => !refsMeli.Contains(x.MeliShipmentId.ToString()));
        return Ok(new { flex, me1, ventas, atrasados, dia = dia.ToString("yyyy-MM-dd") });
    }

    private async Task<List<MeliShipment>> FlexDelDiaAsync(DateTime dia)
    {
        var d1 = dia.AddHours(3); var d2 = dia.AddDays(1).AddHours(3);
        return await _db.MeliShipments
            .Where(s => s.LogisticType == "self_service" && s.Latitude != null && s.Longitude != null
                     && s.Status != "delivered" && s.Status != "cancelled" && s.Status != "not_delivered"
                     && (s.EstimatedDeliveryLimit ?? s.DateCreated) >= d1
                     && (s.EstimatedDeliveryLimit ?? s.DateCreated) < d2)
            .ToListAsync();
    }

    /// <summary>
    /// Flex ATRASADOS: MercadoLibre los prometió para un día anterior y siguen sin entregar. Sin esto
    /// se perdían — "Traer Flex" mira solo el día en el que estás parado, así que un envío prometido
    /// para el lunes que el miércoles sigue dando vueltas no aparecía en ningún botón.
    /// </summary>
    private async Task<List<MeliShipment>> FlexAtrasadosAsync(DateTime dia)
    {
        var d1 = dia.AddHours(3);   // 00:00 del día que estoy mirando, en UTC
        return await _db.MeliShipments
            .Where(s => s.LogisticType == "self_service" && s.Latitude != null && s.Longitude != null
                     && s.Status != "delivered" && s.Status != "cancelled" && s.Status != "not_delivered"
                     && s.EstimatedDeliveryLimit != null && s.EstimatedDeliveryLimit < d1)
            .ToListAsync();
    }

    [HttpPost("traer/atrasados")]
    public async Task<IActionResult> TraerAtrasados([FromQuery] DateTime? fecha = null)
    {
        var dia = FechaDelMapa(fecha);
        await SincronizarFlexDeMeliAsync();
        var n = await SumarEnviosAsync(await FlexAtrasadosAsync(dia), dia);
        return Ok(new { creadas = n, mensaje = Mensaje(n, "atrasados nuevos", dia) });
    }

    /// <summary>ME1 sin entregar. No van por fecha prometida: son pocos y se manejan a mano.</summary>
    private async Task<List<MeliShipment>> Me1PendientesAsync()
        => await _db.MeliShipments
            .Where(s => s.Mode == "me1" && s.Latitude != null && s.Longitude != null
                     && s.Status != "delivered" && s.Status != "cancelled" && s.Status != "not_delivered")
            .ToListAsync();

    /// <summary>Ventas de hoy y ayer sin entregar, con algún domicilio para ubicarlas.</summary>
    private async Task<List<CafeVenta>> VentasRecientesAsync(DateTime dia)
    {
        var desde = dia.AddDays(-VentasDiasAtras);
        return await _db.CafeVentas.Include(v => v.ClienteNav)
            .Where(v => v.EntregadoPorRepartidorId == null && v.Estado != "anulado"
                     && v.Fecha >= desde && v.Fecha < dia.AddDays(1))
            .ToListAsync();
    }

    [HttpPost("traer/flex")]
    public async Task<IActionResult> TraerFlex([FromQuery] DateTime? fecha = null)
    {
        var dia = FechaDelMapa(fecha);
        // 2026-09-03: PRIMERO le preguntamos a MercadoLibre. La sincronización automática solo
        // REFRESCA los envíos que ya tenemos: los Flex nuevos del día entran nada más cuando alguien
        // aprieta "Sincronizar" a mano en la pantalla de MeLi. Sin esta línea el botón miraba una
        // base vacía y decía "0" toda la mañana, que es justo lo que pasó el primer día.
        var buscados = await SincronizarFlexDeMeliAsync();
        var n = await SumarEnviosAsync(await FlexDelDiaAsync(dia), dia);
        var msg = Mensaje(n, "Flex nuevos", dia);
        if (n == 0 && buscados == 0) msg = "Le pregunté a MercadoLibre y todavía no hay Flex para ese día.";
        return Ok(new { creadas = n, mensaje = msg });
    }

    /// <summary>Le pide a MercadoLibre los envíos Flex de los últimos días y los guarda. Tolera el
    /// error: si MeLi no contesta, seguimos con lo que ya tenemos guardado.</summary>
    private async Task<int> SincronizarFlexDeMeliAsync()
    {
        try
        {
            var r = await _shipmentSvc.SyncFlexAsync(daysBack: 3, maxOrdersPerAccount: 200);
            return r?.TotalSynced ?? 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Traer Flex: no pude preguntarle a MercadoLibre, sigo con lo guardado");
            return 0;
        }
    }

    [HttpPost("traer/me1")]
    public async Task<IActionResult> TraerMe1([FromQuery] DateTime? fecha = null)
    {
        var dia = FechaDelMapa(fecha);
        var n = await SumarEnviosAsync(await Me1PendientesAsync(), dia);
        return Ok(new { creadas = n, mensaje = Mensaje(n, "ME1 nuevos", dia) });
    }

    /// <summary>Suma al día los envíos que todavía no están, y devuelve cuántos entraron. Se fija
    /// primero cuáles faltan (en vez de adivinar por la respuesta de cada uno), así el número que
    /// le mostramos al usuario es el de verdad.</summary>
    private async Task<int> SumarEnviosAsync(List<MeliShipment> envios, DateTime dia)
    {
        if (envios.Count == 0) return 0;
        var refs = envios.Select(s => s.MeliShipmentId.ToString()).ToList();
        var yaEstan = (await _db.MapeoStops
            .Where(s => (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null
                     && refs.Contains(s.OriginRefId) && s.FechaReparto == dia)
            .Select(s => s.OriginRefId!).ToListAsync()).ToHashSet();

        int n = 0;
        foreach (var sh in envios)
        {
            if (yaEstan.Contains(sh.MeliShipmentId.ToString())) continue;
            await AddFlexStopFromShipmentAsync(sh, dia);
            n++;
        }
        return n;
    }

    [HttpPost("traer/ventas")]
    public async Task<IActionResult> TraerVentas([FromQuery] DateTime? fecha = null)
    {
        var dia = FechaDelMapa(fecha);
        int n = 0, sinUbicacion = 0;
        foreach (var v in await VentasRecientesAsync(dia))
        {
            var r = await _ventaMapeo.SumarVentaAsync(v, fecha: dia);
            if (r.Ok && !r.YaEstaba) { n++; if (r.SinUbicacion) sinUbicacion++; }
        }
        var msg = Mensaje(n, "ventas nuevas", dia);
        if (sinUbicacion > 0) msg += $" · {sinUbicacion} sin ubicación, buscalas en el mapa";
        return Ok(new { creadas = n, sinUbicacion, mensaje = msg });
    }

    private static string Mensaje(int n, string que, DateTime dia)
        => n == 0 ? $"No hay {que} para sumar a ese día."
                  : $"Sumé {n} al mapa del {dia:dd/MM}.";

    // ══════════ 2026-09-03: armar un día con los envíos que MeLi promete para ese día ══════════

    /// <summary>Cuántos envíos de MercadoLibre hay prometidos para ese día que todavía no están en
    /// el mapa de ese día, y si el llenado automático está prendido o apagado.</summary>
    [HttpGet("auto-armar/estado")]
    public async Task<IActionResult> AutoArmarEstado([FromQuery] DateTime? fecha = null)
    {
        var dia = FechaDelMapa(fecha);
        var d1 = dia.AddHours(3);
        var d2 = dia.AddDays(1).AddHours(3);
        var candidatos = await _db.MeliShipments
            .Where(s => (s.LogisticType == "self_service" || s.Mode == "me1")
                     && s.Latitude != null && s.Longitude != null
                     && s.Status != "delivered" && s.Status != "cancelled" && s.Status != "not_delivered"
                     && (s.EstimatedDeliveryLimit ?? s.DateCreated) >= d1
                     && (s.EstimatedDeliveryLimit ?? s.DateCreated) < d2)
            .Select(s => s.MeliShipmentId.ToString()).ToListAsync();
        var yaEstan = await _db.MapeoStops
            .Where(s => (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null
                     && candidatos.Contains(s.OriginRefId) && s.FechaReparto == dia)
            .CountAsync();
        var activo = (await _db.AppSettings.FindAsync(MapeoAutoArmarBackgroundService.SettingKey))?.Value;
        return Ok(new
        {
            faltan = candidatos.Count - yaEstan,
            total = candidatos.Count,
            automatico = string.Equals(activo, "true", StringComparison.OrdinalIgnoreCase)
        });
    }

    /// <summary>Trae ahora los envíos de MeLi prometidos para ese día. Idempotente.</summary>
    [HttpPost("auto-armar")]
    public async Task<IActionResult> AutoArmar([FromQuery] DateTime? fecha = null)
    {
        var dia = FechaDelMapa(fecha);
        var creadas = await MapeoAutoArmarBackgroundService.ArmarDiaAsync(_db, dia);
        return Ok(new { creadas, mensaje = creadas == 0 ? "No había envíos nuevos para ese día." : $"Sumé {creadas} al mapa del {dia:dd/MM}." });
    }

    public record AutoArmarConfigRequest(bool Activo);

    /// <summary>Prende o apaga el llenado automático del mapa de mañana. Arranca APAGADO.</summary>
    [HttpPost("auto-armar/config")]
    public async Task<IActionResult> AutoArmarConfig([FromBody] AutoArmarConfigRequest req)
    {
        var key = MapeoAutoArmarBackgroundService.SettingKey;
        var st = await _db.AppSettings.FindAsync(key);
        if (st is null) _db.AppSettings.Add(new AppSetting { Key = key, Value = req.Activo ? "true" : "false", UpdatedAt = DateTime.UtcNow });
        else { st.Value = req.Activo ? "true" : "false"; st.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, automatico = req.Activo });
    }

    public record AssignSlotRequest(int? Slot);

    [HttpPut("{id:int}/vehicle-slot")]
    public async Task<IActionResult> AssignSlot(int id, [FromBody] AssignSlotRequest req)
    {
        var s = await _db.MapeoStops.FindAsync(id);
        if (s is null) return NotFound(new { error = "Parada no encontrada" });
        s.AssignedVehicleSlot = req.Slot.HasValue && req.Slot.Value > 0 ? req.Slot.Value : null;
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, slot = s.AssignedVehicleSlot });
    }

    [HttpPost("clear-vehicle-assignments")]
    public async Task<IActionResult> ClearVehicleAssignments([FromQuery] DateTime? fecha = null)
    {
        var dia = FechaDelMapa(fecha);
        await _db.MapeoStops.Where(s => s.FechaReparto == dia).ExecuteUpdateAsync(set => set
            .SetProperty(s => s.AssignedVehicleSlot, (int?)null)
            .SetProperty(s => s.UpdatedAt, DateTime.UtcNow));
        return Ok(new { ok = true });
    }

    public record AssignDriverToSlotRequest(int Slot, int? DriverId, DateTime? Fecha = null);

    /// <summary>Asigna un chofer a todas las paradas de un slot (vehículo del día).</summary>
    [HttpPost("assign-driver-to-slot")]
    public async Task<IActionResult> AssignDriverToSlot([FromBody] AssignDriverToSlotRequest req)
    {
        if (req.Slot <= 0) return BadRequest(new { error = "Slot inválido" });
        int? did = req.DriverId.HasValue && req.DriverId.Value > 0 ? req.DriverId.Value : null;
        var dia = FechaDelMapa(req.Fecha);
        var ids = await _db.MapeoStops.Where(s => s.AssignedVehicleSlot == req.Slot && s.FechaReparto == dia)
            .Select(s => s.Id).ToListAsync();
        var n = await _db.MapeoStops
            .Where(s => s.AssignedVehicleSlot == req.Slot && s.FechaReparto == dia)
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.AssignedDriverId, did)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow));
        // Este es el camino normal del ruteo (zonas primero, choferes al final): también tiene que
        // llegarles al celu, no solo dibujar la ruta.
        await _asignacion.SincronizarAsync(ids);
        return Ok(new { ok = true, updated = n });
    }

    public record AssignBulkRequest(List<int> StopIds, int? DriverId);

    /// <summary>Asigna varios stops al mismo driver (o desasigna si DriverId es null/0).</summary>
    [HttpPost("assign-bulk")]
    public async Task<IActionResult> AssignBulk([FromBody] AssignBulkRequest req)
    {
        if (req.StopIds is null || req.StopIds.Count == 0) return BadRequest(new { error = "Sin stops" });
        var ids = req.StopIds;
        int? did = req.DriverId.HasValue && req.DriverId.Value > 0 ? req.DriverId.Value : null;
        await _db.MapeoStops.Where(s => ids.Contains(s.Id))
            .ExecuteUpdateAsync(set => set
                .SetProperty(s => s.AssignedDriverId, did)
                .SetProperty(s => s.UpdatedAt, DateTime.UtcNow));
        await _asignacion.SincronizarAsync(ids);
        return Ok(new { updated = ids.Count });
    }

    /// <summary>
    /// Reparte automaticamente todos los stops sin driver entre los drivers activos via k-means
    /// usando la distancia geografica (haversine simplificado).
    /// </summary>
    [HttpPost("auto-assign")]
    public async Task<IActionResult> AutoAssign([FromQuery] bool reassignAll = false, [FromQuery] DateTime? fecha = null)
    {
        var drivers = await _db.MapeoDrivers.Where(d => d.IsActive).OrderBy(d => d.Id).ToListAsync();
        if (drivers.Count == 0) return BadRequest(new { error = "No hay drivers activos" });

        var diaAuto = FechaDelMapa(fecha);
        var stopsQ = _db.MapeoStops.Where(s => s.FechaReparto == diaAuto).AsQueryable();
        if (!reassignAll) stopsQ = stopsQ.Where(s => s.AssignedDriverId == null);
        var stops = await stopsQ.ToListAsync();
        if (stops.Count == 0) return Ok(new { assigned = 0 });

        // K-means simple. Centroides iniciales: tomamos N stops espaciados.
        int K = drivers.Count;
        var centroids = new List<(double lat, double lng)>();
        for (int i = 0; i < K; i++)
        {
            var idx = (int)Math.Floor((double)i * stops.Count / K);
            var s = stops[idx];
            centroids.Add(((double)s.Latitude, (double)s.Longitude));
        }
        var assignment = new int[stops.Count];
        for (int iter = 0; iter < 20; iter++)
        {
            // Asignar cada stop al centroide mas cercano
            for (int i = 0; i < stops.Count; i++)
            {
                double bestD = double.MaxValue; int bestC = 0;
                for (int c = 0; c < K; c++)
                {
                    var d = Hav((double)stops[i].Latitude, (double)stops[i].Longitude, centroids[c].lat, centroids[c].lng);
                    if (d < bestD) { bestD = d; bestC = c; }
                }
                assignment[i] = bestC;
            }
            // Recalcular centroides como promedio
            var newCentroids = new List<(double lat, double lng)>();
            bool moved = false;
            for (int c = 0; c < K; c++)
            {
                var members = Enumerable.Range(0, stops.Count).Where(i => assignment[i] == c).ToList();
                if (members.Count == 0) { newCentroids.Add(centroids[c]); continue; }
                var avgLat = members.Average(i => (double)stops[i].Latitude);
                var avgLng = members.Average(i => (double)stops[i].Longitude);
                if (Math.Abs(avgLat - centroids[c].lat) > 0.0001 || Math.Abs(avgLng - centroids[c].lng) > 0.0001) moved = true;
                newCentroids.Add((avgLat, avgLng));
            }
            centroids = newCentroids;
            if (!moved) break;
        }

        for (int i = 0; i < stops.Count; i++) stops[i].AssignedDriverId = drivers[assignment[i]].Id;
        await _db.SaveChangesAsync();
        await _asignacion.SincronizarStopsAsync(stops);
        return Ok(new { assigned = stops.Count, drivers = drivers.Count });
    }

    /// <summary>
    /// Optimiza el orden de las paradas de un driver (o de todos) usando nearest-neighbor desde el punto de partida.
    /// </summary>
    [HttpPost("optimize-order")]
    public async Task<IActionResult> OptimizeOrder([FromQuery] int? driverId = null, [FromQuery] int? vehicleSlot = null, [FromQuery] bool all = false, [FromQuery] DateTime? fecha = null)
    {
        // Punto de partida (de AppSettings)
        double? startLat = null, startLng = null;
        var latStr = (await _db.AppSettings.FindAsync("mapeo.start.lat"))?.Value;
        var lngStr = (await _db.AppSettings.FindAsync("mapeo.start.lng"))?.Value;
        if (double.TryParse(latStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var la)) startLat = la;
        if (double.TryParse(lngStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lo)) startLng = lo;

        // Determinar grupos a optimizar: TODO junto (all), por VEHICULO (slot), por DRIVER, o todos los drivers.
        // 2026-09-03: siempre dentro del día que se está mirando — si no, optimizaría mezclando días.
        var diaOpt = FechaDelMapa(fecha);
        var grupos = new List<List<MapeoStop>>();
        if (all)
        {
            // "Armar ruta óptima" de TODAS las paradas cargadas como una sola ruta (aunque no tengan chofer).
            var todas = await _db.MapeoStops.Where(s => s.FechaReparto == diaOpt).ToListAsync();
            if (todas.Count > 0) grupos.Add(todas);
        }
        else if (vehicleSlot.HasValue && vehicleSlot.Value > 0)
        {
            var stopsV = await _db.MapeoStops.Where(s => s.AssignedVehicleSlot == vehicleSlot.Value && s.FechaReparto == diaOpt).ToListAsync();
            if (stopsV.Count > 0) grupos.Add(stopsV);
        }
        else
        {
            IEnumerable<int?> driverIds;
            if (driverId.HasValue && driverId.Value > 0) driverIds = new int?[] { driverId.Value };
            else driverIds = await _db.MapeoStops.Where(s => s.AssignedDriverId != null && s.FechaReparto == diaOpt)
                .Select(s => s.AssignedDriverId).Distinct().ToListAsync();
            foreach (var did in driverIds)
            {
                var stopsD = await _db.MapeoStops.Where(s => s.AssignedDriverId == did && s.FechaReparto == diaOpt).ToListAsync();
                if (stopsD.Count > 0) grupos.Add(stopsD);
            }
        }

        int optimized = 0;
        int optimizedByGoogle = 0;
        foreach (var grupo in grupos)
        {
            // 1) Intentar ordenar por CALLES REALES con Google (barato: 1 consulta por grupo).
            //    Si no hay clave, hay demasiadas paradas (>25) o falla la API, se usa el respaldo (linea recta).
            if (await TryOptimizeWithGoogleAsync(grupo, startLat, startLng))
            {
                optimized += grupo.Count;
                optimizedByGoogle += grupo.Count;
                continue;
            }

            // 2) Respaldo: nearest-neighbor con distancia en linea recta (haversine), desde el punto de partida.
            double curLat = startLat ?? (double)grupo[0].Latitude;
            double curLng = startLng ?? (double)grupo[0].Longitude;
            var remaining = new List<MapeoStop>(grupo);
            int order = 1;
            while (remaining.Count > 0)
            {
                var next = remaining.OrderBy(s => Hav(curLat, curLng, (double)s.Latitude, (double)s.Longitude)).First();
                next.OrderInRoute = order++;
                next.UpdatedAt = DateTime.UtcNow;
                curLat = (double)next.Latitude; curLng = (double)next.Longitude;
                remaining.Remove(next);
                optimized++;
            }
        }
        await _db.SaveChangesAsync();
        return Ok(new { optimized, optimizedByGoogle });
    }

    public record TramoTransitoDto(int Start, int End, string Speed);
    public record RutaLegDto(int Seconds, int Meters, string? Encoded, string From, string To,
        List<TramoTransitoDto>? Transito = null);
    public record RutaOverviewDto(string Key, string Label, string Color, int? DriverId,
        int DurationSeconds, int DistanceMeters, string? EncodedPolyline, int StopCount,
        List<string> Segments, List<RutaLegDto> Legs, int? VehicleSlot = null);

    // ── Recorridos ya calculados, guardados en memoria (para no pagarle a Google de más) ──
    // Cada recorrido se lo pedimos a Google, y esas consultas SE COBRAN (más todavía con el color de
    // tránsito). Desde que las líneas arrancan prendidas solas, cada vez que alguien abre el Mapeo se
    // pediría todo de nuevo: abrir el mapa 10 veces = 10 veces la misma cuenta. Por eso guardamos el
    // resultado y lo reusamos mientras NADA haya cambiado.
    // La "firma" incluye todo lo que altera un recorrido (qué parada, en qué zona, con qué chofer, en
    // qué puesto, y el punto de partida): si cambia cualquier cosa, la firma cambia y se recalcula solo.
    // El tope de 10 minutos es por el tránsito: los tiempos envejecen, así que igual se refresca solo.
    // Guardamos VARIOS resultados (no uno solo) porque conviven la vista por zonas y la "Ruta única":
    // con un solo lugar, ir y volver entre las dos borraba el guardado y le pagábamos a Google de nuevo.
    private static readonly object _rutasCacheLock = new();
    private static readonly Dictionary<string, (DateTime hora, List<RutaOverviewDto> datos)> _rutasCache = new();
    private static readonly TimeSpan _rutasCacheVida = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Devuelve, por repartidor (o como ruta única), el tiempo estimado + km + la línea dibujable de la ruta.
    /// Usa el orden ya calculado (OrderInRoute) y el punto de partida configurado.
    /// refresh=true saltea lo guardado y se lo vuelve a preguntar a Google (botón "Actualizar").
    /// </summary>
    [HttpGet("routes-overview")]
    public async Task<IActionResult> RoutesOverview([FromQuery] bool single = false, [FromQuery] bool refresh = false, [FromQuery] DateTime? fecha = null)
    {
        double? startLat = null, startLng = null;
        var latStr = (await _db.AppSettings.FindAsync("mapeo.start.lat"))?.Value;
        var lngStr = (await _db.AppSettings.FindAsync("mapeo.start.lng"))?.Value;
        if (double.TryParse(latStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var la)) startLat = la;
        if (double.TryParse(lngStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lo)) startLng = lo;

        var diaRuta = FechaDelMapa(fecha);
        var conOrden = await _db.MapeoStops.Include(x => x.AssignedDriver).Where(x => x.FechaReparto == diaRuta)
            .Where(s => s.OrderInRoute != null).ToListAsync();
        // Sin paradas ordenadas no hay recorrido que dibujar: cortamos acá y NO le preguntamos nada a
        // Google (abrir el Mapeo antes de armar las rutas no cuesta un peso).
        if (conOrden.Count == 0) return Ok(new List<RutaOverviewDto>());

        // ¿Ya lo tenemos calculado y sigue valiendo? (ver comentario del cache más arriba)
        var firma = string.Join("|", new[] { single ? "U" : "Z", $"{startLat},{startLng}" }
            .Concat(conOrden.OrderBy(s => s.Id)
                .Select(s => $"{s.Id}:{s.AssignedDriverId}:{s.AssignedVehicleSlot}:{s.OrderInRoute}:{s.Latitude}:{s.Longitude}")));
        if (!refresh)
        {
            lock (_rutasCacheLock)
            {
                if (_rutasCache.TryGetValue(firma, out var guardado)
                    && DateTime.UtcNow - guardado.hora < _rutasCacheVida)
                {
                    return Ok(guardado.datos);
                }
            }
        }

        // single = todas las paradas como UNA ruta. Si no, CADA ZONA es una ruta INDEPENDIENTE: agrupamos por
        // repartidor si tiene; si no, por vehículo/zona (slot); si no tiene ninguno, van juntas como "sin asignar".
        // (Antes agrupaba SOLO por repartidor, así que dos zonas de vehículo distintas —ambas sin chofer— se
        //  mezclaban en una sola línea que cruzaba las dos. Este es el fix.)
        List<(string key, int? did, int? slot, MapeoDriver? drv, List<MapeoStop> ss)> grupos;
        if (single)
        {
            grupos = new() { ("single", null, null, null, conOrden) };
        }
        else
        {
            grupos = conOrden
                .GroupBy(s => s.AssignedDriverId.HasValue ? $"d{s.AssignedDriverId.Value}"
                             : (s.AssignedVehicleSlot.HasValue ? $"v{s.AssignedVehicleSlot.Value}" : "none"))
                .Select(g =>
                {
                    var f = g.First();
                    int? slot = f.AssignedDriverId.HasValue ? null : f.AssignedVehicleSlot;
                    return (g.Key, f.AssignedDriverId, slot, f.AssignedDriver, g.ToList());
                })
                .ToList();
        }

        var result = new List<RutaOverviewDto>();
        foreach (var (key, did, slot, drv, ss) in grupos)
        {
            var ordered = ss.OrderBy(s => s.OrderInRoute).ToList();
            var pts = ordered.Select(s => ((double)s.Latitude, (double)s.Longitude)).ToList();

            // Secuencia COMPLETA en orden de visita: arranca en el punto de partida (si está configurado)
            // y termina en la última parada. El nuevo motor la parte sola en tramos de ≤25 si hace falta.
            var seq = new List<(double lat, double lng)>();
            if (startLat.HasValue && startLng.HasValue) seq.Add((startLat.Value, startLng.Value));
            seq.AddRange(pts);

            var rr = await _routes.ComputeRouteFullAsync(seq);
            string label = single ? "Ruta única"
                : (drv?.Nombre ?? (slot.HasValue ? $"Zona {slot.Value}" : "Sin repartidor"));
            // Color de la ruta = color de su ZONA (para distinguir varias en el mapa y que coincida con el
            // cuadradito de la zona): color del repartidor si tiene; si no, el color del vehículo/zona (slot);
            // si no tiene ninguno, azul. La ruta única siempre azul.
            string color = single ? "#1d4ed8"
                : (!string.IsNullOrEmpty(drv?.Color) ? drv!.Color!
                   : (slot.HasValue ? VehicleColorHex(slot.Value) : "#1d4ed8"));
            var segments = rr?.Segments ?? new List<string>();
            // Nombres de cada punto EN ORDEN de visita: "Salida" (si hay punto de partida) + el número de cada
            // parada (1, 2, 3…). Sirve para rotular cada tramo ("de la 2 a la 3") cuando se toca la línea.
            var nombresPuntos = new List<string>();
            if (startLat.HasValue && startLng.HasValue) nombresPuntos.Add("Salida");
            for (int i = 0; i < ordered.Count; i++) nombresPuntos.Add((i + 1).ToString());
            var rawLegs = rr?.Legs ?? new List<GoogleRoutesService.RouteLeg>();
            var legs = new List<RutaLegDto>();
            for (int k = 0; k < rawLegs.Count; k++)
            {
                string from = k < nombresPuntos.Count ? nombresPuntos[k] : "";
                string to = (k + 1) < nombresPuntos.Count ? nombresPuntos[k + 1] : "";
                var trans = (rawLegs[k].Intervals ?? new()).Select(i => new TramoTransitoDto(i.Start, i.End, i.Speed)).ToList();
                legs.Add(new RutaLegDto(rawLegs[k].DurationSeconds, rawLegs[k].DistanceMeters, rawLegs[k].EncodedPolyline, from, to, trans));
            }
            result.Add(new RutaOverviewDto(
                key, label, color, did,
                rr?.DurationSeconds ?? 0, rr?.DistanceMeters ?? 0,
                segments.FirstOrDefault(), ordered.Count, segments, legs, slot));
        }
        // Guardamos lo calculado para reusarlo mientras nada cambie (así no le pagamos a Google
        // la misma cuenta cada vez que alguien abre el mapa).
        lock (_rutasCacheLock)
        {
            // Limpieza: sacamos los vencidos y, si igual quedaron muchos (cambió el reparto varias
            // veces), vaciamos todo. Son 2 o 3 entradas en el uso normal; esto es solo por las dudas.
            foreach (var vieja in _rutasCache.Where(kv => DateTime.UtcNow - kv.Value.hora >= _rutasCacheVida)
                                             .Select(kv => kv.Key).ToList())
                _rutasCache.Remove(vieja);
            if (_rutasCache.Count > 8) _rutasCache.Clear();
            _rutasCache[firma] = (DateTime.UtcNow, result);
        }
        return Ok(result);
    }

    // Paleta de colores de las zonas/vehículos (igual que la del frontend VEHICLE_COLORS, para que la línea
    // de cada zona coincida con su cuadradito). Zona 1 = azul, Zona 2 = rojo, etc.
    private static readonly string[] VehicleColors =
    {
        "#1d4ed8", "#dc2626", "#16a34a", "#d97706", "#7c3aed",
        "#0891b2", "#be185d", "#65a30d", "#ea580c", "#4338ca"
    };
    private static string VehicleColorHex(int slot)
        => VehicleColors[((slot - 1) % VehicleColors.Length + VehicleColors.Length) % VehicleColors.Length];

    /// <summary>
    /// Un solo TRAMO (de un punto A a un punto B) por las calles reales, con el tiempo (con tránsito),
    /// los metros y la línea codificada para dibujarlo. Lo usa el modo "Armar ruta" interactivo del mapa:
    /// cada vez que el usuario pincha un envío, pedimos solo el tramo nuevo (barato, una llamada) y lo
    /// dibujamos al toque, estilo Google Maps. Devuelve ok=false si no hay clave o Google falla.
    /// </summary>
    [HttpGet("leg")]
    public async Task<IActionResult> Leg([FromQuery] double fromLat, [FromQuery] double fromLng,
        [FromQuery] double toLat, [FromQuery] double toLng, CancellationToken ct)
    {
        var r = await _routes.ComputeRouteAsync((fromLat, fromLng), (toLat, toLng),
            Array.Empty<(double lat, double lng)>(), ct);
        if (r is null) return Ok(new { ok = false });
        var trans = (r.Intervals ?? new()).Select(i => new { start = i.Start, end = i.End, speed = i.Speed });
        return Ok(new { ok = true, seconds = r.DurationSeconds, meters = r.DistanceMeters, encoded = r.EncodedPolyline, transito = trans });
    }

    public record RutaAhorroDto(string Label, string Color, int? DriverId,
        int ActualSeconds, int OptimoSeconds, int ActualMeters, int OptimoMeters, int StopCount, bool Calculable);

    /// <summary>
    /// Estima cuánto se ahorraría en tiempo/km si se optimizara el orden de cada repartidor: compara el
    /// ORDEN ACTUAL (como está guardado) contra el ORDEN ÓPTIMO que sugiere Google. NO guarda nada; es solo
    /// para mostrar "antes vs después" antes de aplicar. Si un repartidor tiene más de 25 paradas, no se
    /// puede calcular el óptimo en una sola consulta y se marca Calculable=false.
    /// </summary>
    [HttpGet("routes-savings")]
    public async Task<IActionResult> RoutesSavings([FromQuery] DateTime? fecha = null)
    {
        double? startLat = null, startLng = null;
        var latStr = (await _db.AppSettings.FindAsync("mapeo.start.lat"))?.Value;
        var lngStr = (await _db.AppSettings.FindAsync("mapeo.start.lng"))?.Value;
        if (double.TryParse(latStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var la)) startLat = la;
        if (double.TryParse(lngStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var lo)) startLng = lo;

        var diaRuta = FechaDelMapa(fecha);
        var conOrden = await _db.MapeoStops.Include(x => x.AssignedDriver).Where(x => x.FechaReparto == diaRuta)
            .Where(s => s.OrderInRoute != null && s.AssignedDriverId != null).ToListAsync();
        var grupos = conOrden.GroupBy(s => s.AssignedDriverId)
            .Select(g => (drv: g.First().AssignedDriver, ordered: g.OrderBy(s => s.OrderInRoute).ToList()))
            .ToList();

        var res = new List<RutaAhorroDto>();
        foreach (var (drv, ordered) in grupos)
        {
            var pts = ordered.Select(s => ((double)s.Latitude, (double)s.Longitude)).ToList();
            string label = drv?.Nombre ?? "Sin repartidor";
            string color = string.IsNullOrEmpty(drv?.Color) ? "#6b7280" : drv!.Color!;

            // Recorrido en el ORDEN ACTUAL.
            var seqActual = new List<(double lat, double lng)>();
            if (startLat.HasValue && startLng.HasValue) seqActual.Add((startLat.Value, startLng.Value));
            seqActual.AddRange(pts);
            var rrActual = await _routes.ComputeRouteFullAsync(seqActual);

            // Orden ÓPTIMO sugerido por Google (solo si entra en el límite de 25).
            List<(double lat, double lng)>? ptsOpt = null;
            if (pts.Count >= 1 && pts.Count <= GoogleRoutesService.MaxWaypoints)
            {
                if (startLat.HasValue && startLng.HasValue)
                {
                    var start = (startLat.Value, startLng.Value);
                    var order = await _routes.OptimizeWaypointOrderAsync(start, start, pts);
                    if (order != null && order.Length == pts.Count) ptsOpt = order.Select(i => pts[i]).ToList();
                }
                else if (pts.Count >= 2)
                {
                    var rest = pts.Skip(1).ToList();
                    var order = await _routes.OptimizeWaypointOrderAsync(pts[0], pts[0], rest);
                    if (order != null && order.Length == rest.Count)
                    {
                        ptsOpt = new List<(double lat, double lng)> { pts[0] };
                        ptsOpt.AddRange(order.Select(i => rest[i]));
                    }
                }
                else ptsOpt = pts; // una sola parada: el óptimo es ella misma
            }

            int optSec = rrActual?.DurationSeconds ?? 0, optMet = rrActual?.DistanceMeters ?? 0;
            bool calculable = false;
            if (ptsOpt != null)
            {
                var seqOpt = new List<(double lat, double lng)>();
                if (startLat.HasValue && startLng.HasValue) seqOpt.Add((startLat.Value, startLng.Value));
                seqOpt.AddRange(ptsOpt);
                var rrOpt = await _routes.ComputeRouteFullAsync(seqOpt);
                if (rrOpt != null && rrActual != null)
                {
                    optSec = rrOpt.DurationSeconds; optMet = rrOpt.DistanceMeters; calculable = true;
                }
            }

            res.Add(new RutaAhorroDto(label, color, drv?.Id,
                rrActual?.DurationSeconds ?? 0, optSec, rrActual?.DistanceMeters ?? 0, optMet,
                ordered.Count, calculable));
        }
        return Ok(res);
    }

    /// <summary>
    /// Aplica el filtro por modo de entrega (today / tomorrow / overdue / all) usando EstimatedDeliveryLimit.
    /// Refleja la misma logica que MeliShipmentsController.ListFlex para mantener coherencia entre vistas.
    /// </summary>
    private IQueryable<MeliShipment> ApplyDeliveryModeFilter(IQueryable<MeliShipment> q, string mode, DateTime? fecha = null)
    {
        var nowLocal = DateTime.UtcNow.AddHours(-3); // Argentina
        var todayLocal = nowLocal.Date;
        // 2026-09-03: con los días del mapa, "el día que estoy mirando" es un modo más. MercadoLibre
        // nos dice para cuándo promete cada envío (EstimatedDeliveryLimit) y le acierta: sobre 2.101
        // envíos entregados, 1.963 llegaron exactamente el día prometido. Por eso se puede armar el
        // mapa de un día futuro con los envíos que MeLi promete para ese día.
        if (string.Equals(mode, "dia", StringComparison.OrdinalIgnoreCase) && fecha.HasValue)
        {
            var d1 = fecha.Value.Date.AddHours(3);
            var d2 = fecha.Value.Date.AddDays(1).AddHours(3);
            return q.Where(s => (s.EstimatedDeliveryLimit ?? s.DateCreated) >= d1
                             && (s.EstimatedDeliveryLimit ?? s.DateCreated) < d2);
        }
        switch ((mode ?? "today").ToLowerInvariant())
        {
            case "today":
                {
                    var t1 = todayLocal.AddHours(3);
                    var t2 = todayLocal.AddDays(1).AddHours(3);
                    return q.Where(s => (s.EstimatedDeliveryLimit ?? s.DateCreated) >= t1
                                     && (s.EstimatedDeliveryLimit ?? s.DateCreated) < t2);
                }
            case "tomorrow":
                {
                    var t1 = todayLocal.AddDays(1).AddHours(3);
                    var t2 = todayLocal.AddDays(2).AddHours(3);
                    return q.Where(s => (s.EstimatedDeliveryLimit ?? s.DateCreated) >= t1
                                     && (s.EstimatedDeliveryLimit ?? s.DateCreated) < t2);
                }
            case "overdue":
                {
                    var nowUtc = DateTime.UtcNow;
                    return q.Where(s => s.EstimatedDeliveryLimit != null && s.EstimatedDeliveryLimit < nowUtc);
                }
            case "all":
            default:
                return q;
        }
    }

    /// <summary>
    /// Ordena las paradas del grupo por CALLES REALES usando Google Routes API. Si hay punto de partida
    /// configurado, arranca desde ahi; si no, deja como primera la primer parada del grupo (igual que el respaldo).
    /// Escribe OrderInRoute en cada parada. Devuelve true si Google resolvio el orden; false para que el
    /// llamador use el respaldo en linea recta (sin clave, grupo > 25 paradas, o error de la API).
    /// </summary>
    private async Task<bool> TryOptimizeWithGoogleAsync(List<MapeoStop> grupo, double? startLat, double? startLng)
    {
        if (!_routes.IsConfigured || grupo.Count == 0) return false;
        var now = DateTime.UtcNow;
        try
        {
            if (startLat.HasValue && startLng.HasValue)
            {
                var start = (startLat.Value, startLng.Value);
                var inter = grupo.Select(s => ((double)s.Latitude, (double)s.Longitude)).ToList();
                var order = await _routes.OptimizeWaypointOrderAsync(start, start, inter);
                if (!EsPermutacionValida(order, grupo.Count)) return false;
                int ord = 1;
                foreach (var idx in order!) { grupo[idx].OrderInRoute = ord++; grupo[idx].UpdatedAt = now; }
                return true;
            }
            else
            {
                // Sin punto de partida: la primer parada del grupo queda como arranque fijo.
                var first = grupo[0];
                var origin = ((double)first.Latitude, (double)first.Longitude);
                var rest = grupo.Skip(1).ToList();
                var inter = rest.Select(s => ((double)s.Latitude, (double)s.Longitude)).ToList();
                var order = await _routes.OptimizeWaypointOrderAsync(origin, origin, inter);
                if (!EsPermutacionValida(order, rest.Count)) return false;
                int ord = 1;
                first.OrderInRoute = ord++; first.UpdatedAt = now;
                foreach (var idx in order!) { rest[idx].OrderInRoute = ord++; rest[idx].UpdatedAt = now; }
                return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Optimización con Google falló para un grupo; se usa el respaldo en línea recta.");
            return false;
        }
    }

    /// <summary>Valida que 'order' sea una permutación EXACTA de 0..count-1 (evita índices fuera de rango de Google).</summary>
    private static bool EsPermutacionValida(int[]? order, int count)
    {
        if (order is null || order.Length != count) return false;
        if (count == 0) return true;
        var visto = new bool[count];
        foreach (var i in order)
        {
            if (i < 0 || i >= count || visto[i]) return false;
            visto[i] = true;
        }
        return true;
    }

    /// <summary>Distancia haversine en km (aproximada).</summary>
    private static double Hav(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371.0;
        double toRad(double d) => d * Math.PI / 180.0;
        var dLat = toRad(lat2 - lat1);
        var dLng = toRad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(toRad(lat1)) * Math.Cos(toRad(lat2)) *
                Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }

    /// <summary>Cuenta cuantos Flex pendientes hay para importar (dado un rango de dias). Sirve para el preview antes de confirmar.</summary>
    [HttpGet("import-flex-preview")]
    public async Task<IActionResult> ImportFlexPreview([FromQuery] string mode = "today", [FromQuery] DateTime? fecha = null)
    {
        // Mismo modo que el filtro principal del Mapeo: today / tomorrow / overdue / all.
        var q = _db.MeliShipments
            .Where(s => s.LogisticType == "self_service"
                     && s.Status != "delivered" && s.Status != "cancelled"
                     && s.Latitude != null && s.Longitude != null);
        q = ApplyDeliveryModeFilter(q, mode, fecha);
        var ships = await q
            .Select(s => new { s.MeliShipmentId, s.ReceiverName, s.City, s.AddressLine })
            .ToListAsync();
        var diaImport = FechaDelMapa(fecha);
        var existingRefs = await _db.MapeoStops.Where(s => s.FechaReparto == diaImport)
            .Where(s => s.Origin == "flex")
            .Select(s => s.OriginRefId)
            .ToListAsync();
        var existingSet = new HashSet<string?>(existingRefs);
        var nuevos = ships.Where(x => !existingSet.Contains(x.MeliShipmentId.ToString())).ToList();
        return Ok(new
        {
            total = ships.Count,
            yaCargados = ships.Count - nuevos.Count,
            aImportar = nuevos.Count,
            sample = nuevos.Take(5).Select(x => new { x.ReceiverName, x.City, x.AddressLine })
        });
    }

    /// <summary>Importa todos los shipments Flex pendientes como paradas (si todavía no existen).</summary>
    [HttpPost("import-flex")]
    public async Task<IActionResult> ImportFlex([FromQuery] string mode = "today", [FromQuery] DateTime? fecha = null)
    {
        var q = _db.MeliShipments
            .Where(s => s.LogisticType == "self_service"
                     && s.Status != "delivered" && s.Status != "cancelled"
                     && s.Latitude != null && s.Longitude != null);
        q = ApplyDeliveryModeFilter(q, mode, fecha);
        var ships = await q
            .ToListAsync();
        // Excluir las que ya están como stops
        var diaImport = FechaDelMapa(fecha);
        var existingRefs = await _db.MapeoStops.Where(s => s.FechaReparto == diaImport)
            .Where(s => s.Origin == "flex")
            .Select(s => s.OriginRefId)
            .ToListAsync();
        var existingSet = new HashSet<string?>(existingRefs);
        int created = 0;
        foreach (var sh in ships)
        {
            var refId = sh.MeliShipmentId.ToString();
            if (existingSet.Contains(refId)) continue;
            _db.MapeoStops.Add(new MapeoStop
            {
                Origin = "flex",
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
                CreatedAt = DateTime.UtcNow
            });
            created++;
        }
        await _db.SaveChangesAsync();
        return Ok(new { created, total = ships.Count });
    }

    public record ScanFlexRequest(string Code, DateTime? Fecha = null);

    /// <summary>
    /// Suma UNA parada al mapa a partir del QR de una etiqueta Flex escaneada con el celular.
    /// El QR trae un JSON tipo {"id":"47599650926",...}; extraemos el id y buscamos ese envio
    /// (que ya debe estar sincronizado con su ubicacion). Idempotente: si ya estaba, avisa y no duplica.
    /// </summary>
    [HttpPost("scan-flex")]
    public async Task<IActionResult> ScanFlex([FromBody] ScanFlexRequest req)
    {
        // ¿Es el QR de una factura/cotización NUESTRA? (URL .../repartidor/{token}).
        // Si sí, buscamos la venta por ese token y la sumamos al mapa (misma base que el botón del listado).
        var ventaToken = ExtractRepartidorToken(req?.Code);
        if (ventaToken is not null)
        {
            var venta = await _db.CafeVentas.Include(x => x.ClienteNav).FirstOrDefaultAsync(x => x.PublicToken == ventaToken);
            if (venta is null)
                return Ok(new { ok = false, motivo = "no_encontrado", mensaje = "No reconozco ese comprobante (puede ser de un alquiler o de otra cuenta)." });
            var rv = await _ventaMapeo.SumarVentaAsync(venta);
            return Ok(new { ok = rv.Ok, yaEstaba = rv.YaEstaba, motivo = rv.Motivo, mensaje = rv.Mensaje, nombre = rv.Nombre, localidad = rv.Localidad, stopId = rv.StopId });
        }

        // ¿Es el QR de una reserva de ALQUILER? (URL .../alquiler/{token}).
        var alqToken = ExtractAlquilerToken(req?.Code);
        if (alqToken is not null)
        {
            var reserva = await _db.AlqReservas.Include(x => x.ClienteNav).FirstOrDefaultAsync(x => x.PublicToken == alqToken);
            if (reserva is null)
                return Ok(new { ok = false, motivo = "no_encontrado", mensaje = "No reconozco ese comprobante de alquiler." });
            var ra = await _alqMapeo.SumarReservaAsync(reserva);
            return Ok(new { ok = ra.Ok, yaEstaba = ra.YaEstaba, motivo = ra.Motivo, mensaje = ra.Mensaje, nombre = ra.Nombre, localidad = ra.Localidad, stopId = ra.StopId });
        }

        // ¿Es el QR de un recibo de VISITA? (URL .../visita/{token}).
        var visitaToken = ExtractVisitaToken(req?.Code);
        if (visitaToken is not null)
        {
            var visita = await _db.Visitas.FirstOrDefaultAsync(x => x.PublicToken == visitaToken);
            if (visita is null)
                return Ok(new { ok = false, motivo = "no_encontrado", mensaje = "No reconozco ese recibo de visita." });
            var rvi = await _visitaMapeo.SumarVisitaAsync(visita);
            return Ok(new { ok = rvi.Ok, yaEstaba = rvi.YaEstaba, mensaje = rvi.Mensaje, nombre = visita.ClienteNombre, stopId = rvi.StopId });
        }

        var id = ExtractShipmentId(req?.Code);
        if (id is null)
            return Ok(new { ok = false, motivo = "sin_id", mensaje = "No pude leer el numero de envio de ese codigo." });

        var sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == id.Value);
        if (sh is null)
        {
            // No estaba sincronizado: lo traemos de MeLi en el momento (reemplaza al viejo paso "Traer Flex").
            try { await _shipmentSvc.SyncSingleShipmentAsync(id.Value); }
            catch (Exception ex) { _logger.LogWarning(ex, "scan-flex: no se pudo traer el envio {Id} de MeLi", id.Value); }
            sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == id.Value);
        }
        if (sh is null)
            return Ok(new { ok = false, motivo = "no_encontrado", id = id.Value, mensaje = $"No pude traer el envio {id.Value} de MercadoLibre. Puede ser de otra cuenta, o muy nuevo (esperá unos minutos)." });
        return Ok(await AddFlexStopFromShipmentAsync(sh, req?.Fecha));
    }

    /// <summary>Crea (o reconoce) la parada de un envío MeLi Flex/ME1 ya sincronizado. Idempotente por
    /// OriginRefId = MeliShipmentId. Devuelve el mismo shape que scan-flex.</summary>
    private async Task<object> AddFlexStopFromShipmentAsync(MeliShipment sh, DateTime? fecha = null)
    {
        if (sh.Latitude is null || sh.Longitude is null)
            return new { ok = false, motivo = "sin_ubicacion", id = sh.MeliShipmentId, nombre = sh.ReceiverName, mensaje = "Ese envio no tiene ubicacion cargada, no lo puedo poner en el mapa." };

        // 2026-09-03: la parada es de UN día. Si el mismo envío ya estuvo en el mapa de otro día
        // (ayer no se pudo entregar y hoy se vuelve a escanear), esa queda como historial y hoy nace
        // una nueva, limpia. Antes se "revivía" la vieja, que era el parche de cuando había un solo mapa.
        var dia = FechaDelMapa(fecha);
        var refId = sh.MeliShipmentId.ToString();
        var existente = await _db.MapeoStops.FirstOrDefaultAsync(s =>
            (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId == refId && s.FechaReparto == dia);
        if (existente is not null)
            return new { ok = true, yaEstaba = true, id = sh.MeliShipmentId, nombre = sh.ReceiverName, localidad = sh.City, stopId = existente.Id,
                         mensaje = "Ya estaba en el mapa de ese día." };

        var stop = new MapeoStop
        {
            Origin = string.Equals(sh.Mode, "me1", System.StringComparison.OrdinalIgnoreCase) ? "me1" : "flex",
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
            FechaReparto = dia,
            CreatedAt = DateTime.UtcNow
        };
        _db.MapeoStops.Add(stop);
        await _db.SaveChangesAsync();
        return new { ok = true, yaEstaba = false, id = sh.MeliShipmentId, nombre = sh.ReceiverName, localidad = sh.City, stopId = stop.Id, mensaje = "Agregado al mapa." };
    }

    public record ByNumberRequest(string Number, DateTime? Fecha = null);

    /// <summary>
    /// Trae UNA parada al mapa a partir de un NÚMERO tipeado a mano (plan B si falla el escáner/impresora).
    /// Reconoce, en este orden: (1) nº de VENTA propia (Cafe_Ventas), (2) nº de ALQUILER (AlqReservas),
    /// (3) nº de ENVÍO o nº de VENTA de MercadoLibre (Flex/ME1). Si el envío MeLi no estaba sincronizado,
    /// lo trae de MeLi en el momento. Mismo shape de respuesta que scan-flex.
    /// </summary>
    [HttpPost("by-number")]
    public async Task<IActionResult> ByNumber([FromBody] ByNumberRequest req)
    {
        var raw = (req?.Number ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw))
            return Ok(new { ok = false, motivo = "vacio", mensaje = "Escribí un número." });

        // (1) Venta propia (café) por número exacto.
        var venta = await _db.CafeVentas.Include(x => x.ClienteNav).FirstOrDefaultAsync(x => x.Numero == raw);
        if (venta is not null)
        {
            var rv = await _ventaMapeo.SumarVentaAsync(venta);
            return Ok(new { ok = rv.Ok, yaEstaba = rv.YaEstaba, motivo = rv.Motivo, mensaje = rv.Mensaje, nombre = rv.Nombre, localidad = rv.Localidad, stopId = rv.StopId, tipo = "venta" });
        }

        // (2) Alquiler por número exacto.
        var reserva = await _db.AlqReservas.Include(x => x.ClienteNav).FirstOrDefaultAsync(x => x.Numero == raw);
        if (reserva is not null)
        {
            var ra = await _alqMapeo.SumarReservaAsync(reserva);
            return Ok(new { ok = ra.Ok, yaEstaba = ra.YaEstaba, motivo = ra.Motivo, mensaje = ra.Mensaje, nombre = ra.Nombre, localidad = ra.Localidad, stopId = ra.StopId, tipo = "alquiler" });
        }

        // (2.5) Visita propia por número exacto (números chicos: 1, 12, 0007…).
        if (int.TryParse(raw, out var visNum) && visNum > 0)
        {
            var visita = await _db.Visitas.FirstOrDefaultAsync(x => x.Numero == visNum);
            if (visita is not null)
            {
                var rvi = await _visitaMapeo.SumarVisitaAsync(visita);
                return Ok(new { ok = rvi.Ok, yaEstaba = rvi.YaEstaba, mensaje = rvi.Mensaje, nombre = visita.ClienteNombre, stopId = rvi.StopId, tipo = "visita" });
            }
        }

        // (3) MercadoLibre: nº de envío o nº de venta (order). Nos quedamos solo con los dígitos.
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        if (digits.Length >= 6 && long.TryParse(digits, out var num))
        {
            // 3a) directo como nº de envío (shipment id) que ya tengamos local
            var sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == num);
            // 3b) como nº de venta (order id) ya guardado en el envío
            if (sh is null) sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliOrderId == num);
            // 3c) como nº de venta buscando la orden LOCAL → su envío (sincronizándolo si hace falta)
            if (sh is null)
            {
                var ord = await _db.MeliOrders.FirstOrDefaultAsync(o => o.MeliOrderId == num);
                if (ord?.ShippingId is not null)
                {
                    sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == ord.ShippingId.Value);
                    if (sh is null)
                    {
                        try { await _shipmentSvc.SyncSingleShipmentAsync(ord.ShippingId.Value); }
                        catch (Exception ex) { _logger.LogWarning(ex, "by-number: no se pudo traer el envio {Id} de MeLi", ord.ShippingId.Value); }
                        sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == ord.ShippingId.Value);
                    }
                }
            }
            // 3d) NO está local: le preguntamos a MeLi. Primero como nº de VENTA (order) — recorre las
            // cuentas conectadas, encuentra la orden, saca su envío y lo sincroniza (sirve para ventas viejas).
            if (sh is null)
            {
                try
                {
                    var imp = await _shipmentSvc.ImportByOrderIdAsync(digits);
                    if (imp.ok && imp.shipmentId is not null)
                        sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == imp.shipmentId.Value);
                }
                catch (Exception ex) { _logger.LogWarning(ex, "by-number: ImportByOrderId fallo para {N}", digits); }
            }
            // 3e) último recurso: tratar el número como nº de ENVÍO y traerlo de MeLi en el momento.
            if (sh is null)
            {
                try { await _shipmentSvc.SyncSingleShipmentAsync(num); }
                catch (Exception ex) { _logger.LogWarning(ex, "by-number: no se pudo traer el envio {Id} de MeLi", num); }
                sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == num);
            }
            if (sh is not null)
            {
                var res = await AddFlexStopFromShipmentAsync(sh, req?.Fecha);
                return Ok(res);
            }
        }

        return Ok(new { ok = false, motivo = "no_encontrado", mensaje = $"No encontré ninguna venta, alquiler ni envío con el número {raw}. Revisá que esté bien escrito." });
    }

    /// <summary>Saca el numero de envio de lo que trae el QR (JSON con "id", o si no la corrida de digitos mas larga).</summary>
    private static long? ExtractShipmentId(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        // Preferimos el campo "id" del JSON del QR: {"id":"47599650926",...}. Aceptamos que las
        // comillas o los dos puntos vengan cambiados/faltando (pasa con escaneres fisicos mal
        // configurados). El lookbehind evita confundirlo con "sender_id" o "security_digit".
        var m = System.Text.RegularExpressions.Regex.Match(code, "(?<![A-Za-z0-9_])id\"?\\s*:?\\s*\"?(\\d{6,})");
        if (m.Success && long.TryParse(m.Groups[1].Value, out var vid)) return vid;
        // Respaldo: la corrida de digitos mas larga que sea un numero valido (>= 6 digitos).
        foreach (var run in System.Text.RegularExpressions.Regex.Matches(code, "\\d+")
                     .Cast<System.Text.RegularExpressions.Match>()
                     .Select(x => x.Value)
                     .Where(v => v.Length >= 6)
                     .OrderByDescending(v => v.Length))
        {
            if (long.TryParse(run, out var v)) return v;
        }
        return null;
    }

    /// <summary>Saca el token de una URL de comprobante de VENTA (.../repartidor/{token}). null si no aplica.</summary>
    private static string? ExtractRepartidorToken(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(code, @"/repartidor/([A-Za-z0-9_\-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Saca el token de una URL de comprobante de ALQUILER (.../alquiler/{token}). null si no aplica.</summary>
    private static string? ExtractAlquilerToken(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(code, @"/alquiler/([A-Za-z0-9_\-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>Saca el token de una URL de recibo de VISITA (.../visita/{token}). null si no aplica.</summary>
    private static string? ExtractVisitaToken(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(code, @"/visita/([A-Za-z0-9_\-]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    public record FromShipmentRequest(int ShipmentId, string? Direccion, string? Link, DateTime? Fecha = null);

    /// <summary>
    /// Suma un envío de MercadoLibre (ME1 o Flex) al mapa desde su pantalla (botón "Al mapa").
    /// Usa las coords del envío; si no tiene, geocodifica la dirección. Si tampoco se puede, devuelve
    /// sin_domicilio para que el front pida la dirección. Idempotente por OriginRefId=MeliShipmentId.
    /// </summary>
    [HttpPost("from-shipment")]
    public async Task<IActionResult> FromShipment([FromBody] FromShipmentRequest req)
    {
        var sh = await _db.MeliShipments.FirstOrDefaultAsync(x => x.Id == req.ShipmentId);
        if (sh is null) return NotFound(new { ok = false, mensaje = "Envío no encontrado." });

        var nombre = string.IsNullOrWhiteSpace(sh.ReceiverName) ? "Cliente" : sh.ReceiverName!;
        var direccion = !string.IsNullOrWhiteSpace(sh.AddressLine) ? sh.AddressLine : sh.City;
        var localidad = sh.City;
        var telefono = sh.ReceiverPhone;
        var origin = sh.Mode == "me1" ? "me1" : (sh.LogisticType == "self_service" ? "flex" : "me1");

        decimal? lat = sh.Latitude, lng = sh.Longitude;

        if (req is not null && (!string.IsNullOrWhiteSpace(req.Link) || !string.IsNullOrWhiteSpace(req.Direccion)))
        {
            lat = null; lng = null;
            if (!string.IsNullOrWhiteSpace(req.Link))
            { var r = await _mapsResolver.TryResolverCoordenadasAsync(req.Link); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; } }
            if (lat is null && !string.IsNullOrWhiteSpace(req.Direccion))
            { var q = req.Direccion + (string.IsNullOrWhiteSpace(localidad) ? "" : ", " + localidad) + ", Argentina";
              var r = await _mapsResolver.TryGeocodeAddressAsync(q); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; } }
            if (lat is null)
                return Ok(new { ok = false, motivo = "no_resuelto", mensaje = "No pude encontrar esa dirección. Probá con calle + número + localidad, o pegá un link de Google Maps." });
            if (!string.IsNullOrWhiteSpace(req.Direccion)) direccion = req.Direccion.Trim();
        }
        else if (lat is null || lng is null)
        {
            // ME1 sin coords: geocodificar la dirección del envío.
            if (!string.IsNullOrWhiteSpace(direccion))
            { var q = direccion + (string.IsNullOrWhiteSpace(localidad) ? "" : ", " + localidad) + ", Argentina";
              var r = await _mapsResolver.TryGeocodeAddressAsync(q); if (r.HasValue) { lat = r.Value.lat; lng = r.Value.lng; } }
            if (lat is null)
                return Ok(new { ok = false, motivo = "sin_domicilio", mensaje = "Este envío no tiene ubicación. Cargá la dirección.",
                    nombre, direccionSugerida = direccion, localidad });
        }

        var refId = sh.MeliShipmentId.ToString();
        var diaFrom = FechaDelMapa(req?.Fecha);
        var existente = await _db.MapeoStops.FirstOrDefaultAsync(s => (s.Origin == "me1" || s.Origin == "flex") && s.OriginRefId == refId && s.FechaReparto == diaFrom);
        if (existente is not null)
        {
            existente.Latitude = lat!.Value;
            existente.Longitude = lng!.Value;
            existente.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, yaEstaba = true, mensaje = "Ya estaba en el mapa (actualicé la ubicación).", nombre, localidad });
        }

        _db.MapeoStops.Add(new MapeoStop
        {
            Origin = origin,
            OriginRefId = refId,
            Alias = nombre,
            Direccion = string.IsNullOrWhiteSpace(direccion) ? nombre : direccion!,
            Localidad = localidad,
            Latitude = lat!.Value,
            Longitude = lng!.Value,
            ContactName = nombre,
            Telefono = telefono,
            InternalStatus = "pending",
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, yaEstaba = false, mensaje = "Agregado al mapa.", nombre, localidad });
    }

    /// <summary>
    /// Devuelve la clave del navegador (Maps JavaScript API) para dibujar el mapa de Google.
    /// Es una clave pública restringida por dominio; el front la usa para cargar el mapa.
    /// </summary>
    [HttpGet("map-key")]
    public IActionResult MapKey([FromServices] IConfiguration config)
    {
        var key = config["GOOGLE_MAPS_BROWSER_KEY"] ?? Environment.GetEnvironmentVariable("GOOGLE_MAPS_BROWSER_KEY") ?? "";
        return Ok(new { key });
    }

    /// <summary>
    /// "Ver dirección al tocar": geocodificación inversa (punto → calle+número).
    /// La hace el SERVIDOR con la clave GOOGLE_MAPS_API_KEY (la misma que ya geocodifica direcciones),
    /// porque la clave del navegador puede no tener habilitada la Geocoding API.
    /// </summary>
    [HttpGet("reverse-geocode")]
    public async Task<IActionResult> ReverseGeocode([FromQuery] decimal lat, [FromQuery] decimal lng)
    {
        var addr = await _mapsResolver.TryReverseGeocodeAsync(lat, lng);
        return Ok(new { address = addr });
    }

    /// <summary>
    /// Tipo de calle (asfalto/tierra/empedrado) del domicilio, deducido por IA sobre la foto de
    /// Street View. Se calcula una vez por punto y se cachea; el globito lo muestra como cartelito.
    /// </summary>
    [HttpGet("surface")]
    public async Task<IActionResult> Surface([FromQuery] decimal lat, [FromQuery] decimal lng,
        [FromServices] SurfaceClassifierService surface)
    {
        if (lat == 0 && lng == 0) return Ok(new { tipo = "no_seguro", conf = (string?)null });
        var r = await surface.ClassifyAsync(lat, lng);
        return Ok(new { tipo = r.Tipo, conf = r.Conf });
    }

    /// <summary>
    /// Devuelve un PNG con el QR de una URL (para mostrar en la compu y abrir el escáner en el celular).
    /// Se genera en el servidor (QRCoder) para no depender de librerías del navegador ni del caché.
    /// </summary>
    [HttpGet("escanear-qr")]
    public IActionResult EscanearQr([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return BadRequest();
        using var gen = new QRCoder.QRCodeGenerator();
        using var data = gen.CreateQrCode(url, QRCoder.QRCodeGenerator.ECCLevel.M);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(8);
        return File(png, "image/png");
    }

    // ==== Descargar / Compartir ruta ====
    // 2026-07-31: exporta el listado de la ruta con las columnas que elige el usuario.
    // format = "pdf" | "excel" | "text" (texto plano, para pegar en WhatsApp).
    // driverId opcional: si viene, exporta SOLO la ruta de ese repartidor.
    // Cada columna es un flag para incluirla o no.
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string format = "pdf",
        [FromQuery] int? driverId = null,
        [FromQuery] bool orden = true,
        [FromQuery] bool nombre = true,
        [FromQuery] bool direccion = true,
        [FromQuery] bool telefono = true,
        [FromQuery] bool notas = false,
        [FromQuery] bool repartidor = true,
        [FromQuery] bool estado = false,
        [FromQuery] bool ventaMeli = false,
        [FromQuery] bool incluirEntregados = true,
        [FromQuery] DateTime? fecha = null)
    {
        var diaExport = FechaDelMapa(fecha);
        var stops = await _db.MapeoStops.Include(s => s.AssignedDriver).Where(s => s.FechaReparto == diaExport)
            .Where(s => driverId == null || s.AssignedDriverId == driverId)
            .OrderBy(s => s.AssignedDriverId).ThenBy(s => s.OrderInRoute ?? int.MaxValue).ThenBy(s => s.Id)
            .ToListAsync();

        // ¿Cuáles paradas están entregadas? Combinamos las tres señales que ya usa el resto del Mapeo:
        // envío Flex/ME1 confirmado por MeLi, marca del repartidor (InternalStatus) y venta de café con fecha de entrega.
        var refs = stops.Where(s => (s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null)
                        .Select(s => long.TryParse(s.OriginRefId, out var v) ? v : 0L)
                        .Where(v => v != 0L).Distinct().ToList();
        var ships = refs.Count == 0
            ? new Dictionary<long, MeliShipment>()
            : await _db.MeliShipments.Where(m => refs.Contains(m.MeliShipmentId)).ToDictionaryAsync(m => m.MeliShipmentId);
        var ventaRefs = stops.Where(s => s.Origin == "venta_cafe" && s.OriginRefId != null)
                             .Select(s => int.TryParse(s.OriginRefId, out var v) ? v : 0)
                             .Where(v => v != 0).Distinct().ToList();
        var ventasEntrega = ventaRefs.Count == 0
            ? new Dictionary<int, DateTime?>()
            : await _db.CafeVentas.Where(v => ventaRefs.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.EntregadoAt);

        bool IsDelivered(MapeoStop s)
        {
            if (string.Equals(s.InternalStatus, "entregado", StringComparison.OrdinalIgnoreCase)) return true;
            if ((s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null
                && long.TryParse(s.OriginRefId, out var sid) && ships.TryGetValue(sid, out var m)
                && m.Status == "delivered") return true;
            if (s.Origin == "venta_cafe" && s.OriginRefId != null
                && int.TryParse(s.OriginRefId, out var vid) && ventasEntrega.TryGetValue(vid, out var ea) && ea.HasValue) return true;
            return false;
        }

        string NombreDe(MapeoStop s)
        {
            if (!string.IsNullOrWhiteSpace(s.Alias)) return s.Alias!;
            if (!string.IsNullOrWhiteSpace(s.ContactName)) return s.ContactName!;
            if ((s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null
                && long.TryParse(s.OriginRefId, out var sid) && ships.TryGetValue(sid, out var m)
                && !string.IsNullOrWhiteSpace(m.BuyerNickname)) return m.BuyerNickname!;
            return "(sin nombre)";
        }
        string DireccionDe(MapeoStop s)
            => string.IsNullOrWhiteSpace(s.Localidad) ? s.Direccion : $"{s.Direccion}, {s.Localidad}";
        string VentaDe(MapeoStop s)
        {
            if ((s.Origin == "flex" || s.Origin == "me1") && s.OriginRefId != null
                && long.TryParse(s.OriginRefId, out var sid) && ships.TryGetValue(sid, out var m) && m.MeliOrderId.HasValue)
                return "#" + m.MeliOrderId.Value;
            return s.OriginRefId ?? "";
        }

        if (!incluirEntregados)
            stops = stops.Where(s => !IsDelivered(s)).ToList();

        // Agrupar por repartidor, respetando el orden de recorrido. Los "sin asignar" van al final.
        var grupos = stops
            .GroupBy(s => s.AssignedDriverId)
            .Select(g => new
            {
                DriverName = g.First().AssignedDriver?.Nombre ?? "Sin asignar",
                Color = g.First().AssignedDriver?.Color,
                Stops = g.ToList()
            })
            .OrderBy(g => g.DriverName == "Sin asignar" ? 1 : 0)
            .ThenBy(g => g.DriverName)
            .ToList();

        // Columnas elegidas, en un orden fijo y prolijo.
        var colDefs = new List<(string Key, string Header, float Weight)>();
        if (orden) colDefs.Add(("orden", "#", 0.5f));
        if (nombre) colDefs.Add(("nombre", "Cliente", 2.2f));
        if (direccion) colDefs.Add(("direccion", "Dirección", 3f));
        if (telefono) colDefs.Add(("telefono", "Teléfono", 1.4f));
        if (repartidor && driverId == null) colDefs.Add(("repartidor", "Repartidor", 1.4f));
        if (estado) colDefs.Add(("estado", "Estado", 1.2f));
        if (ventaMeli) colDefs.Add(("venta", "Venta", 1.2f));
        if (notas) colDefs.Add(("notas", "Notas", 2.4f));
        if (colDefs.Count == 0) colDefs.Add(("nombre", "Cliente", 2.2f)); // siempre al menos una

        string CellVal(MapeoStop s, string key, int idx) => key switch
        {
            "orden" => (idx + 1).ToString(),
            "nombre" => NombreDe(s),
            "direccion" => DireccionDe(s),
            "telefono" => s.Telefono ?? "",
            "repartidor" => s.AssignedDriver?.Nombre ?? "Sin asignar",
            "estado" => IsDelivered(s) ? "Entregado" : "Pendiente",
            "venta" => VentaDe(s),
            "notas" => s.Notas ?? "",
            _ => ""
        };

        var local = DateTime.UtcNow.AddHours(-3);
        var tituloBase = driverId == null ? "Ruta de reparto" : $"Ruta de {grupos.FirstOrDefault()?.DriverName ?? "reparto"}";
        var subtitulo = $"{local:dd/MM/yyyy HH:mm} · {stops.Count} paradas";

        // ---- Texto plano (para WhatsApp) ----
        if (format == "text")
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"🚚 {tituloBase} — {local:dd/MM/yyyy}");
            sb.AppendLine();
            foreach (var g in grupos)
            {
                if (driverId == null) sb.AppendLine($"👤 {g.DriverName} ({g.Stops.Count})");
                for (int i = 0; i < g.Stops.Count; i++)
                {
                    var s = g.Stops[i];
                    var head = orden ? $"{i + 1}. " : "• ";
                    sb.AppendLine(head + NombreDe(s) + (estado && IsDelivered(s) ? " ✓" : ""));
                    if (direccion) sb.AppendLine("   📍 " + DireccionDe(s));
                    if (telefono && !string.IsNullOrWhiteSpace(s.Telefono)) sb.AppendLine("   📞 " + s.Telefono);
                    if (ventaMeli && !string.IsNullOrWhiteSpace(VentaDe(s))) sb.AppendLine("   🧾 " + VentaDe(s));
                    if (notas && !string.IsNullOrWhiteSpace(s.Notas)) sb.AppendLine("   📝 " + s.Notas);
                }
                sb.AppendLine();
            }
            return Content(sb.ToString().TrimEnd(), "text/plain; charset=utf-8");
        }

        // ---- Excel ----
        if (format == "excel")
        {
            using var wb = new ClosedXML.Excel.XLWorkbook();
            var ws = wb.AddWorksheet("Ruta");
            ws.Cell(1, 1).Value = tituloBase;
            ws.Cell(1, 1).Style.Font.Bold = true;
            ws.Cell(1, 1).Style.Font.FontSize = 13;
            ws.Cell(2, 1).Value = subtitulo;

            int headerRow = 4;
            for (int c = 0; c < colDefs.Count; c++)
            {
                var cell = ws.Cell(headerRow, c + 1);
                cell.Value = colDefs[c].Header;
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;
            }
            int fila = headerRow + 1;
            foreach (var g in grupos)
            {
                if (driverId == null)
                {
                    ws.Cell(fila, 1).Value = $"▼ {g.DriverName} ({g.Stops.Count})";
                    ws.Cell(fila, 1).Style.Font.Bold = true;
                    if (colDefs.Count > 1) ws.Range(fila, 1, fila, colDefs.Count).Merge();
                    fila++;
                }
                for (int i = 0; i < g.Stops.Count; i++)
                {
                    var s = g.Stops[i];
                    for (int c = 0; c < colDefs.Count; c++)
                        ws.Cell(fila, c + 1).Value = CellVal(s, colDefs[c].Key, i);
                    if (estado && IsDelivered(s))
                        ws.Range(fila, 1, fila, colDefs.Count).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.FromArgb(220, 252, 231);
                    fila++;
                }
            }
            ws.Columns().AdjustToContents();
            using var ms = new MemoryStream();
            wb.SaveAs(ms);
            return File(ms.ToArray(),
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                $"ruta-{local:yyyy-MM-dd-HHmm}.xlsx");
        }

        // ---- PDF (por defecto) ----
        var columnas = colDefs.Select(c => new MapeoRutaPdfService.Columna(c.Header, c.Weight)).ToList();
        var pdfGrupos = grupos.Select(g => new MapeoRutaPdfService.Grupo(
            g.DriverName, g.Color,
            g.Stops.Select((s, i) => new MapeoRutaPdfService.Fila(
                colDefs.Select(c => CellVal(s, c.Key, i)).ToArray(),
                IsDelivered(s))).ToList()
        )).ToList();
        var pdf = _rutaPdf.Generar(tituloBase, subtitulo, columnas, pdfGrupos);
        return File(pdf, "application/pdf", $"ruta-{local:yyyy-MM-dd-HHmm}.pdf");
    }
}
