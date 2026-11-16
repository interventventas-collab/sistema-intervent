using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-16: pantalla Sueldos centralizada pero independiente. Pedido del dueño: "centrar todo acá,
/// pero independiente... que abajo de cada empleado aparezcan todos los conceptos y poder entrar a
/// cada uno" + "que diga siempre la fecha que hay que pagarle: mañana, este sábado, tal plata".
///  - GET sueldos-mes: por empleado, cada concepto (sueldo, horas extras, comisiones, feriados, plus,
///    aguinaldo) con cargado / pagado / debe y cuándo se paga.
///  - Fechas de pago del SUELDO (Nom_SueldoFechas): el sueldo se paga en partes; lo pagado se reparte
///    por orden de fecha para saber qué parte falta de cada una.
///  - GET proximos-pagos: lo atrasado + lo de los próximos N días (sueldo y extras), para el inicio.
/// </summary>
[ApiController]
[Route("api/nominas")]
[Authorize]
public class NomSueldosMesController : ControllerBase
{
    private readonly AppDbContext _db;
    public NomSueldosMesController(AppDbContext db) { _db = db; }

    private static DateTime HoyAr() => DateTime.UtcNow.AddHours(-3).Date;
    private static readonly System.Globalization.CultureInfo EsAr = new("es-AR");

    public record SueldoMesConceptoDto(string Tipo, decimal Cargado, decimal Pagado, decimal Debe, string? Info,
        DateTime? ProximaFecha, decimal ProximaMonto, DateTime? UltimoPago, bool SinFecha);
    public record SueldoMesEmpleadoDto(int EmpleadoId, string Nombre, string? Puesto, int? LiquidacionId, List<SueldoMesConceptoDto> Conceptos);
    public record SueldoFechaDto(int Id, int LiquidacionId, DateTime Fecha, decimal Monto, decimal Falta);
    public record ProximoPagoDto(int EmpleadoId, string Nombre, string Tipo, DateTime Fecha, decimal Monto, int Anio, int Mes, int LiquidacionId);
    public class SueldoFechaRequest { public string? FechaStr { get; set; } public decimal Monto { get; set; } }

    /// <summary>Lo pagado de SUELDO de una liquidación: pagos que no son de los módulos.</summary>
    private static decimal PagadoSueldo(IEnumerable<NomPago> pagosLiq)
        => pagosLiq.Where(p => p.ExtraId == null && !NomExtrasController.Tipos.Contains(p.Concepto)).Sum(p => p.Monto);

    private static decimal CargadoSueldo(NomLiquidacion l) => Math.Max(0m, l.NetoAPagar - l.MontoExtras);

    /// <summary>Reparte lo pagado de sueldo entre las fechas, por orden: cuánto falta de cada una.</summary>
    private static List<(NomSueldoFecha F, decimal Falta)> Repartir(IEnumerable<NomSueldoFecha> fechas, decimal pagado)
    {
        var res = new List<(NomSueldoFecha, decimal)>();
        var pozo = pagado;
        foreach (var f in fechas.OrderBy(f => f.Fecha).ThenBy(f => f.Id))
        {
            var tomo = Math.Min(pozo, f.Monto);
            pozo -= tomo;
            res.Add((f, Math.Max(0m, f.Monto - tomo)));
        }
        return res;
    }

    // ============================================================
    // PANTALLA SUELDOS: empleado → conceptos
    // ============================================================

    [HttpGet("sueldos-mes")]
    public async Task<IActionResult> SueldosMes([FromQuery] int anio, [FromQuery] int mes)
    {
        if (anio < 2000 || mes < 1 || mes > 12) return BadRequest(new { error = "Año/mes inválido" });
        return Ok(await ArmarMesAsync(anio, mes));
    }

    /// <summary>16/11: desde julio 2026 los conceptos (comisión café, etc.) están separados del sueldo.</summary>
    public const int PrimerPeriodoVista = 202607;
    public record PendienteAnteriorDto(int EmpleadoId, string Nombre, string Tipo, int Anio, int Mes, decimal Debe,
        DateTime? ProximaFecha, int? LiquidacionId);

