using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-11: Horas extras, Comisiones, Feriados trabajados, Aguinaldo y Plus — cada uno en su
/// módulo, aparte de la liquidación de sueldo. Pedido del dueño: "no me gusta como esta todo junto,
/// prefiero que sea en modulos diferentes por mas que despues se pueda sumar".
///
/// Cómo se arma:
///  - Cada renglón (Nom_Extras) cuelga de la liquidación del mes del empleado (aguinaldo: junio o
///    diciembre). Si esa liquidación no existe, se crea sola (con el sueldo de la ficha, como el
///    botón "+ Nueva Liquidación"). Así el total del mes y "¿Cuánto debo pagar?" lo suman.
///  - Se paga desde el módulo: el pago es un Nom_Pagos normal (sale de la caja igual que el sueldo)
///    con ExtraId apuntando al renglón y Concepto = Tipo.
///  - El monto lo deciden ellos. El sistema solo PROPONE (fichaje, kg × $/kg, sueldo ÷ 30, mitad
///    del mejor sueldo) y ellos lo pisan.
/// </summary>
[ApiController]
[Route("api/nominas")]
[Authorize]
public class NomExtrasController : ControllerBase
{
    private readonly AppDbContext _db;
    public NomExtrasController(AppDbContext db) { _db = db; }

    public static readonly string[] Tipos = { "horas_extra", "comision_cafe", "feriado", "aguinaldo", "plus" };

    private static DateTime HoyAr() => DateTime.UtcNow.AddHours(-3).Date;
    private static readonly System.Globalization.CultureInfo EsAr = new("es-AR");
    private static string M(decimal v) => v.ToString("N0", EsAr);
    private static string M2(decimal v) => v.ToString("N2", EsAr);

    // ============================================================
    // MÓDULO: lo cargado + lo que el sistema propone
    // ============================================================

    [HttpGet("extras/{tipo}")]
    public async Task<IActionResult> GetModulo(string tipo, [FromQuery] int anio, [FromQuery] int mes)
    {
        tipo = (tipo ?? "").Trim().ToLowerInvariant();
        if (!Tipos.Contains(tipo)) return BadRequest(new { error = "Módulo desconocido" });
        if (anio < 2000 || anio > 2100 || mes < 1 || mes > 12) return BadRequest(new { error = "Año/mes inválido" });
        if (tipo == "aguinaldo") mes = mes <= 6 ? 6 : 12;

        var cargados = await _db.NomExtras
            .Include(x => x.EmpleadoNav)
            .Include(x => x.LiquidacionNav)
            .Where(x => x.Tipo == tipo && x.LiquidacionNav!.Anio == anio && x.LiquidacionNav.Mes == mes)
            .OrderBy(x => x.Fecha).ThenBy(x => x.EmpleadoNav!.Nombre).ThenBy(x => x.Id)
            .ToListAsync();
        var ids = cargados.Select(x => x.Id).ToList();
        var pagos = await _db.NomPagos.Where(p => p.ExtraId != null && ids.Contains(p.ExtraId.Value)).ToListAsync();
        var pagadoPor = await PagadoPorExtraAsync(_db, cargados.Select(x => x.LiquidacionId).Distinct().ToList(), tipo);

        var empleados = await _db.NomEmpleados.Where(e => e.IsActive).OrderBy(e => e.Nombre).ToListAsync();
        var desde = new DateTime(anio, mes, 1);
        var hasta = desde.AddMonths(1).AddDays(-1);
        var feriados = await _db.HorasExtrasFeriados.Where(f => f.Fecha >= desde && f.Fecha <= hasta)
            .OrderBy(f => f.Fecha).ToListAsync();

        var sugerencias = tipo switch
        {
            "horas_extra" => await SugerirHorasExtra(empleados, cargados, desde, hasta),
            "comision_cafe" => SugerirComisiones(empleados, cargados),
            "feriado" => await SugerirFeriados(empleados, cargados, feriados),
            "aguinaldo" => await SugerirAguinaldo(empleados, cargados, anio, mes),
            _ => new List<NomExtraSugerenciaDto>()
        };

        return Ok(new NomExtrasModuloDto(
            tipo, anio, mes,
            cargados.Select(x => MapExtra(x, pagos.Where(p => p.ExtraId == x.Id).ToList(), pagadoPor.GetValueOrDefault(x.Id))).ToList(),
            sugerencias,
            feriados.Select(f => new NomFeriadoDto(f.Id, f.Fecha, f.Descripcion)).ToList(),
            empleados.Select(e => new NomExtraEmpleadoDto(e.Id, e.Nombre, e.SueldoBase, e.ValorHora,
                e.ComisionPorKg, e.ModalidadSueldo, e.JornalDiario)).ToList()));
    }

