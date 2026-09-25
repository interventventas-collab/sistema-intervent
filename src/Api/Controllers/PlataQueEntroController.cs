using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-09-25 — "Plata que entró": UNA sola lista con toda la plata que llegó al negocio y
/// todavía no está cargada como cobranza. Pedido del dueño: que cheques y transferencias entren
/// como aviso en la bolsita 💰 (igual que los cobros de repartidores) para que no se pase ninguno.
///
/// Junta 4 fuentes que antes había que ir a buscar a 4 pantallas:
///   - cobros de repartidores (ventas y alquileres) esperando aprobación;
///   - transferencias del extracto Galicia sin cobranza;
///   - e-cheqs recibidos que bajó el robot y nadie cargó (también los que el banco ya muestra usados);
///   - cheques cargados a mano en cartera sin cobranza.
/// SOLO LECTURA: cargar la cobranza sigue siendo la pantalla de siempre (la lista solo lleva ahí
/// con todo lleno). "No es cobro" usa el ignorar del extracto que ya existía.
/// </summary>
[ApiController]
[Route("api/plata-que-entro")]
[Authorize]
public class PlataQueEntroController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CafeSaldosService _saldos;

    public PlataQueEntroController(AppDbContext db, CafeSaldosService saldos) { _db = db; _saldos = saldos; }

    /// <summary>Transferencias y cheques más viejos que esto no se muestran: los de antes de usar el
    /// sistema se cobraron por fuera (abril/mayo) y ensuciarían la lista para siempre.</summary>
    private const int DiasVentana = 60;
    /// <summary>Pasados estos días sin volcar, el renglón sale en rojo.</summary>
    private const int DiasAlarma = 3;

    public record PlataItemDto(
        string Key, string Tipo, int Id, DateTime Llego, int Dias, decimal Importe,
        string QueEs, string? DeQuien,
        int? ClienteId, string? ClienteNombre, string? PorQue, decimal? Deuda,
        int? VentaId,
        // Volcados / no eran cobros
        string? CobranzaNumero, string? Quien, DateTime? Cuando, string? Motivo, bool PuedeDeshacer);

    public record ResumenDto(int PorVolcar, bool Alarma);

    private static readonly System.Globalization.CultureInfo EsAr = new("es-AR");
    private static DateTime HoyAr() => DateTime.UtcNow.AddHours(-3).Date;
    private static string SoloDigitos(string? s) => new((s ?? "").Where(char.IsDigit).ToArray());

    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen()
    {
        var items = await PorVolcarAsync(conSugerencia: false);
        return Ok(new ResumenDto(items.Count, items.Any(i => i.Dias > DiasAlarma)));
    }

    /// <summary>tab = por-volcar | volcados | no-eran</summary>
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] string tab = "por-volcar")
    {
        var items = tab switch
        {
            "volcados" => await VolcadosAsync(),
            "no-eran" => await NoEranAsync(),
            _ => await PorVolcarAsync(conSugerencia: true),
        };
        return Ok(items);
    }

    // ─────────────────────────────── Por volcar ───────────────────────────────
    private async Task<List<PlataItemDto>> PorVolcarAsync(bool conSugerencia)
    {
        var hoy = HoyAr();
        var desde = hoy.AddDays(-DiasVentana);
        var items = new List<PlataItemDto>();

        // 1. Cobros de repartidores (ventas)
        var rep = await _db.CafeCobranzasPendientes.AsNoTracking()
            .Where(p => p.Estado == "PENDIENTE")
            .Select(p => new { p.Id, p.Importe, p.CreatedAt, p.VentaId, VentaNumero = p.Venta!.Numero,
                p.Venta.ClienteId, Cliente = p.Venta.ClienteNav!.Nombre, Repartidor = p.Repartidor!.Nombre })
            .ToListAsync();
        items.AddRange(rep.Select(p => new PlataItemDto(
            $"R-{p.Id}", "REPARTIDOR", p.Id, p.CreatedAt, Dias(p.CreatedAt, hoy), p.Importe,
            $"Efectivo · repartidor {p.Repartidor}", p.Cliente,
            p.ClienteId, p.Cliente, $"venta {p.VentaNumero}", null, p.VentaId,
            null, null, null, null, false)));

        // 2. Cobros de repartidores (alquileres)
        var alq = await _db.AlqCobranzasPendientes.AsNoTracking()
            .Where(p => p.Estado == "PENDIENTE")
            .Select(p => new { p.Id, p.Importe, p.CreatedAt, ReservaNumero = p.Reserva!.Numero,
                p.Reserva.ClienteId, Cliente = p.Reserva.ClienteNav!.Nombre, Repartidor = p.Repartidor!.Nombre })
            .ToListAsync();
        items.AddRange(alq.Select(p => new PlataItemDto(
            $"A-{p.Id}", "ALQ_REPARTIDOR", p.Id, p.CreatedAt, Dias(p.CreatedAt, hoy), p.Importe,
            $"Efectivo · repartidor {p.Repartidor} · alquiler", p.Cliente,
            p.ClienteId, p.Cliente, $"reserva {p.ReservaNumero}", null, null,
            null, null, null, null, false)));

        // 3. Transferencias del banco sin cobranza. Las de Palanica a Palanica (cuentas propias)
        //    no son cobros: se apartan solas y aparecen en "No eran cobros".
        var movs = await _db.CafeExtractoMovimientos.AsNoTracking()
            .Where(m => m.Creditos > 0 && m.CobranzaUsadaId == null && m.IgnoradoAt == null && m.Fecha >= desde
                        && m.LeyendaAdicional2 != ChequesBancoImportService.CuitPalanica)
            .Select(m => new { m.Id, m.Fecha, m.Creditos, m.Descripcion, m.LeyendaAdicional1, m.LeyendaAdicional2, m.VentaIdAsociada })
            .ToListAsync();

        // 4. E-cheqs recibidos que nadie cargó. Disponibles, o que el banco ya muestra usados
        //    (se cobraron o endosaron el mismo día que llegaron: caso QX del 25/09).
        var echeqs = await _db.CafeChequesBanco.AsNoTracking()
            .Where(b => b.Tipo != "EMITIDO" && b.CobranzaId == null && b.CafeChequeId == null
                        && (b.Estado == "Disponible" || (b.Estado == "Pagado" && b.FechaPago >= desde)))
            .Select(b => new { b.Id, b.Numero, b.Importe, b.FechaPago, b.Estado, b.LibradorNombre, b.LibradorCuit, b.CreatedAt, b.BancoEmisor })
            .ToListAsync();

        // 5. Cheques cargados a mano en cartera que todavía no son cobranza de nadie.
        var cartera = await _db.CafeCheques.AsNoTracking()
            .Where(c => c.Estado == "EN_CARTERA" && c.CobranzaOrigenId == null)
            .Select(c => new { c.Id, c.Numero, c.Importe, c.FechaCobro, c.FechaVencimiento, c.Emisor, c.ClienteOrigenId,
                Cliente = c.ClienteOrigen!.Nombre, c.CreatedAt, c.Banco })
            .ToListAsync();

        // Sugerencia de cliente: por CUIT, y si no, por una factura impaga del mismo importe.
        Dictionary<string, List<(int Id, string Nombre)>> porCuit = new();
        List<(int ClienteId, string Nombre, string Numero, decimal Importe)> impagas = new();
        if (conSugerencia && (movs.Count > 0 || echeqs.Count > 0))
        {
            var cl = await _db.CafeClientes.AsNoTracking().Where(c => c.IsActive && c.Cuit != null && c.Cuit != "")
                .Select(c => new { c.Id, c.Nombre, c.Cuit }).ToListAsync();
            porCuit = cl.GroupBy(c => SoloDigitos(c.Cuit)).Where(g => g.Key.Length >= 10)
                .ToDictionary(g => g.Key, g => g.Select(x => (x.Id, x.Nombre)).ToList());
            var imp = await _db.CafeVentas.AsNoTracking()
                .Where(v => !v.IsPaid && v.ClienteId != null && v.Estado != "anulado" && (v.TipoComprobante == null || !v.TipoComprobante.StartsWith("NC")))
                .Select(v => new { ClienteId = v.ClienteId!.Value, Nombre = v.ClienteNav!.Nombre, v.Numero,
                    Importe = v.ArcaImpTotal != null && v.ArcaImpTotal > 0 ? v.ArcaImpTotal.Value : v.Total })
                .ToListAsync();
            impagas = imp.Select(v => (v.ClienteId, v.Nombre, v.Numero, v.Importe)).ToList();
        }

        (int? id, string? nombre, string? porQue) Sugerir(string? cuit, decimal importe, int? ventaAsociada)
        {
            if (!conSugerencia) return (null, null, null);
            var d = SoloDigitos(cuit);
            if (d.Length >= 10 && porCuit.TryGetValue(d, out var cs))
                return (cs[0].Id, cs[0].Nombre, cs.Count == 1 ? "mismo CUIT" : $"mismo CUIT · hay {cs.Count} clientes con ese CUIT");
            var mismas = impagas.Where(v => Math.Abs(v.Importe - importe) < 0.01m).ToList();
            if (mismas.Select(v => v.ClienteId).Distinct().Count() == 1)
                return (mismas[0].ClienteId, mismas[0].Nombre, $"por el importe: la factura {mismas[0].Numero} es de ${importe.ToString("N2", EsAr)}");
            return (null, null, null);
        }

        foreach (var m in movs)
        {
            var s = Sugerir(m.LeyendaAdicional2, m.Creditos, m.VentaIdAsociada);
            int? ventaId = m.VentaIdAsociada;
            if (ventaId.HasValue && conSugerencia)
            {
                var v = await _db.CafeVentas.AsNoTracking().Where(x => x.Id == ventaId.Value)
                    .Select(x => new { x.ClienteId, Nombre = x.ClienteNav!.Nombre, x.Numero }).FirstOrDefaultAsync();
                if (v?.ClienteId is not null) s = (v.ClienteId, v.Nombre, $"ya asociada a la venta {v.Numero}");
            }
            items.Add(new PlataItemDto(
                $"T-{m.Id}", "TRANSFERENCIA", m.Id, m.Fecha, Dias(m.Fecha, hoy), m.Creditos,
                "Transferencia", m.LeyendaAdicional1 ?? m.Descripcion,
                s.id, s.nombre, s.porQue, null, ventaId, null, null, null, null, false));
        }
        foreach (var b in echeqs)
        {
            var s = Sugerir(b.LibradorCuit, b.Importe, null);
            var usado = b.Estado == "Pagado";
            items.Add(new PlataItemDto(
                $"B-{b.Id}", "ECHEQ", b.Id, b.CreatedAt, Dias(b.CreatedAt, hoy), b.Importe,
                $"E-cheq N° {b.Numero}" + (b.FechaPago.HasValue ? $" · vence {b.FechaPago:dd/MM}" : "") + (usado ? " · el banco ya lo muestra usado" : ""),
                b.LibradorNombre, s.id, s.nombre, s.porQue, null, null, null, null, null, null, false));
        }
        foreach (var c in cartera)
        {
            items.Add(new PlataItemDto(
                $"C-{c.Id}", "CHEQUE", c.Id, c.CreatedAt, Dias(c.CreatedAt, hoy), c.Importe,
                $"Cheque N° {c.Numero}" + ((c.FechaVencimiento ?? c.FechaCobro) is DateTime f ? $" · vence {f:dd/MM}" : "") + " · cargado a mano",
                c.Emisor, c.ClienteOrigenId, c.Cliente, c.ClienteOrigenId.HasValue ? "cargado a su nombre" : null,
                null, null, null, null, null, null, false));
        }

        // Cuánto debe cada cliente sugerido (la misma cuenta que "¿Quién me debe?").
        if (conSugerencia)
        {
            var deudas = new Dictionary<int, decimal>();
            foreach (var cid in items.Where(i => i.ClienteId.HasValue).Select(i => i.ClienteId!.Value).Distinct())
            {
                try { deudas[cid] = await _saldos.GetSaldoClienteAsync(cid); } catch { /* si falla, sin deuda */ }
            }
            items = items.Select(i => i.ClienteId is int id && deudas.TryGetValue(id, out var d) ? i with { Deuda = d } : i).ToList();
        }

        return items.OrderBy(i => i.Llego).ToList();
    }

    // ─────────────────────────────── Volcados (últimos 7 días) ───────────────────────────────
    private async Task<List<PlataItemDto>> VolcadosAsync()
    {
        var desde = DateTime.UtcNow.AddDays(-7);
        var items = new List<PlataItemDto>();

        var rep = await _db.CafeCobranzasPendientes.AsNoTracking()
            .Where(p => p.Estado == "APROBADA" && p.RevisadaAt >= desde)
            .Select(p => new { p.Id, p.Importe, p.CreatedAt, p.RevisadaAt, p.RevisadaPor, p.CobranzaCreadaId,
                Cliente = p.Venta!.ClienteNav!.Nombre, Repartidor = p.Repartidor!.Nombre })
            .ToListAsync();
        var alq = await _db.AlqCobranzasPendientes.AsNoTracking()
            .Where(p => p.Estado == "APROBADA" && p.RevisadaAt >= desde)
            .Select(p => new { p.Id, p.Importe, p.CreatedAt, p.RevisadaAt, p.RevisadaPor, p.CobranzaCreadaId,
                Cliente = p.Reserva!.ClienteNav!.Nombre, Repartidor = p.Repartidor!.Nombre })
            .ToListAsync();
        var movs = await (from m in _db.CafeExtractoMovimientos.AsNoTracking()
                          join c in _db.CafeCobranzas on m.CobranzaUsadaId equals c.Id
                          where m.Creditos > 0 && c.CreatedAt >= desde
                          select new { m.Id, m.Fecha, m.Creditos, m.LeyendaAdicional1, CobId = c.Id, c.Numero, c.CreatedAt, c.Operador, Cliente = c.Cliente!.Nombre })
                         .ToListAsync();
        var echeqs = await (from b in _db.CafeChequesBanco.AsNoTracking()
                            join c in _db.CafeCobranzas on b.CobranzaId equals c.Id
                            where c.CreatedAt >= desde
                            select new { b.Id, ChequeNumero = b.Numero, b.Importe, b.CreatedAt, CobId = c.Id, CobNumero = c.Numero, CobCreated = c.CreatedAt, c.Operador, Cliente = c.Cliente!.Nombre })
                           .ToListAsync();

        // Quién creó cada cobranza: el operador queda en el registro de cambios.
        var cobIds = rep.Where(x => x.CobranzaCreadaId.HasValue).Select(x => x.CobranzaCreadaId!.Value)
            .Concat(alq.Where(x => x.CobranzaCreadaId.HasValue).Select(x => x.CobranzaCreadaId!.Value))
            .Concat(movs.Select(x => x.CobId)).Concat(echeqs.Select(x => x.CobId)).Distinct().ToList();
        var cobIdsStr = cobIds.Select(i => i.ToString()).ToList();
        var autores = (await _db.AuditLogs.AsNoTracking()
                .Where(a => a.EntityType == "CafeCobranza" && a.Action == "CREATE" && cobIdsStr.Contains(a.EntityId))
                .Select(a => new { a.EntityId, a.UserName }).ToListAsync())
            .GroupBy(a => a.EntityId).ToDictionary(g => g.Key, g => g.First().UserName);
        var numeros = await _db.CafeCobranzas.AsNoTracking().Where(c => cobIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.Numero);
        string? Autor(int cobId, string? operador) =>
            autores.TryGetValue(cobId.ToString(), out var u) && !string.IsNullOrWhiteSpace(u) ? u : operador;

        items.AddRange(rep.Select(p => new PlataItemDto($"R-{p.Id}", "REPARTIDOR", p.Id, p.CreatedAt, 0, p.Importe,
            $"Efectivo · repartidor {p.Repartidor}", p.Cliente, null, p.Cliente, null, null, null,
            p.CobranzaCreadaId is int ci && numeros.TryGetValue(ci, out var n) ? n : null,
            p.RevisadaPor, p.RevisadaAt, null, false)));
        items.AddRange(alq.Select(p => new PlataItemDto($"A-{p.Id}", "ALQ_REPARTIDOR", p.Id, p.CreatedAt, 0, p.Importe,
            $"Efectivo · repartidor {p.Repartidor} · alquiler", p.Cliente, null, p.Cliente, null, null, null,
            p.CobranzaCreadaId is int ci && numeros.TryGetValue(ci, out var n) ? n : null,
            p.RevisadaPor, p.RevisadaAt, null, false)));
        items.AddRange(movs.Select(m => new PlataItemDto($"T-{m.Id}", "TRANSFERENCIA", m.Id, m.Fecha, 0, m.Creditos,
            "Transferencia", m.LeyendaAdicional1, null, m.Cliente, null, null, null,
            m.Numero, Autor(m.CobId, m.Operador), m.CreatedAt, null, false)));
        items.AddRange(echeqs.Select(b => new PlataItemDto($"B-{b.Id}", "ECHEQ", b.Id, b.CreatedAt, 0, b.Importe,
            $"E-cheq N° {b.ChequeNumero}", null, null, b.Cliente, null, null, null,
            b.CobNumero, Autor(b.CobId, b.Operador), b.CobCreated, null, false)));

        return items.OrderByDescending(i => i.Cuando).ToList();
    }

    // ─────────────────────────────── No eran cobros (últimos 60 días) ───────────────────────────────
    private async Task<List<PlataItemDto>> NoEranAsync()
    {
        var hoy = HoyAr();
        var desde = hoy.AddDays(-DiasVentana);
        var movs = await _db.CafeExtractoMovimientos.AsNoTracking()
            .Where(m => m.Creditos > 0 && m.CobranzaUsadaId == null && m.Fecha >= desde
                        && (m.IgnoradoAt != null || m.LeyendaAdicional2 == ChequesBancoImportService.CuitPalanica))
            .Select(m => new { m.Id, m.Fecha, m.Creditos, m.LeyendaAdicional1, m.LeyendaAdicional2, m.IgnoradoAt, m.IgnoradoPor, m.IgnoradoMotivo })
            .ToListAsync();
        return movs.Select(m =>
        {
            var propia = m.IgnoradoAt == null;
            return new PlataItemDto($"T-{m.Id}", "TRANSFERENCIA", m.Id, m.Fecha, 0, m.Creditos,
                "Transferencia", m.LeyendaAdicional1, null, null, null, null, null, null,
                propia ? "el sistema" : m.IgnoradoPor, propia ? m.Fecha : m.IgnoradoAt,
                propia ? "entre cuentas propias (se aparta sola)" : (m.IgnoradoMotivo ?? "no es cobro"),
                PuedeDeshacer: !propia);
        }).OrderByDescending(i => i.Cuando).ToList();
    }

    private static int Dias(DateTime llego, DateTime hoyAr)
    {
        // El extracto trae la fecha sola (00:00, ya es dia argentino); el resto viene en UTC.
        var dia = llego.TimeOfDay == TimeSpan.Zero ? llego.Date : llego.AddHours(-3).Date;
        return Math.Max(0, (int)(hoyAr - dia).TotalDays);
    }
}