    /// <summary>
    /// 16/11: lo que quedó sin pagar de los meses ANTERIORES al elegido (desde julio 2026). Pedido: la comisión
    /// de octubre se paga el 5/11; mirando noviembre tiene que verse que falta pagarla.
    /// </summary>
    [HttpGet("pendientes-anteriores")]
    public async Task<IActionResult> PendientesAnteriores([FromQuery] int anio, [FromQuery] int mes)
    {
        if (anio < 2000 || mes < 1 || mes > 12) return BadRequest(new { error = "Año/mes inválido" });
        var res = new List<PendienteAnteriorDto>();
        var (a, m) = (PrimerPeriodoVista / 100, PrimerPeriodoVista % 100);
        while (a * 100 + m < anio * 100 + mes)
        {
            foreach (var e in await ArmarMesAsync(a, m))
                foreach (var c in e.Conceptos.Where(c => c.Debe > 0.01m))
                    res.Add(new PendienteAnteriorDto(e.EmpleadoId, e.Nombre, c.Tipo, a, m, c.Debe, c.ProximaFecha, e.LiquidacionId));
            if (++m > 12) { m = 1; a++; }
        }
        return Ok(res.OrderBy(r => r.Anio).ThenBy(r => r.Mes).ThenBy(r => r.Nombre).ToList());
    }

    private async Task<List<SueldoMesEmpleadoDto>> ArmarMesAsync(int anio, int mes)
    {
        var liqs = await _db.NomLiquidaciones.Where(l => l.Anio == anio && l.Mes == mes && l.Estado != "anulada").ToListAsync();
        var liqIds = liqs.Select(l => l.Id).ToList();
        var empIdsLiq = liqs.Select(l => l.EmpleadoId).ToList();
        var emps = await _db.NomEmpleados.Where(e => e.IsActive || empIdsLiq.Contains(e.Id)).OrderBy(e => e.Nombre).ToListAsync();
        var pagos = await _db.NomPagos.Where(p => liqIds.Contains(p.LiquidacionId)).ToListAsync();
        var extras = await _db.NomExtras.Where(x => liqIds.Contains(x.LiquidacionId)).OrderBy(x => x.Id).ToListAsync();
        var fechas = await _db.NomSueldoFechas.Where(f => liqIds.Contains(f.LiquidacionId)).ToListAsync();
        var pagadoPorExtra = new Dictionary<int, decimal>();
        foreach (var tipo in NomExtrasController.Tipos)
            foreach (var kv in await NomExtrasController.PagadoPorExtraAsync(_db, liqIds, tipo)) pagadoPorExtra[kv.Key] = kv.Value;

        var res = new List<SueldoMesEmpleadoDto>();
        foreach (var e in emps)
        {
            var liq = liqs.FirstOrDefault(l => l.EmpleadoId == e.Id);
            var conceptos = new List<SueldoMesConceptoDto>();
            if (liq is not null)
            {
                var pagosLiq = pagos.Where(p => p.LiquidacionId == liq.Id).ToList();
                // --- Sueldo ---
                var cargado = CargadoSueldo(liq);
                // Antes de nov 2026 la liquidación traía adentro aguinaldo / bono / comisión (NetoAPagar los incluye) y
                // esos pagos se anotaban con su concepto: ahí cuenta todo pago suelto (sin ExtraId) como pago de la liquidación.
                var viejo = anio * 100 + mes < 202611;
                var pagosSueldo = viejo ? pagosLiq.Where(p => p.ExtraId == null).ToList()
                                        : pagosLiq.Where(p => p.ExtraId == null && !NomExtrasController.Tipos.Contains(p.Concepto)).ToList();
                var pagado = pagosSueldo.Sum(p => p.Monto);
                var debe = Math.Max(0m, cargado - pagado);
                var reparto = Repartir(fechas.Where(f => f.LiquidacionId == liq.Id), pagado);
                var prox = reparto.FirstOrDefault(r => r.Falta > 0.01m);
                var sinFecha = debe > 0.01m && prox.F is null;
                conceptos.Add(new SueldoMesConceptoDto("sueldo", cargado, pagado, debe,
                    pagosSueldo.Count == 0 ? null : pagosSueldo.Count == 1 ? "1 pago" : $"{pagosSueldo.Count} pagos",
                    debe > 0.01m ? prox.F?.Fecha : null, debe > 0.01m && prox.F is not null ? Math.Min(prox.Falta, debe) : 0m,
                    pagosSueldo.Count > 0 ? pagosSueldo.Max(p => p.FechaPago) : null, sinFecha));

                // --- Módulos ---
                foreach (var tipo in NomExtrasController.Tipos)
                {
                    var xs = extras.Where(x => x.LiquidacionId == liq.Id && x.Tipo == tipo).ToList();
                    if (xs.Count == 0) continue;
                    var car = xs.Sum(x => x.Monto);
                    var pag = xs.Sum(x => Math.Min(x.Monto, pagadoPorExtra.GetValueOrDefault(x.Id)));
                    var deb = Math.Max(0m, car - pag);
                    var pend = xs.Where(x => x.Monto - pagadoPorExtra.GetValueOrDefault(x.Id) > 0.01m).ToList();
                    var conFecha = pend.Where(x => x.FechaPago != null).ToList();
                    DateTime? pf = conFecha.Count > 0 ? conFecha.Min(x => x.FechaPago) : null;
                    var pm = pf is null ? 0m : conFecha.Where(x => x.FechaPago == pf).Sum(x => x.Monto - pagadoPorExtra.GetValueOrDefault(x.Id));
                    var pagosX = pagosLiq.Where(p => p.ExtraId != null && xs.Any(x => x.Id == p.ExtraId)).ToList();
                    conceptos.Add(new SueldoMesConceptoDto(tipo, car, pag, deb, InfoExtras(tipo, xs), pf, pm,
                        pagosX.Count > 0 ? pagosX.Max(p => p.FechaPago) : null, deb > 0.01m && pf is null));
                }
            }
            if (liq is null && !e.IsActive) continue;
            res.Add(new SueldoMesEmpleadoDto(e.Id, e.Nombre, e.Puesto, liq?.Id, conceptos));
        }
        return res;
    }