    /// <summary>Horas extra del fichaje: por cada día con entrada y salida, lo trabajado de más
    /// sobre la jornada de ese día. Los FERIADOS no cuentan acá (van a su módulo).</summary>
    private async Task<List<NomExtraSugerenciaDto>> SugerirHorasExtra(
        List<NomEmpleado> empleados, List<NomExtra> cargados, DateTime desde, DateTime hasta)
    {
        var res = new List<NomExtraSugerenciaDto>();
        var fichas = await _db.HorasExtrasEmpleados.Where(f => f.NomEmpleadoId != null && f.IsActive).ToListAsync();
        var fichaIds = fichas.Select(f => f.Id).ToList();
        var regs = await _db.HorasExtrasRegistros
            .Where(r => fichaIds.Contains(r.EmpleadoId) && r.Fecha >= desde && r.Fecha <= hasta
                        && r.HoraEntrada != null && r.HoraSalida != null)
            .ToListAsync();
        var feriados = new HashSet<DateTime>(await _db.HorasExtrasFeriados
            .Where(f => f.Fecha >= desde && f.Fecha <= hasta).Select(f => f.Fecha.Date).ToListAsync());

        foreach (var emp in empleados)
        {
            if (cargados.Any(c => c.EmpleadoId == emp.Id)) continue;
            decimal horas = 0m; int dias = 0;
            foreach (var ficha in fichas.Where(f => f.NomEmpleadoId == emp.Id))
            {
                // Socios sin horario controlado (jornada toda en 0): no se calcula, contaría todo.
                var semana = ficha.HorasLunes + ficha.HorasMartes + ficha.HorasMiercoles + ficha.HorasJueves
                           + ficha.HorasViernes + ficha.HorasSabado + ficha.HorasDomingo;
                if (semana <= 0m) continue;
                foreach (var r in regs.Where(r => r.EmpleadoId == ficha.Id))
                {
                    if (feriados.Contains(r.Fecha.Date)) continue;
                    var dur = r.HoraSalida!.Value - r.HoraEntrada!.Value;
                    if (dur.TotalMinutes < 0) dur += TimeSpan.FromHours(24);
                    var extra = (decimal)dur.TotalHours - ficha.HorasParaDia(r.Fecha.DayOfWeek);
                    if (extra > 0m) { horas += extra; dias++; }
                }
            }
            horas = Math.Round(horas, 2, MidpointRounding.AwayFromZero);
            if (horas <= 0m) continue;
            var monto = Math.Round(horas * emp.ValorHora, 0, MidpointRounding.AwayFromZero);
            res.Add(new NomExtraSugerenciaDto(emp.Id, emp.Nombre, null, null, horas, monto,
                $"{horas:0.##} h extra según el fichaje",
                emp.ValorHora > 0
                    ? $"Fichaje: {horas:0.##} h de más en {dias} día(s) × ${M(emp.ValorHora)} la hora"
                    : $"Fichaje: {horas:0.##} h de más en {dias} día(s) · sin valor hora en la ficha"));
        }
        return res;
    }

    /// <summary>Solo los que cobran comisión (tienen $ por kg en la ficha) y todavía no se cargaron.</summary>
    private static List<NomExtraSugerenciaDto> SugerirComisiones(List<NomEmpleado> empleados, List<NomExtra> cargados) =>
        empleados.Where(e => e.ComisionPorKg > 0m && !cargados.Any(c => c.EmpleadoId == e.Id))
            .Select(e => new NomExtraSugerenciaDto(e.Id, e.Nombre, null, null, 0m, 0m, null,
                $"Cobra ${M(e.ComisionPorKg)} por kg — poné los kg vendidos"))
            .ToList();

