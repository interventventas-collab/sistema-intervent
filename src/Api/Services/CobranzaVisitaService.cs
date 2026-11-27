using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 27/11/2026: "MANDAR A COBRAR". Le asigna a un repartidor la cobranza de un cliente que debe,
/// para un día, y le imprime el recibo de visita por cobranza.
///
/// No hay tabla nueva: la cobranza vive en el mapa como una parada más (MapeoStops) con
/// Origin = "cobranza". Así se ve en el mapa del día, se asigna/reasigna con los choferes como
/// cualquier parada y le llega sola al celu del repartidor (Mis pedidos lee las paradas de hoy).
///
/// OriginRefId:
///   "{clienteId}"            → cobrar TODO lo que debe el cliente.
///   "{clienteId}:{ventaId}"  → cobrar solo esa venta (clienteId = 0 si la venta no tiene cliente).
///
/// Estados de la parada (InternalStatus): pending = mandada · entregado = el repartidor cargó el
/// cobro · no_encontrado = fue y no pagó (con el motivo adelante en Notas).
/// </summary>
public class CobranzaVisitaService
{
    public const string Origin = "cobranza";

    private readonly AppDbContext _db;
    private readonly CafeSaldosService _saldos;
    private readonly GoogleMapsLinkResolverService _maps;

    public CobranzaVisitaService(AppDbContext db, CafeSaldosService saldos, GoogleMapsLinkResolverService maps)
    {
        _db = db; _saldos = saldos; _maps = maps;
    }

    private static DateTime HoyAr() => DateTime.UtcNow.AddHours(-3).Date;

    public static (int clienteId, int? ventaId) ParseRef(string? r)
    {
        if (string.IsNullOrWhiteSpace(r)) return (0, null);
        var partes = r.Split(':');
        int.TryParse(partes[0], out var c);
        int? v = partes.Length > 1 && int.TryParse(partes[1], out var vv) ? vv : null;
        return (c, v);
    }

    public static string ArmarRef(int clienteId, int? ventaId)
        => ventaId.HasValue ? $"{clienteId}:{ventaId.Value}" : clienteId.ToString();