    private static string? InfoExtras(string tipo, List<NomExtra> xs) => tipo switch
    {
        "horas_extra" => xs.Sum(x => x.Cantidad) > 0 ? $"{xs.Sum(x => x.Cantidad):0.##} hs" : null,
        "comision_cafe" => xs.Sum(x => x.Cantidad) > 0 ? $"{xs.Sum(x => x.Cantidad):0.##} kg café" : null,
        "feriado" => string.Join(", ", xs.Where(x => x.Fecha != null).Select(x => x.Fecha!.Value.ToString("dd/MM"))) is { Length: > 0 } f ? f : null,
        "plus" => Corto(string.Join(", ", xs.Select(x => x.Detalle).Where(d => !string.IsNullOrWhiteSpace(d)))),
        _ => null
    };
    private static string? Corto(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Length > 40 ? s[..40] + "…" : s;

    // ============================================================
    // FECHAS DE PAGO DEL SUELDO
    // ============================================================

    [HttpGet("liquidaciones/{id:int}/sueldo-fechas")]
    public async Task<IActionResult> Fechas(int id)
    {
        var liq = await _db.NomLiquidaciones.FindAsync(id);
        if (liq is null) return NotFound(new { error = "Liquidación no encontrada" });
        return Ok(await FechasDto(liq));
    }

    private async Task<List<SueldoFechaDto>> FechasDto(NomLiquidacion liq)
    {
        var fechas = await _db.NomSueldoFechas.Where(f => f.LiquidacionId == liq.Id).ToListAsync();
        var pagado = PagadoSueldo(await _db.NomPagos.Where(p => p.LiquidacionId == liq.Id).ToListAsync());
        return Repartir(fechas, pagado).Select(r => new SueldoFechaDto(r.F.Id, r.F.LiquidacionId, r.F.Fecha, r.F.Monto, r.Falta)).ToList();
    }

    [HttpPost("liquidaciones/{id:int}/sueldo-fechas")]
    public async Task<IActionResult> AgregarFecha(int id, [FromBody] SueldoFechaRequest req)
    {
        var liq = await _db.NomLiquidaciones.FindAsync(id);
        if (liq is null) return NotFound(new { error = "Liquidación no encontrada" });
        if (!DateTime.TryParseExact((req.FechaStr ?? "").Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var fecha))
            return BadRequest(new { error = "Poné la fecha" });
        if (req.Monto <= 0m) return BadRequest(new { error = "Poné un importe mayor a 0" });
        var yaPlaneado = await _db.NomSueldoFechas.Where(f => f.LiquidacionId == id).SumAsync(f => (decimal?)f.Monto) ?? 0m;
        var cargado = CargadoSueldo(liq);
        if (yaPlaneado + req.Monto > cargado + 0.01m)
            return BadRequest(new { error = $"Con esta fecha pasás el sueldo del mes (${cargado.ToString("N0", EsAr)}). Ya hay ${yaPlaneado.ToString("N0", EsAr)} con fecha." });
        _db.NomSueldoFechas.Add(new NomSueldoFecha { LiquidacionId = id, Fecha = fecha.Date, Monto = Math.Round(req.Monto, 2), CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        return Ok(await FechasDto(liq));
    }

    [HttpDelete("sueldo-fechas/{id:int}")]
    public async Task<IActionResult> BorrarFecha(int id)
    {
        var f = await _db.NomSueldoFechas.FindAsync(id);
        if (f is null) return NotFound(new { error = "No encontrada" });
        _db.NomSueldoFechas.Remove(f);
        await _db.SaveChangesAsync();
        var liq = await _db.NomLiquidaciones.FindAsync(f.LiquidacionId);
        return Ok(liq is null ? new List<SueldoFechaDto>() : await FechasDto(liq));
    }

    // ============================================================
    // PRÓXIMOS PAGOS (inicio → "Pagos al equipo")
    // ============================================================

    [HttpGet("proximos-pagos")]
    public async Task<IActionResult> Proximos([FromQuery] int dias = 7)
        => Ok(await ProximosPagosAsync(_db, HoyAr().AddDays(Math.Clamp(dias, 0, 60))));

    /// <summary>Lo que falta pagar con fecha hasta <paramref name="hasta"/> (incluye todo lo atrasado).</summary>
    public static async Task<List<ProximoPagoDto>> ProximosPagosAsync(AppDbContext db, DateTime hasta)
    {
        var res = new List<ProximoPagoDto>();
        var nombres = await db.NomEmpleados.ToDictionaryAsync(e => e.Id, e => e.Nombre);

        // Extras con fecha de pago
        foreach (var kv in await NomExtrasController.APagarHastaAsync(db, hasta))
            foreach (var (x, debe) in kv.Value)
            {
                var liq = await db.NomLiquidaciones.FindAsync(x.LiquidacionId);
                if (liq is null || liq.Estado == "anulada") continue;
                res.Add(new ProximoPagoDto(x.EmpleadoId, nombres.GetValueOrDefault(x.EmpleadoId, "—"), x.Tipo, x.FechaPago!.Value.Date, debe, liq.Anio, liq.Mes, liq.Id));
            }

        // Sueldo: fechas cargadas
        var liqIds = await db.NomSueldoFechas.Where(f => f.Fecha <= hasta).Select(f => f.LiquidacionId).Distinct().ToListAsync();
        if (liqIds.Count > 0)
        {
            var liqs = await db.NomLiquidaciones.Where(l => liqIds.Contains(l.Id) && l.Estado != "anulada").ToListAsync();
            var fechas = await db.NomSueldoFechas.Where(f => liqIds.Contains(f.LiquidacionId)).ToListAsync();
            var pagos = await db.NomPagos.Where(p => liqIds.Contains(p.LiquidacionId)).ToListAsync();
            foreach (var liq in liqs)
            {
                var pagado = PagadoSueldo(pagos.Where(p => p.LiquidacionId == liq.Id));
                var debeTotal = Math.Max(0m, CargadoSueldo(liq) - pagado);
                foreach (var (f, falta) in Repartir(fechas.Where(f => f.LiquidacionId == liq.Id), pagado))
                {
                    if (f.Fecha > hasta || falta <= 0.01m || debeTotal <= 0.01m) continue;
                    var monto = Math.Min(falta, debeTotal);
                    debeTotal -= monto;
                    res.Add(new ProximoPagoDto(liq.EmpleadoId, nombres.GetValueOrDefault(liq.EmpleadoId, "—"), "sueldo", f.Fecha.Date, monto, liq.Anio, liq.Mes, liq.Id));
                }
            }
        }
        return res.OrderBy(r => r.Fecha).ThenBy(r => r.Nombre).ToList();
    }
}