    /// <summary>Quién fichó en cada feriado del mes. El día ya va en el sueldo: si lo trabajó se le
    /// paga UN DÍA MÁS aparte. Propuesta: sueldo ÷ 30 (o el jornal si cobra por día).</summary>
    private async Task<List<NomExtraSugerenciaDto>> SugerirFeriados(
        List<NomEmpleado> empleados, List<NomExtra> cargados, List<HorasExtrasFeriado> feriados)
    {
        var res = new List<NomExtraSugerenciaDto>();
        if (feriados.Count == 0) return res;
        var fechas = feriados.Select(f => f.Fecha.Date).ToList();
        var fichas = await _db.HorasExtrasEmpleados.Where(f => f.NomEmpleadoId != null).ToListAsync();
        var fichaIds = fichas.Select(f => f.Id).ToList();
        var regs = await _db.HorasExtrasRegistros
            .Where(r => fichaIds.Contains(r.EmpleadoId) && fechas.Contains(r.Fecha)
                        && (r.HoraEntrada != null || r.HoraSalida != null || r.Cantidad > 0))
            .ToListAsync();
        foreach (var f in feriados)
        {
            foreach (var r in regs.Where(r => r.Fecha.Date == f.Fecha.Date))
            {
                var nomId = fichas.First(x => x.Id == r.EmpleadoId).NomEmpleadoId!.Value;
                var emp = empleados.FirstOrDefault(e => e.Id == nomId);
                if (emp is null) continue;
                if (cargados.Any(c => c.EmpleadoId == emp.Id && c.Fecha?.Date == f.Fecha.Date)) continue;
                if (res.Any(s => s.EmpleadoId == emp.Id && s.FeriadoId == f.Id)) continue;
                var esDiario = string.Equals(emp.ModalidadSueldo, "diario", StringComparison.OrdinalIgnoreCase);
                var valorDia = esDiario ? emp.JornalDiario : Math.Round(emp.SueldoBase / 30m, 0, MidpointRounding.AwayFromZero);
                var horario = r.HoraEntrada.HasValue && r.HoraSalida.HasValue
                    ? $"fichó {r.HoraEntrada.Value:hh\\:mm} a {r.HoraSalida.Value:hh\\:mm}"
                    : "fichó ese día";
                res.Add(new NomExtraSugerenciaDto(emp.Id, emp.Nombre, f.Fecha.Date, f.Id, 1m, valorDia,
                    $"Feriado {f.Fecha:dd/MM} · {f.Descripcion}",
                    $"{horario} · propuesto {(esDiario ? "jornal" : "sueldo ÷ 30")} ${M(valorDia)}"));
            }
        }
        return res;
    }

    /// <summary>Aguinaldo del semestre: mitad del mejor sueldo del semestre (de sus liquidaciones;
    /// si no tiene, el de la ficha), proporcional a los días si entró a mitad de semestre.</summary>
    private async Task<List<NomExtraSugerenciaDto>> SugerirAguinaldo(
        List<NomEmpleado> empleados, List<NomExtra> cargados, int anio, int mes)
    {
        var res = new List<NomExtraSugerenciaDto>();
        var semIni = new DateTime(anio, mes == 6 ? 1 : 7, 1);
        var semFin = new DateTime(anio, mes, DateTime.DaysInMonth(anio, mes));
        var mesesSem = Enumerable.Range(semIni.Month, 6).ToList();
        var liqs = await _db.NomLiquidaciones
            .Where(l => l.Anio == anio && mesesSem.Contains(l.Mes) && l.Estado != "anulada")
            .Select(l => new { l.EmpleadoId, l.SueldoBase })
            .ToListAsync();
        var diasSem = (semFin - semIni).Days + 1;
        foreach (var emp in empleados)
        {
            if (cargados.Any(c => c.EmpleadoId == emp.Id)) continue;
            var deLiqs = liqs.Where(l => l.EmpleadoId == emp.Id).Select(l => l.SueldoBase).DefaultIfEmpty(0m).Max();
            var mejor = deLiqs > 0m ? deLiqs : emp.SueldoBase;
            if (mejor <= 0m) continue;
            var ingreso = emp.FechaIngreso.Date;
            var inicio = ingreso > semIni ? ingreso : semIni;
            if (inicio > semFin) continue;
            var dias = (semFin - inicio).Days + 1;
            var monto = Math.Round(mejor / 2m * dias / diasSem, 0, MidpointRounding.AwayFromZero);
            res.Add(new NomExtraSugerenciaDto(emp.Id, emp.Nombre, null, null, dias, monto,
                $"Aguinaldo {(mes == 6 ? "1º" : "2º")} semestre {anio}",
                dias < diasSem
                    ? $"Mitad de ${M(mejor)} × {dias} de {diasSem} días (entró el {ingreso:dd/MM/yyyy})"
                    : $"Mitad del mejor sueldo del semestre (${M(mejor)})" + (deLiqs > 0m ? "" : " · de la ficha")));
        }
        return res;
    }