    /// <summary>¿La parada sigue abierta (mandada y sin resolver)?</summary>
    public static bool Abierta(MapeoStop s)
        => !string.Equals(s.InternalStatus, "entregado", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(s.InternalStatus, "no_encontrado", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(s.InternalStatus, "cancelado", StringComparison.OrdinalIgnoreCase);

    public record ComprobanteDto(int VentaId, string Numero, DateTime Fecha, decimal Total, decimal Saldo, int Dias);

    /// <summary>Comprobantes que se le van a cobrar, del más viejo al más nuevo. Si viene ventaId
    /// (y no "todas"), solo esa venta.</summary>
    public async Task<List<ComprobanteDto>> ComprobantesAsync(int clienteId, int? ventaId)
    {
        List<CafeSaldosService.VentaCuenta> cuenta;
        if (clienteId > 0) cuenta = await _saldos.GetVentasCuentaAsync(clienteId);
        else if (ventaId.HasValue) cuenta = (await _saldos.GetVentasCuentaAsync(soloSinCliente: true)).Where(v => v.Id == ventaId.Value).ToList();
        else return new();
        var hoy = HoyAr();
        // Las reservas de alquiler vienen con Id negativo: esas se cobran por su lado (Alquileres).
        return cuenta.Where(v => v.Id > 0 && v.Pendiente && (!ventaId.HasValue || v.Id == ventaId.Value))
            .OrderBy(v => v.Fecha).ThenBy(v => v.Id)
            .Select(v => new ComprobanteDto(v.Id, v.Numero, v.Fecha, v.Cobrable, v.Saldo, Math.Max(0, (hoy - v.Fecha.Date).Days)))
            .ToList();
    }

    /// <summary>Lo que debe de ALQUILERES (no entra en "Mandar a cobrar": se cobra desde la reserva).</summary>
    public async Task<decimal> SaldoAlquileresAsync(int clienteId)
        => clienteId <= 0 ? 0m : (await _saldos.GetVentasCuentaAsync(clienteId)).Where(v => v.Id < 0 && v.Pendiente).Sum(v => v.Saldo);

    /// <summary>Lo que hay que cobrar. Si es todo lo del cliente, se descuentan los pagos a cuenta y
    /// notas de crédito (misma cuenta que "¿Quién me debe?").</summary>
    public async Task<decimal> SaldoAsync(int clienteId, int? ventaId, List<ComprobanteDto> comps)
    {
        var suma = comps.Sum(c => c.Saldo);
        if (ventaId.HasValue || clienteId <= 0) return suma;
        var neto = await _saldos.GetSaldoClienteAsync(clienteId);
        return neto > 0 && neto < suma ? neto : suma;
    }

    public record AbiertaDto(int StopId, int ClienteId, int? VentaId, DateTime Fecha, int? RepartidorId, string? RepartidorNombre, string? Nota);

    /// <summary>Cobranzas mandadas y todavía sin resolver (de hoy en adelante, y las de la última
    /// semana que quedaron sin hacer).</summary>
    public async Task<List<AbiertaDto>> AbiertasAsync()
    {
        var desde = HoyAr().AddDays(-7);
        var stops = await _db.MapeoStops.AsNoTracking().Include(s => s.AssignedDriver)
            .Where(s => s.Origin == Origin && s.FechaReparto >= desde)
            .OrderByDescending(s => s.FechaReparto).ThenByDescending(s => s.Id)
            .ToListAsync();
        return stops.Where(Abierta).Select(s =>
        {
            var (c, v) = ParseRef(s.OriginRefId);
            return new AbiertaDto(s.Id, c, v, s.FechaReparto, s.AssignedDriver?.CafeRepartidorId,
                s.AssignedDriver?.Nombre, s.Notas);
        }).ToList();
    }

    public record MandarRequest(int? ClienteId, int? VentaId, bool Todas, int? RepartidorId, DateTime? Fecha, string? Nota);
    public record MandarResult(bool Ok, string Mensaje, int? StopId = null);

    public async Task<MandarResult> MandarAsync(MandarRequest req)
    {
        int clienteId = req.ClienteId ?? 0;
        CafeVenta? venta = null;
        if (req.VentaId.HasValue)
        {
            venta = await _db.CafeVentas.AsNoTracking().FirstOrDefaultAsync(v => v.Id == req.VentaId.Value);
            if (venta is null) return new(false, "No encontré la venta");
            if (clienteId <= 0) clienteId = venta.ClienteId ?? 0;
        }
        if (clienteId <= 0 && venta is null) return new(false, "Falta el cliente");
        int? soloVenta = (req.Todas && clienteId > 0) ? null : req.VentaId;

        var comps = await ComprobantesAsync(clienteId, soloVenta);
        if (comps.Count == 0) return new(false, "No debe nada: no hay qué cobrar");

        var cli = clienteId > 0 ? await _db.CafeClientes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clienteId) : null;
        var nombre = cli?.Nombre ?? venta?.ClienteNombreSnapshot ?? "Cliente";
        var direccion = FirstNonEmpty(cli?.DomicilioEntrega, cli?.Direccion, venta?.ClienteDomicilioEntregaSnapshot, venta?.ClienteDireccionSnapshot);
        var localidad = FirstNonEmpty(cli?.LocalidadEntrega, cli?.Localidad, venta?.ClienteLocalidadSnapshot);
        var telefono = FirstNonEmpty(cli?.Telefono, venta?.ClienteTelefonoSnapshot);

        var dia = (req.Fecha ?? HoyAr()).Date;
        if (dia < HoyAr()) return new(false, "Elegí hoy o un día que viene");

        int? driverId = null;
        string? repNombre = null;
        if (req.RepartidorId is int repId && repId > 0)
        {
            var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(r => r.Id == repId && r.IsActive);
            if (rep is null) return new(false, "Ese repartidor ya no está activo");
            repNombre = rep.Nombre;
            var drv = await _db.MapeoDrivers.FirstOrDefaultAsync(d => d.CafeRepartidorId == rep.Id);
            if (drv is null)
            {
                // Espejo del repartidor en el mapa (lo mismo que hace la lista de choferes del mapa).
                drv = new MapeoDriver { Nombre = rep.Nombre, CafeRepartidorId = rep.Id, IsActive = true, CreatedAt = DateTime.UtcNow };
                _db.MapeoDrivers.Add(drv);
                await _db.SaveChangesAsync();
            }
            driverId = drv.Id;
        }

        var nota = string.IsNullOrWhiteSpace(req.Nota) ? null : req.Nota.Trim();
        if (nota is { Length: > 300 }) nota = nota[..300];
        var refId = ArmarRef(clienteId, soloVenta);

        // Si ya había una mandada y sin resolver para este cliente, se MUEVE (otro día / otro
        // repartidor / otra nota) en vez de duplicarla.
        var desde = HoyAr().AddDays(-7);
        var candidatas = await _db.MapeoStops
            .Where(s => s.Origin == Origin && s.FechaReparto >= desde && s.OriginRefId != null
                     && (s.OriginRefId == clienteId.ToString() || s.OriginRefId.StartsWith(clienteId + ":")))
            .ToListAsync();
        if (clienteId == 0) candidatas = candidatas.Where(s => s.OriginRefId == refId).ToList();
        var stop = candidatas.Where(Abierta).OrderByDescending(s => s.Id).FirstOrDefault();

        decimal lat = 0m, lng = 0m;
        if (cli?.MapeoLat is decimal la && cli.MapeoLng is decimal ln) { lat = la; lng = ln; }
        else if (stop is not null && (stop.Latitude != 0 || stop.Longitude != 0)) { lat = stop.Latitude; lng = stop.Longitude; }
        else if (!string.IsNullOrWhiteSpace(direccion))
        {
            var g = await _maps.TryGeocodeAddressAsync(direccion + (string.IsNullOrWhiteSpace(localidad) ? "" : ", " + localidad) + ", Argentina");
            if (g.HasValue) { lat = g.Value.lat; lng = g.Value.lng; }
        }

        bool nueva = stop is null;
        if (stop is null)
        {
            stop = new MapeoStop { Origin = Origin, CreatedAt = DateTime.UtcNow, InternalStatus = "pending" };
            _db.MapeoStops.Add(stop);
        }
        else if (stop.FechaReparto != dia || stop.AssignedDriverId != driverId)
        {
            // Cambió de día o de repartidor: sale de la ruta que tenía.
            stop.OrderInRoute = null;
            stop.AssignedVehicleSlot = null;
        }
        stop.OriginRefId = refId;
        stop.Alias = Cortar(nombre, 100);
        stop.ContactName = Cortar(nombre, 150);
        stop.Direccion = Cortar(string.IsNullOrWhiteSpace(direccion) ? nombre : direccion!, 300)!;
        stop.Localidad = Cortar(localidad, 150);
        stop.Telefono = Cortar(telefono, 50);
        stop.Latitude = lat; stop.Longitude = lng;
        stop.Notas = nota;
        stop.FechaReparto = dia;
        stop.AssignedDriverId = driverId;
        stop.InternalStatus = "pending";
        stop.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var cuando = dia == HoyAr() ? "hoy" : dia == HoyAr().AddDays(1) ? "mañana" : dia.ToString("dd/MM");
        var msg = repNombre is null
            ? $"En el mapa de {cuando}, sin repartidor todavía"
            : $"Se la mandé a {repNombre} para {cuando}";
        if (!nueva) msg = "Actualizada: " + msg.ToLowerInvariant();
        if (lat == 0 && lng == 0) msg += " · sin ubicación, buscala en el mapa";
        return new(true, msg, stop.Id);
    }

    public async Task<bool> CancelarAsync(int stopId)
    {
        var s = await _db.MapeoStops.FirstOrDefaultAsync(x => x.Id == stopId && x.Origin == Origin);
        if (s is null || !Abierta(s)) return false;
        _db.MapeoStops.Remove(s);
        await _db.SaveChangesAsync();
        return true;
    }

    public record DeudorDto(int ClienteId, string Nombre, string? Direccion, decimal Saldo, int Cantidad,
        DateTime FechaMasAntigua, int Dias, int? StopIdAbierta, DateTime? FechaAbierta, string? RepartidorAbierta,
        bool EnEsteDia, string? UltimoNoPago);

    /// <summary>Para la pestaña "Deben" del mapa: los que deben, el más atrasado arriba.</summary>
    public async Task<List<DeudorDto>> DeudoresAsync(DateTime dia)
    {
        // Solo deuda de VENTAS (los alquileres se cobran desde la reserva). Mismo saldo que la ventanita.
        var hoy = HoyAr();
        var porCliente = (await _saldos.GetVentasCuentaAsync())
            .Where(v => v.Id > 0 && v.Pendiente && v.ClienteId.HasValue)
            .GroupBy(v => v.ClienteId!.Value)
            .ToDictionary(g => g.Key, g => (suma: g.Sum(v => v.Saldo), n: g.Count(), fecha: g.Min(v => v.Fecha)));
        var saldos = (await _saldos.GetSaldosPendientesAsync())
            .Where(x => x.SaldoPendiente > CafeSaldosService.Umbral && porCliente.ContainsKey(x.ClienteId)).ToList();
        var ids = saldos.Select(x => x.ClienteId).ToList();
        var clis = await _db.CafeClientes.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.DomicilioEntrega, c.Direccion, c.LocalidadEntrega, c.Localidad })
            .ToDictionaryAsync(c => c.Id);
        var abiertas = (await AbiertasAsync()).GroupBy(a => a.ClienteId).ToDictionary(g => g.Key, g => g.First());
        var desde = HoyAr().AddDays(-30);
        var noPagos = (await _db.MapeoStops.AsNoTracking()
                .Where(s => s.Origin == Origin && s.InternalStatus == "no_encontrado" && s.FechaReparto >= desde)
                .OrderByDescending(s => s.UpdatedAt).ToListAsync())
            .GroupBy(s => ParseRef(s.OriginRefId).clienteId)
            .ToDictionary(g => g.Key, g => g.First());
        return saldos.Select(x =>
        {
            clis.TryGetValue(x.ClienteId, out var c);
            var dir = c is null ? null : string.Join(", ", new[] { FirstNonEmpty(c.DomicilioEntrega, c.Direccion), FirstNonEmpty(c.LocalidadEntrega, c.Localidad) }.Where(z => !string.IsNullOrWhiteSpace(z)));
            abiertas.TryGetValue(x.ClienteId, out var a);
            string? noPago = null;
            if (noPagos.TryGetValue(x.ClienteId, out var np) && (a is null || np.UpdatedAt > DateTime.UtcNow.AddDays(-30)))
            {
                var t = np.Notas ?? "";
                const string pre = "⚠ No se pudo entregar: ";
                if (t.StartsWith(pre)) { t = t[pre.Length..]; var i = t.IndexOf(" · ", StringComparison.Ordinal); if (i > 0) t = t[..i]; }
                noPago = $"no pagó {np.FechaReparto:dd/MM}: {t}";
            }
            var pv = porCliente[x.ClienteId];
            var saldo = Math.Min(pv.suma, x.SaldoPendiente);
            return new DeudorDto(x.ClienteId, x.Nombre, string.IsNullOrWhiteSpace(dir) ? null : dir, saldo,
                pv.n, pv.fecha, Math.Max(0, (hoy - pv.fecha.Date).Days),
                a?.StopId, a?.Fecha, a?.RepartidorNombre, a is not null && a.Fecha == dia.Date, a is null ? noPago : null);
        }).OrderByDescending(d => d.Dias).ThenByDescending(d => d.Saldo).ToList();
    }

    /// <summary>Arma el recibo. Si hay una cobranza mandada para ese cliente, lleva el repartidor, el
    /// día y la nota.</summary>
    public async Task<(CafeReciboVisitaCobranzaPdfService.ReciboCobranzaDatos? datos, CafeVenta? ventaQr)> DatosReciboAsync(int? stopId, int? clienteId, int? ventaId, bool todas)
    {
        MapeoStop? stop = null;
        if (stopId.HasValue)
        {
            stop = await _db.MapeoStops.AsNoTracking().Include(s => s.AssignedDriver).FirstOrDefaultAsync(s => s.Id == stopId.Value && s.Origin == Origin);
            if (stop is null) return (null, null);
            var (c, v) = ParseRef(stop.OriginRefId);
            clienteId = c; ventaId = v; todas = !v.HasValue;
        }
        CafeVenta? venta = ventaId.HasValue ? await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == ventaId.Value) : null;
        int cid = clienteId ?? 0;
        if (cid <= 0 && venta?.ClienteId is int vc) cid = vc;
        int? soloVenta = (todas && cid > 0) ? null : ventaId;
        var comps = await ComprobantesAsync(cid, soloVenta);
        if (comps.Count == 0) return (null, null);

        if (stop is null)
        {
            // ¿Hay una mandada para este cliente? Así el recibo dice a quién y qué día.
            var ab = (await AbiertasAsync()).FirstOrDefault(a => a.ClienteId == cid && (cid > 0 || a.VentaId == ventaId));
            if (ab is not null)
                stop = await _db.MapeoStops.AsNoTracking().Include(s => s.AssignedDriver).FirstOrDefaultAsync(s => s.Id == ab.StopId);
        }

        var cli = cid > 0 ? await _db.CafeClientes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid) : null;
        var ventaQr = venta ?? await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == comps[0].VentaId);
        var d = ventaQr is not null && cli is null
            ? CafeReciboVisitaCobranzaPdfService.DatosDeVenta(ventaQr, comps.Sum(x => x.Saldo))
            : new CafeReciboVisitaCobranzaPdfService.ReciboCobranzaDatos
            {
                ClienteNombre = cli?.Nombre ?? ventaQr?.ClienteNombreSnapshot ?? "(sin cliente)",
                RazonSocial = cli?.RazonSocial,
                Cuit = cli?.Cuit,
                Direccion = FirstNonEmpty(cli?.DomicilioEntrega, cli?.Direccion),
                LocalidadLinea = FirstNonEmpty(cli?.LocalidadEntrega, cli?.Localidad),
                Telefono = cli?.Telefono,
            };
        d.Comprobantes = comps.Select(x => new CafeReciboVisitaCobranzaPdfService.ReciboCobranzaItem(x.Numero, x.Fecha, x.Total, x.Saldo)).ToList();
        d.SaldoTotal = await SaldoAsync(cid, soloVenta, comps);
        d.NumeroRecibo = stop is not null ? $"COB-{stop.FechaReparto:yyyy}-{stop.Id:00000}" : $"VIS-{DateTime.Now.Year:0000}-{comps[0].VentaId:0000}";
        if (stop is not null)
        {
            d.Repartidor = stop.AssignedDriver?.Nombre;
            d.FechaVisita = stop.FechaReparto;
            d.Nota = SinMarcaNoPago(stop.Notas);
        }
        return (d, ventaQr);
    }

    public static string? SinMarcaNoPago(string? notas)
    {
        if (string.IsNullOrWhiteSpace(notas)) return null;
        if (!notas.StartsWith("⚠ No se pudo entregar:", StringComparison.Ordinal)) return notas;
        var i = notas.IndexOf(" · ", StringComparison.Ordinal);
        return i < 0 ? null : notas[(i + 3)..].Trim();
    }

    private static string? FirstNonEmpty(params string?[] xs) => xs.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
    private static string? Cortar(string? s, int max) => s is null ? null : (s.Length > max ? s[..max] : s);
}