    // ============================================================
    // ALTA / CAMBIO / BAJA
    // ============================================================

    public class CrearExtraRequest
    {
        public string Tipo { get; set; } = "plus";
        public int EmpleadoId { get; set; }
        public int Anio { get; set; }
        public int Mes { get; set; }
        public string? FechaStr { get; set; }
        public decimal Cantidad { get; set; }
        public decimal Monto { get; set; }
        public string? Detalle { get; set; }
        public int? FeriadoId { get; set; }
    }

    [HttpPost("extras")]
    public async Task<IActionResult> Crear([FromBody] CrearExtraRequest req)
    {
        var tipo = (req.Tipo ?? "").Trim().ToLowerInvariant();
        if (!Tipos.Contains(tipo)) return BadRequest(new { error = "Módulo desconocido" });
        if (req.Monto <= 0m) return BadRequest(new { error = "Poné un monto mayor a 0" });
        if (req.Anio < 2000 || req.Anio > 2100 || req.Mes < 1 || req.Mes > 12) return BadRequest(new { error = "Año/mes inválido" });
        var mes = tipo == "aguinaldo" ? (req.Mes <= 6 ? 6 : 12) : req.Mes;
        var emp = await _db.NomEmpleados.FindAsync(req.EmpleadoId);
        if (emp is null) return BadRequest(new { error = "Empleado no encontrado" });

        DateTime? fecha = ParseFecha(req.FechaStr);
        if (tipo == "feriado" && req.FeriadoId.HasValue)
        {
            var fer = await _db.HorasExtrasFeriados.FindAsync(req.FeriadoId.Value);
            if (fer is null) return BadRequest(new { error = "Feriado no encontrado" });
            fecha = fer.Fecha.Date;
            if (await _db.NomExtras.AnyAsync(x => x.Tipo == "feriado" && x.EmpleadoId == emp.Id && x.Fecha == fecha))
                return BadRequest(new { error = $"Ya está cargado el feriado {fecha:dd/MM} de {emp.Nombre}" });
        }

        var (liq, error) = await LiquidacionDelMes(emp, req.Anio, mes);
        if (liq is null) return BadRequest(new { error });

        var extra = new NomExtra
        {
            LiquidacionId = liq.Id,
            EmpleadoId = emp.Id,
            Tipo = tipo,
            Fecha = fecha,
            Cantidad = Math.Max(0m, req.Cantidad),
            Monto = Math.Round(req.Monto, 2, MidpointRounding.AwayFromZero),
            Detalle = string.IsNullOrWhiteSpace(req.Detalle) ? null : req.Detalle.Trim(),
            FeriadoId = tipo == "feriado" ? req.FeriadoId : null,
            CargadoPor = User?.Identity?.Name,
            CreatedAt = DateTime.UtcNow
        };
        _db.NomExtras.Add(extra);
        await _db.SaveChangesAsync();
        await RecalcularLiquidacionAsync(_db, liq.Id);
        return Ok(new { id = extra.Id, liquidacionId = liq.Id });
    }

    public class EditarExtraRequest
    {
        public decimal? Cantidad { get; set; }
        public decimal? Monto { get; set; }
        public string? Detalle { get; set; }
        public string? FechaStr { get; set; }
    }

    [HttpPut("extras/{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] EditarExtraRequest req)
    {
        var x = await _db.NomExtras.FindAsync(id);
        if (x is null) return NotFound(new { error = "No encontrado" });
        var pagado = await _db.NomPagos.Where(p => p.ExtraId == id).SumAsync(p => (decimal?)p.Monto) ?? 0m;
        if (req.Monto.HasValue)
        {
            if (req.Monto.Value <= 0m) return BadRequest(new { error = "Poné un monto mayor a 0" });
            if (req.Monto.Value < pagado - 0.01m)
                return BadRequest(new { error = $"Ya se pagaron ${M2(pagado)}: el monto no puede ser menor" });
            x.Monto = Math.Round(req.Monto.Value, 2, MidpointRounding.AwayFromZero);
        }
        if (req.Cantidad.HasValue) x.Cantidad = Math.Max(0m, req.Cantidad.Value);
        if (req.Detalle is not null) x.Detalle = string.IsNullOrWhiteSpace(req.Detalle) ? null : req.Detalle.Trim();
        if (!string.IsNullOrWhiteSpace(req.FechaStr) && x.Tipo == "plus") x.Fecha = ParseFecha(req.FechaStr);
        x.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await RecalcularLiquidacionAsync(_db, x.LiquidacionId);
        return Ok(new { ok = true });
    }

    [HttpDelete("extras/{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var x = await _db.NomExtras.FindAsync(id);
        if (x is null) return NotFound(new { error = "No encontrado" });
        if (await _db.NomPagos.AnyAsync(p => p.ExtraId == id))
            return BadRequest(new { error = "Ya tiene pagos. Borrá primero el pago (desde la liquidación del mes)." });
        var liqId = x.LiquidacionId;
        _db.NomExtras.Remove(x);
        await _db.SaveChangesAsync();
        await RecalcularLiquidacionAsync(_db, liqId);
        return Ok(new { deleted = true });
    }

    // ============================================================
    // PAGAR un renglón
    // ============================================================

    public class PagarExtraRequest
    {
        public decimal Monto { get; set; }
        public string Metodo { get; set; } = "efectivo";
        public string? FechaPagoStr { get; set; }
        public int? CajaId { get; set; }
        public string? Notas { get; set; }
    }

    [HttpPost("extras/{id:int}/pagar")]
    public async Task<IActionResult> Pagar(int id, [FromBody] PagarExtraRequest req)
    {
        var x = await _db.NomExtras.Include(e => e.EmpleadoNav).FirstOrDefaultAsync(e => e.Id == id);
        if (x is null) return NotFound(new { error = "No encontrado" });
        var liq = await _db.NomLiquidaciones.FindAsync(x.LiquidacionId);
        if (liq is null) return BadRequest(new { error = "Liquidación no encontrada" });
        if (liq.Estado == "anulada") return BadRequest(new { error = "La liquidación de ese mes está anulada" });
        if (req.Monto <= 0m) return BadRequest(new { error = "El monto debe ser mayor a 0" });
        if (string.IsNullOrWhiteSpace(req.Metodo)) return BadRequest(new { error = "Indicá un método de pago" });
        var pagado = (await PagadoPorExtraAsync(_db, new List<int> { x.LiquidacionId }, x.Tipo)).GetValueOrDefault(x.Id);
        var debe = x.Monto - pagado;
        if (req.Monto > debe + 0.01m) return BadRequest(new { error = $"Supera lo que se debe (${M2(debe)})" });

        var pago = new NomPago
        {
            LiquidacionId = liq.Id,
            ExtraId = x.Id,
            FechaPago = ParseFecha(req.FechaPagoStr) ?? HoyAr(),
            Metodo = req.Metodo.Trim().ToLowerInvariant(),
            Monto = req.Monto,
            Concepto = x.Tipo,
            Detalle = DetalleAuto(x),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            CajaId = req.CajaId,
            CreatedAt = DateTime.UtcNow
        };
        _db.NomPagos.Add(pago);
        if (req.CajaId.HasValue && req.CajaId.Value > 0)
        {
            if (!await _db.CafeCajas.AnyAsync(c => c.Id == req.CajaId.Value)) return BadRequest(new { error = "La caja elegida no existe" });
            var mov = new CafeCajaMovimiento
            {
                CajaId = req.CajaId.Value,
                Fecha = pago.FechaPago.Date,
                Tipo = "SUELDO",
                Importe = -Math.Abs(pago.Monto),
                Motivo = $"{TipoLabel(x.Tipo)} de {x.EmpleadoNav?.Nombre ?? "empleado"} · {liq.Mes:00}/{liq.Anio}",
                CargadoPor = User?.Identity?.Name
            };
            _db.CafeCajaMovimientos.Add(mov);
            await _db.SaveChangesAsync();
            pago.CajaMovimientoId = mov.Id;
        }
        else pago.CajaId = null;
        await _db.SaveChangesAsync();
        await RecalcularLiquidacionAsync(_db, liq.Id);
        return Ok(new { ok = true });
    }

    // ============================================================
    // FERIADOS (se cargan desde el módulo Feriados)
    // ============================================================

    public class CrearFeriadoRequest { public string? FechaStr { get; set; } public string? Descripcion { get; set; } }

    [HttpPost("feriados")]
    public async Task<IActionResult> CrearFeriado([FromBody] CrearFeriadoRequest req)
    {
        var f = ParseFecha(req.FechaStr);
        if (f is null) return BadRequest(new { error = "Fecha inválida" });
        if (string.IsNullOrWhiteSpace(req.Descripcion)) return BadRequest(new { error = "Poné qué feriado es" });
        if (await _db.HorasExtrasFeriados.AnyAsync(x => x.Fecha == f.Value))
            return BadRequest(new { error = $"El {f:dd/MM/yyyy} ya está cargado como feriado" });
        _db.HorasExtrasFeriados.Add(new HorasExtrasFeriado { Fecha = f.Value, Descripcion = req.Descripcion.Trim(), CreatedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpDelete("feriados/{id:int}")]
    public async Task<IActionResult> BorrarFeriado(int id)
    {
        var f = await _db.HorasExtrasFeriados.FindAsync(id);
        if (f is null) return NotFound(new { error = "No encontrado" });
        if (await _db.NomExtras.AnyAsync(x => x.FeriadoId == id))
            return BadRequest(new { error = "Ese feriado ya tiene días pagados cargados. Borralos primero." });
        _db.HorasExtrasFeriados.Remove(f);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    // ============================================================
    // HELPERS
    // ============================================================

    /// <summary>La liquidación del empleado para ese mes. Si no existe se crea sola, igual que
    /// "+ Nueva Liquidación": sueldo de la ficha congelado y el bono fijo precargado.</summary>
    private async Task<(NomLiquidacion? liq, string? error)> LiquidacionDelMes(NomEmpleado emp, int anio, int mes)
    {
        var liq = await _db.NomLiquidaciones.FirstOrDefaultAsync(l => l.EmpleadoId == emp.Id && l.Anio == anio && l.Mes == mes);
        if (liq is not null)
            return liq.Estado == "anulada"
                ? (null, $"La liquidación de {emp.Nombre} de {mes:00}/{anio} está anulada")
                : (liq, null);
        liq = new NomLiquidacion
        {
            EmpleadoId = emp.Id, Anio = anio, Mes = mes,
            Bonos = Math.Max(0m, emp.BonoFijo),
            Estado = "pendiente", CreatedAt = DateTime.UtcNow,
            Notas = "Creada sola al cargar un extra"
        };
        NomLiquidacionesController.Calcular(liq, emp, esCreacion: true);
        _db.NomLiquidaciones.Add(liq);
        await _db.SaveChangesAsync();
        return (liq, null);
    }

    /// <summary>Vuelve a sumar los extras en la liquidación y acomoda el estado (pagado/pendiente).</summary>
    public static async Task RecalcularLiquidacionAsync(AppDbContext db, int liqId)
    {
        var liq = await db.NomLiquidaciones.Include(l => l.Pagos).FirstOrDefaultAsync(l => l.Id == liqId);
        if (liq is null) return;
        var emp = await db.NomEmpleados.FindAsync(liq.EmpleadoId);
        if (emp is null) return;
        liq.MontoExtras = await db.NomExtras.Where(x => x.LiquidacionId == liqId).SumAsync(x => (decimal?)x.Monto) ?? 0m;
        NomLiquidacionesController.Calcular(liq, emp, esCreacion: false);
        if (liq.Estado != "anulada")
        {
            var pagado = liq.Pagos.Sum(p => p.Monto);
            liq.Estado = pagado >= liq.NetoAPagar - 0.01m && liq.NetoAPagar > 0m ? "pagado" : "pendiente";
        }
        liq.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>Cuánto se pagó de cada extra. Además de los pagos hechos desde el módulo (ExtraId),
    /// reparte los pagos del mismo concepto hechos desde la liquidación o desde "¿Cuánto debo
    /// pagar?" (sin ExtraId), en orden de carga, descontando antes lo viejo que estaba en la
    /// liquidación (MontoHsExtra / Comision / Aguinaldo de los meses de antes).</summary>
    public static async Task<Dictionary<int, decimal>> PagadoPorExtraAsync(AppDbContext db, List<int> liqIds, string tipo)
    {
        var res = new Dictionary<int, decimal>();
        if (liqIds.Count == 0) return res;
        var extras = await db.NomExtras.Where(x => liqIds.Contains(x.LiquidacionId) && x.Tipo == tipo)
            .OrderBy(x => x.Id).ToListAsync();
        var pagos = await db.NomPagos.Where(p => liqIds.Contains(p.LiquidacionId) && (p.Concepto == tipo || p.ExtraId != null))
            .ToListAsync();
        var liqs = await db.NomLiquidaciones.Where(l => liqIds.Contains(l.Id)).ToListAsync();
        foreach (var liq in liqs)
        {
            var viejo = tipo switch { "horas_extra" => liq.MontoHsExtra, "comision_cafe" => liq.Comision, "aguinaldo" => liq.Aguinaldo, _ => 0m };
            var sueltos = pagos.Where(p => p.LiquidacionId == liq.Id && p.ExtraId == null && p.Concepto == tipo).Sum(p => p.Monto);
            var pozo = Math.Max(0m, sueltos - viejo);
            foreach (var x in extras.Where(e => e.LiquidacionId == liq.Id))
            {
                var directo = pagos.Where(p => p.ExtraId == x.Id).Sum(p => p.Monto);
                var falta = Math.Max(0m, x.Monto - directo);
                var tomo = Math.Min(pozo, falta);
                pozo -= tomo;
                res[x.Id] = directo + tomo;
            }
        }
        return res;
    }

    private static DateTime? ParseFecha(string? s) =>
        !string.IsNullOrWhiteSpace(s) && DateTime.TryParseExact(s.Trim(), "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d.Date : null;

    public static string TipoLabel(string tipo) => tipo switch
    {
        "horas_extra" => "Horas extra",
        "comision_cafe" => "Comisión",
        "feriado" => "Feriado trabajado",
        "aguinaldo" => "Aguinaldo",
        "plus" => "Plus",
        _ => "Extra"
    };

    private static string DetalleAuto(NomExtra x) => x.Tipo switch
    {
        "horas_extra" => $"{x.Cantidad:0.##} h extra" + (string.IsNullOrWhiteSpace(x.Detalle) ? "" : $" · {x.Detalle}"),
        "comision_cafe" => $"{x.Cantidad:0.##} kg de café" + (string.IsNullOrWhiteSpace(x.Detalle) ? "" : $" · {x.Detalle}"),
        _ => x.Detalle ?? TipoLabel(x.Tipo)
    };

    private static NomExtraDto MapExtra(NomExtra x, List<NomPago> pagos, decimal pagado)
    {
        return new NomExtraDto(
            x.Id, x.LiquidacionId, x.EmpleadoId, x.EmpleadoNav?.Nombre ?? "—", x.Tipo,
            x.LiquidacionNav?.Anio ?? 0, x.LiquidacionNav?.Mes ?? 0,
            x.Fecha, x.Cantidad, x.Monto, x.Detalle, x.FeriadoId,
            pagado, Math.Max(0m, x.Monto - pagado), x.CargadoPor, x.CreatedAt,
            pagos.OrderBy(p => p.FechaPago).Select(p => new NomPagoDto(
                p.Id, p.LiquidacionId, p.FechaPago, p.Metodo, p.Monto, p.Concepto, p.Detalle, p.Notas, p.CreatedAt)).ToList());
    }
}
