using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Modulo de Horas Extras: cada empleado tiene un link publico con su token. Entra al link
/// y carga cuantas horas extras hizo cada dia. El admin ve todo en tiempo real desde el panel.
///
/// Estructura:
///   /api/horas-extras/admin/...   → requiere auth (admin)
///   /api/horas-extras/publica/... → AllowAnonymous, acceso por token
/// </summary>
[ApiController]
[Route("api/horas-extras")]
public class HorasExtrasController : ControllerBase
{
    private readonly AppDbContext _db;
    public HorasExtrasController(AppDbContext db) { _db = db; }

    // ============================================================
    // ENDPOINTS PUBLICOS (sin auth, por token)
    // ============================================================

    public record PublicRegistroDto(int Id, DateTime Fecha, decimal Cantidad, string? Observaciones,
        string? HoraEntrada, string? HoraSalida,
        // 2026-06-03: extras del dia (trabajadas - jornada del dia de la semana).
        // Positivo = extras (verde en UI). Negativo = falta de horas (rojo). null = no aplica (sin horario).
        decimal? ExtrasDia);
    public record PublicEmpleadoDto(string Nombre, DateTime HoyFecha, DateTime FechaSeleccionada,
        decimal? HorasSeleccionada, string? ObservacionesSeleccionada,
        string? HoraEntradaSeleccionada, string? HoraSalidaSeleccionada,
        List<PublicRegistroDto> Ultimos7Dias, decimal TotalSemana, decimal TotalMes,
        // 2026-06-03: ciclo de liquidacion del empleado (puede ser mes calendario o personalizado).
        // CicloLabel: "ESTE MES (junio)" o "CICLO 16/05 → 15/06".
        // TotalCiclo: suma de horas trabajadas dentro del ciclo. ExtrasCiclo: suma de extras (>=0).
        // MostrarExtras: si el admin tildo el checkbox para que el empleado vea sus extras.
        DateTime CicloDesde, DateTime CicloHasta, string CicloLabel,
        decimal TotalCiclo, decimal ExtrasCiclo, bool MostrarExtras);

    /// <summary>Busca un empleado por slug del nombre + clave (últimos 3 del DNI). Helper
    /// usado por todos los endpoints públicos. Devuelve null si no coincide nada activo.</summary>
    private async Task<HorasExtrasEmpleado?> FindEmpleadoAsync(string slug, string clave)
    {
        if (string.IsNullOrWhiteSpace(slug) || string.IsNullOrWhiteSpace(clave)) return null;
        clave = clave.Trim();
        if (clave.Length != 3 || !clave.All(char.IsDigit)) return null;
        var slugNorm = Slugify(slug);
        // Comparamos en memoria — son pocos empleados, no vale la pena indexar el slug.
        var todos = await _db.HorasExtrasEmpleados.Where(e => e.IsActive && e.DniUltimos3 == clave).ToListAsync();
        return todos.FirstOrDefault(e => Slugify(e.Nombre) == slugNorm);
    }

    /// <summary>Convierte un nombre a slug seguro (lowercase, sin acentos, espacios → guion).
    /// Ej: "Pablo López" → "pablo-lopez".</summary>
    private static string Slugify(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var n = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var c in n)
        {
            var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (uc == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
    }

    /// <summary>El empleado abre el link con su slug + clave y obtiene su nombre + carga del dia + historial.
    /// Opcional: ?fecha=YYYY-MM-DD para precargar los datos de una fecha pasada (carga atrasada).</summary>
    [HttpGet("publica/{slug}/{clave}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetPublica(string slug, string clave, [FromQuery] string? fecha = null)
    {
        var emp = await FindEmpleadoAsync(slug, clave);
        if (emp is null) return NotFound(new { error = "Link inválido o empleado inactivo" });

        var hoy = FechaArgentinaHoy();
        // Si vino fecha, la parseamos; si no, hoy. Cap a hoy (no permitir futuro).
        var fechaSel = hoy;
        if (!string.IsNullOrWhiteSpace(fecha) && DateTime.TryParse(fecha, out var f))
        {
            fechaSel = f.Date;
            if (fechaSel > hoy) fechaSel = hoy;
        }
        var hace7 = hoy.AddDays(-6);
        // 2026-06-03: el ciclo de liquidacion ahora puede ser personalizado (Alexis 16→15).
        var ciclo = emp.CicloActual(hoy);

        // Traemos los registros del empleado desde el comienzo del ciclo o desde hace 7 dias
        // o desde la fecha seleccionada — lo que sea mas viejo (para cubrir todos los casos
        // que el frontend pueda necesitar mostrar).
        var desde = new[] { ciclo.Desde, hace7, fechaSel }.Min();
        var registros = await _db.HorasExtrasRegistros
            .Where(r => r.EmpleadoId == emp.Id && r.Fecha >= desde)
            .OrderByDescending(r => r.Fecha)
            .ToListAsync();

        var registroSel = registros.FirstOrDefault(r => r.Fecha == fechaSel);
        // 2026-06-03 FIX: desde 02/06 el publico guarda Cantidad=0 y el calculo lo hace el admin.
        // Antes el mobile mostraba r.Cantidad → siempre 0 para registros nuevos. Ahora mostramos
        // las HORAS TRABAJADAS (Salida − Entrada) que es lo util para el empleado.
        decimal HorasTrabajadas(HorasExtrasRegistro r)
        {
            if (!r.HoraEntrada.HasValue || !r.HoraSalida.HasValue) return r.Cantidad; // fallback al campo viejo
            var dur = r.HoraSalida.Value - r.HoraEntrada.Value;
            if (dur.TotalMinutes <= 0) dur = dur.Add(TimeSpan.FromDays(1));            // cruza medianoche
            return Math.Round((decimal)dur.TotalHours, 2, MidpointRounding.AwayFromZero);
        }
        // Calcula extras del dia (trabajadas - jornada del dia de la semana). null si no hay horario cargado.
        // Positivo = extras (verde). Negativo = falto horas (rojo). 0 = justo (la UI lo oculta).
        decimal? ExtrasDelDia(HorasExtrasRegistro r)
        {
            if (!r.HoraEntrada.HasValue || !r.HoraSalida.HasValue) return null;
            var trab = HorasTrabajadas(r);
            var jornada = emp.HorasParaDia(r.Fecha.DayOfWeek);
            return Math.Round(trab - jornada, 2, MidpointRounding.AwayFromZero);
        }
        var ultimos7 = registros.Where(r => r.Fecha >= hace7 && r.Fecha <= hoy).OrderByDescending(r => r.Fecha)
            .Select(r => new PublicRegistroDto(r.Id, r.Fecha, HorasTrabajadas(r), r.Observaciones,
                FormatHora(r.HoraEntrada), FormatHora(r.HoraSalida), ExtrasDelDia(r)))
            .ToList();
        var totalSemana = ultimos7.Sum(r => r.Cantidad); // legacy, no se muestra (se saco el cuadro "Esta semana")
        // Totales del CICLO (no del mes calendario, salvo que el empleado este en ciclo "mes calendario").
        var registrosCiclo = registros.Where(r => r.Fecha >= ciclo.Desde && r.Fecha <= ciclo.Hasta).ToList();
        var totalCiclo = registrosCiclo.Sum(r => HorasTrabajadas(r));
        // Extras del ciclo = SOLO POSITIVOS (no se compensan horas faltadas con extras).
        var extrasCiclo = registrosCiclo.Sum(r => Math.Max(0m, ExtrasDelDia(r) ?? 0m));

        return Ok(new PublicEmpleadoDto(emp.Nombre, hoy, fechaSel,
            registroSel?.Cantidad, registroSel?.Observaciones,
            FormatHora(registroSel?.HoraEntrada), FormatHora(registroSel?.HoraSalida),
            ultimos7, totalSemana, totalCiclo,
            ciclo.Desde, ciclo.Hasta, ciclo.Label,
            totalCiclo, extrasCiclo, emp.MostrarExtrasAlEmpleado));
    }

    /// <summary>Devuelve "HH:mm" o null. Lo usamos en el JSON para que el input type=time del browser
    /// lo entienda sin conversion extra.</summary>
    private static string? FormatHora(TimeSpan? t) =>
        t.HasValue ? $"{t.Value.Hours:D2}:{t.Value.Minutes:D2}" : null;

    public class CargarHorasRequest
    {
        public decimal Cantidad { get; set; }
        public string? Observaciones { get; set; }
        /// <summary>Opcional. Si null, se usa la fecha de hoy (Argentina). Si viene, debe ser hoy o ayer.</summary>
        public DateTime? Fecha { get; set; }
        /// <summary>Opcionales. Formato "HH:mm" (lo que manda el input type=time). Null = no se carga.</summary>
        public string? HoraEntrada { get; set; }
        public string? HoraSalida { get; set; }
    }

    /// <summary>El empleado carga (o actualiza) sus horas extras del día. Si ya existe un registro
    /// para ese día, se actualiza. Si no, se crea. UPSERT.</summary>
    [HttpPost("publica/{slug}/{clave}")]
    [AllowAnonymous]
    public async Task<IActionResult> CargarPublica(string slug, string clave, [FromBody] CargarHorasRequest req)
    {
        var emp = await FindEmpleadoAsync(slug, clave);
        if (emp is null) return NotFound(new { error = "Link inválido o empleado inactivo" });

        // El campo en DB es decimal(5,2) → hasta 999.99. Antes limitabamos a 24 pensando en
        // 1 dia, pero el usuario carga acumulados (ej. 90hs para cerrar periodo) → permitido.
        if (req.Cantidad < 0 || req.Cantidad > 999)
            return BadRequest(new { error = "Cantidad inválida (0–999)" });

        // 2026-06-02: el empleado SOLO puede cargar el dia de hoy. Sin cargas atrasadas.
        // Si vino fecha, debe ser exactamente la de hoy (Argentina). Si quieren corregir
        // un dia viejo, lo hace el admin desde el panel.
        var hoy = FechaArgentinaHoy();
        var fechaCarga = req.Fecha?.Date ?? hoy;
        if (fechaCarga != hoy)
            return BadRequest(new { error = "Solo podés cargar el día de hoy. Pedile al admin que corrija otros días." });

        // Parsea "HH:mm" → TimeSpan?. Strings vacios o invalidos → null (campo opcional).
        var horaEnt = ParseHora(req.HoraEntrada);
        var horaSal = ParseHora(req.HoraSalida);

        var existente = await _db.HorasExtrasRegistros.FirstOrDefaultAsync(r => r.EmpleadoId == emp.Id && r.Fecha == fechaCarga);
        if (existente is null)
        {
            existente = new HorasExtrasRegistro
            {
                EmpleadoId = emp.Id,
                Fecha = fechaCarga,
                Cantidad = req.Cantidad,
                Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim(),
                HoraEntrada = horaEnt,
                HoraSalida = horaSal,
                CreatedAt = DateTime.UtcNow
            };
            _db.HorasExtrasRegistros.Add(existente);
        }
        else
        {
            existente.Cantidad = req.Cantidad;
            existente.Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim();
            existente.HoraEntrada = horaEnt;
            existente.HoraSalida = horaSal;
            existente.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, fecha = fechaCarga, cantidad = req.Cantidad });
    }

    /// <summary>Parsea "HH:mm" o "HH:mm:ss" a TimeSpan. Strings vacios/invalidos → null.</summary>
    private static TimeSpan? ParseHora(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return TimeSpan.TryParse(s.Trim(), out var t) ? t : null;
    }

    // ============================================================
    // ENDPOINTS ADMIN (con auth)
    // ============================================================

    public record AdminEmpleadoDto(int Id, string Nombre, string Token, bool IsActive,
        decimal TotalHoy, decimal TotalSemana, decimal TotalMes, DateTime? UltimaCargaAt, DateTime CreatedAt,
        string? UltimaHoraEntrada, string? UltimaHoraSalida,
        string? DniUltimos3, string Slug,
        // 2026-06-02: jornada configurable por dia de la semana + total semanal
        decimal HorasLunes, decimal HorasMartes, decimal HorasMiercoles, decimal HorasJueves,
        decimal HorasViernes, decimal HorasSabado, decimal HorasDomingo,
        decimal JornadaSemanal,
        // Acumulado real (horario marcado en los registros) vs esperado segun jornada
        decimal TrabajadoSemana, decimal EsperadoSemana, decimal DiferenciaSemana,
        decimal TrabajadoMes, decimal EsperadoMes, decimal DiferenciaMes,
        // 2026-06-03: flag para mostrar extras al empleado + ciclo de liquidacion personalizado
        bool MostrarExtrasAlEmpleado, int? CicloDiaInicio, int? CicloDiaFin,
        // Calculado: rango del ciclo actual + totales del ciclo
        DateTime CicloDesde, DateTime CicloHasta, string CicloLabel,
        decimal TrabajadoCiclo, decimal EsperadoCiclo, decimal DiferenciaCiclo);

    /// <summary>Lista de empleados con totales (hoy / semana / mes) y la última vez que cargaron.</summary>
    [HttpGet("admin/empleados")]
    [Authorize]
    public async Task<IActionResult> ListEmpleados()
    {
        var emps = await _db.HorasExtrasEmpleados.OrderBy(e => e.Nombre).ToListAsync();
        var hoy = FechaArgentinaHoy();
        var inicioSemana = hoy.AddDays(-6);
        var inicioMes = new DateTime(hoy.Year, hoy.Month, 1);

        var regs = await _db.HorasExtrasRegistros
            .Where(r => r.Fecha >= inicioMes)
            .ToListAsync();

        var ultimasCargas = await _db.HorasExtrasRegistros
            .GroupBy(r => r.EmpleadoId)
            .Select(g => new { EmpId = g.Key, Ult = g.Max(r => (DateTime?)(r.UpdatedAt ?? r.CreatedAt)) })
            .ToListAsync();
        var ultDic = ultimasCargas.ToDictionary(x => x.EmpId, x => x.Ult);

        // Para mostrar el horario en la card del empleado: traemos el registro MÁS RECIENTE
        // de cada empleado y guardamos sus HoraEntrada/HoraSalida.
        var ultRegPorEmp = await _db.HorasExtrasRegistros
            .GroupBy(r => r.EmpleadoId)
            .Select(g => g.OrderByDescending(r => r.UpdatedAt ?? r.CreatedAt).First())
            .ToListAsync();
        var ultRegDic = ultRegPorEmp.ToDictionary(r => r.EmpleadoId, r => r);

        var result = emps.Select(e => {
            ultRegDic.TryGetValue(e.Id, out var ultReg);
            // 2026-06-02: Calcular acumulados de horario marcado vs jornada esperada
            var regsDelEmp = regs.Where(r => r.EmpleadoId == e.Id).ToList();
            var regsSemana = regsDelEmp.Where(r => r.Fecha >= inicioSemana).ToList();
            var regsMes = regsDelEmp.ToList();
            decimal trabSemana = regsSemana.Sum(r => HorasTrabajadas(r));
            decimal trabMes = regsMes.Sum(r => HorasTrabajadas(r));
            // Esperado = sumar HorasParaDia para cada dia del rango (independiente de si cargo o no)
            decimal espSemana = 0m;
            for (var d = inicioSemana; d <= hoy; d = d.AddDays(1)) espSemana += e.HorasParaDia(d.DayOfWeek);
            decimal espMes = 0m;
            for (var d = inicioMes; d <= hoy; d = d.AddDays(1)) espMes += e.HorasParaDia(d.DayOfWeek);
            decimal jornadaSem = e.HorasLunes + e.HorasMartes + e.HorasMiercoles + e.HorasJueves
                               + e.HorasViernes + e.HorasSabado + e.HorasDomingo;
            // 2026-06-03: ciclo de liquidacion del empleado (puede ser personalizado o mes calendario).
            // Para el panel admin, mostramos totales del CICLO para que coincidan con lo que el empleado ve.
            var ciclo = e.CicloActual(hoy);
            // Necesitamos registros del ciclo, que puede arrancar mas atras que inicioMes (ej. ciclo 16/05→15/06).
            // En la query 'regs' arriba solo trajimos desde inicioMes, asi que para el ciclo recalculamos a partir
            // de regsDelEmp acotado por las fechas del ciclo. Si el ciclo arranca antes del 1ro del mes, las que
            // falten se filtraran con "false" (no estan en la lista) — el ciclo entonces puede subestimar el inicio.
            // Para ser exactos, la query principal arriba trae 'desde inicioMes'. Para ciclos personalizados que
            // arrancan en el mes anterior, traemos extra:
            decimal trabCiclo, espCiclo;
            if (ciclo.Desde < inicioMes)
            {
                // Caso ciclo personalizado que arranca el mes anterior. No esta cubierto por 'regs'.
                // Calculamos directamente con un sum scoped a ese empleado y rango (se hace fuera del Select por
                // perf — pero como pasa solo para empleados con ciclo personalizado, lo dejamos).
                var regsCiclo = _db.HorasExtrasRegistros
                    .Where(r => r.EmpleadoId == e.Id && r.Fecha >= ciclo.Desde && r.Fecha <= ciclo.Hasta)
                    .ToList();
                trabCiclo = regsCiclo.Sum(r => HorasTrabajadas(r));
            }
            else
            {
                trabCiclo = regsDelEmp.Where(r => r.Fecha >= ciclo.Desde && r.Fecha <= ciclo.Hasta).Sum(r => HorasTrabajadas(r));
            }
            espCiclo = 0m;
            var hastaCiclo = ciclo.Hasta < hoy ? ciclo.Hasta : hoy;  // no contar dias futuros del ciclo
            for (var d = ciclo.Desde; d <= hastaCiclo; d = d.AddDays(1)) espCiclo += e.HorasParaDia(d.DayOfWeek);

            return new AdminEmpleadoDto(
                e.Id, e.Nombre, e.Token, e.IsActive,
                regs.Where(r => r.EmpleadoId == e.Id && r.Fecha == hoy).Sum(r => r.Cantidad),
                regs.Where(r => r.EmpleadoId == e.Id && r.Fecha >= inicioSemana).Sum(r => r.Cantidad),
                regs.Where(r => r.EmpleadoId == e.Id).Sum(r => r.Cantidad),
                ultDic.TryGetValue(e.Id, out var u) ? u : null,
                e.CreatedAt,
                FormatHora(ultReg?.HoraEntrada),
                FormatHora(ultReg?.HoraSalida),
                e.DniUltimos3,
                Slugify(e.Nombre),
                e.HorasLunes, e.HorasMartes, e.HorasMiercoles, e.HorasJueves,
                e.HorasViernes, e.HorasSabado, e.HorasDomingo,
                jornadaSem,
                trabSemana, espSemana, trabSemana - espSemana,
                trabMes, espMes, trabMes - espMes,
                e.MostrarExtrasAlEmpleado, e.CicloDiaInicio, e.CicloDiaFin,
                ciclo.Desde, ciclo.Hasta, ciclo.Label,
                trabCiclo, espCiclo, trabCiclo - espCiclo
            );
        }).ToList();
        return Ok(result);
    }

    /// <summary>2026-06-02: calcula cuantas horas trabajo el empleado segun entrada/salida marcadas.
    /// Si falta alguna, devuelve 0. Si la salida es antes que la entrada (cruzo medianoche), suma 24.</summary>
    private static decimal HorasTrabajadas(HorasExtrasRegistro r)
    {
        if (!r.HoraEntrada.HasValue || !r.HoraSalida.HasValue) return 0m;
        var dur = r.HoraSalida.Value - r.HoraEntrada.Value;
        if (dur.TotalHours < 0) dur += TimeSpan.FromHours(24);
        return Math.Round((decimal)dur.TotalHours, 2, MidpointRounding.AwayFromZero);
    }

    public class CreateEmpleadoRequest
    {
        public string Nombre { get; set; } = "";
        /// <summary>Últimos 3 dígitos del DNI — clave corta para el URL público.</summary>
        public string? DniUltimos3 { get; set; }
    }

    [HttpPost("admin/empleados")]
    [Authorize]
    public async Task<IActionResult> CreateEmpleado([FromBody] CreateEmpleadoRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre obligatorio" });
        var dni3 = NormDni3(req.DniUltimos3);
        // Validar que el slug + clave no esté ya tomado por otro empleado activo.
        if (dni3 is not null)
        {
            var slugNuevo = Slugify(req.Nombre);
            var colision = await _db.HorasExtrasEmpleados
                .Where(e => e.IsActive && e.DniUltimos3 == dni3)
                .ToListAsync();
            if (colision.Any(e => Slugify(e.Nombre) == slugNuevo))
                return BadRequest(new { error = "Ya existe un empleado activo con ese nombre + DNI" });
        }
        var emp = new HorasExtrasEmpleado
        {
            Nombre = req.Nombre.Trim(),
            Token = Guid.NewGuid().ToString("N"),
            DniUltimos3 = dni3,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.HorasExtrasEmpleados.Add(emp);
        await _db.SaveChangesAsync();
        return Ok(emp);
    }

    /// <summary>Normaliza los últimos 3 del DNI: trim, valida que sean 3 dígitos. Null o invalido → null.</summary>
    private static string? NormDni3(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.Length != 3 || !s.All(char.IsDigit)) return null;
        return s;
    }

    public class UpdateEmpleadoRequest
    {
        public string? Nombre { get; set; }
        public bool? IsActive { get; set; }
        public string? DniUltimos3 { get; set; }
        public bool ClearDniUltimos3 { get; set; }
        /// <summary>Si true, regenera el token (legacy — ya no se usa en el URL).</summary>
        public bool RegenerarToken { get; set; }
        // 2026-06-02: jornada laboral por dia de la semana (opcional — si no se manda, no se toca)
        public decimal? HorasLunes { get; set; }
        public decimal? HorasMartes { get; set; }
        public decimal? HorasMiercoles { get; set; }
        public decimal? HorasJueves { get; set; }
        public decimal? HorasViernes { get; set; }
        public decimal? HorasSabado { get; set; }
        public decimal? HorasDomingo { get; set; }
        // 2026-06-03: flag para mostrar extras al empleado + ciclo de liquidacion
        public bool? MostrarExtrasAlEmpleado { get; set; }
        /// <summary>Si vienen ambos > 0 -> ciclo personalizado. Si vienen 0 o null -> mes calendario.
        /// Si solo viene ClearCiclo=true, se borran los dos (vuelve a mes calendario).</summary>
        public int? CicloDiaInicio { get; set; }
        public int? CicloDiaFin { get; set; }
        public bool ClearCiclo { get; set; }
    }

    [HttpPut("admin/empleados/{id:int}")]
    [Authorize]
    public async Task<IActionResult> UpdateEmpleado(int id, [FromBody] UpdateEmpleadoRequest req)
    {
        var emp = await _db.HorasExtrasEmpleados.FindAsync(id);
        if (emp is null) return NotFound();
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre no puede ser vacío" });
            emp.Nombre = req.Nombre.Trim();
        }
        if (req.IsActive.HasValue) emp.IsActive = req.IsActive.Value;
        if (req.DniUltimos3 is not null) emp.DniUltimos3 = NormDni3(req.DniUltimos3);
        else if (req.ClearDniUltimos3) emp.DniUltimos3 = null;
        if (req.RegenerarToken) emp.Token = Guid.NewGuid().ToString("N");
        // 2026-06-02: jornada por dia. Si vino el valor, lo aplico (clampeado 0-24).
        decimal Clamp(decimal v) => Math.Max(0m, Math.Min(24m, v));
        if (req.HorasLunes.HasValue) emp.HorasLunes = Clamp(req.HorasLunes.Value);
        if (req.HorasMartes.HasValue) emp.HorasMartes = Clamp(req.HorasMartes.Value);
        if (req.HorasMiercoles.HasValue) emp.HorasMiercoles = Clamp(req.HorasMiercoles.Value);
        if (req.HorasJueves.HasValue) emp.HorasJueves = Clamp(req.HorasJueves.Value);
        if (req.HorasViernes.HasValue) emp.HorasViernes = Clamp(req.HorasViernes.Value);
        if (req.HorasSabado.HasValue) emp.HorasSabado = Clamp(req.HorasSabado.Value);
        if (req.HorasDomingo.HasValue) emp.HorasDomingo = Clamp(req.HorasDomingo.Value);
        // 2026-06-03: flag mostrar extras + ciclo de liquidacion
        if (req.MostrarExtrasAlEmpleado.HasValue) emp.MostrarExtrasAlEmpleado = req.MostrarExtrasAlEmpleado.Value;
        if (req.ClearCiclo)
        {
            emp.CicloDiaInicio = null;
            emp.CicloDiaFin = null;
        }
        else
        {
            // Solo se actualizan si vienen los DOS valores (uno solo no tiene sentido).
            // Validamos que esten en 1-31.
            if (req.CicloDiaInicio.HasValue && req.CicloDiaFin.HasValue)
            {
                if (req.CicloDiaInicio.Value < 1 || req.CicloDiaInicio.Value > 31)
                    return BadRequest(new { error = "CicloDiaInicio debe estar entre 1 y 31" });
                if (req.CicloDiaFin.Value < 1 || req.CicloDiaFin.Value > 31)
                    return BadRequest(new { error = "CicloDiaFin debe estar entre 1 y 31" });
                emp.CicloDiaInicio = req.CicloDiaInicio.Value;
                emp.CicloDiaFin = req.CicloDiaFin.Value;
            }
        }
        emp.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(emp);
    }

    [HttpDelete("admin/empleados/{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteEmpleado(int id)
    {
        var emp = await _db.HorasExtrasEmpleados.FindAsync(id);
        if (emp is null) return NotFound();
        _db.HorasExtrasEmpleados.Remove(emp);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public record AdminRegistroDto(int Id, int EmpleadoId, string EmpleadoNombre, DateTime Fecha,
        decimal Cantidad, string? Observaciones, string? HoraEntrada, string? HoraSalida,
        DateTime CreatedAt, DateTime? UpdatedAt,
        // 2026-06-02: calculado en backend para el reporte
        decimal HorasTrabajadas, decimal HorasEsperadas, decimal Diferencia, string DiaNombre);

    /// <summary>Lista todos los registros, opcionalmente filtrando por rango y/o empleado.
    /// Por default devuelve los últimos 30 días. Si se filtra por empleado SIN pasar desde/hasta,
    /// usa el CICLO DE LIQUIDACION del empleado (2026-06-03), asi los totales del panel coinciden
    /// con lo que el empleado ve en su celular.</summary>
    [HttpGet("admin/registros")]
    [Authorize]
    public async Task<IActionResult> ListRegistros([FromQuery] DateTime? desde = null,
        [FromQuery] DateTime? hasta = null, [FromQuery] int? empleadoId = null)
    {
        var hoy = FechaArgentinaHoy();
        DateTime d, h;
        if (empleadoId.HasValue && !desde.HasValue && !hasta.HasValue)
        {
            // 2026-06-03: filtro por empleado sin rango -> usar SU ciclo
            var empCiclo = await _db.HorasExtrasEmpleados.FindAsync(empleadoId.Value);
            if (empCiclo is not null)
            {
                var ciclo = empCiclo.CicloActual(hoy);
                d = ciclo.Desde;
                h = ciclo.Hasta < hoy ? hoy : ciclo.Hasta; // no contar dias futuros del ciclo
            }
            else { d = hoy.AddDays(-30).Date; h = hoy.Date; }
        }
        else
        {
            d = (desde ?? hoy.AddDays(-30)).Date;
            h = (hasta ?? hoy).Date;
        }

        var q = _db.HorasExtrasRegistros.Include(r => r.Empleado).AsQueryable();
        q = q.Where(r => r.Fecha >= d && r.Fecha <= h);
        if (empleadoId.HasValue) q = q.Where(r => r.EmpleadoId == empleadoId.Value);
        var regs = await q.OrderByDescending(r => r.Fecha).ThenBy(r => r.Empleado!.Nombre).ToListAsync();
        string[] diasNom = { "Domingo", "Lunes", "Martes", "Miércoles", "Jueves", "Viernes", "Sábado" };
        var result = regs.Select(r => {
            decimal trab = HorasTrabajadas(r);
            decimal esp = r.Empleado is null ? 0m : r.Empleado.HorasParaDia(r.Fecha.DayOfWeek);
            return new AdminRegistroDto(r.Id, r.EmpleadoId, r.Empleado?.Nombre ?? "?",
                r.Fecha, r.Cantidad, r.Observaciones, FormatHora(r.HoraEntrada), FormatHora(r.HoraSalida),
                r.CreatedAt, r.UpdatedAt,
                trab, esp, trab - esp, diasNom[(int)r.Fecha.DayOfWeek]);
        }).ToList();
        return Ok(result);
    }

    [HttpDelete("admin/registros/{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteRegistro(int id)
    {
        var r = await _db.HorasExtrasRegistros.FindAsync(id);
        if (r is null) return NotFound();
        _db.HorasExtrasRegistros.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    public class CargarManualRequest
    {
        public int EmpleadoId { get; set; }
        public DateTime Fecha { get; set; }
        public decimal Cantidad { get; set; }
        public string? Observaciones { get; set; }
    }

    /// <summary>Endpoint admin para cargar un registro manualmente (sin pasar por horario).
    /// Pensado para acumulados o correcciones. UPSERT por (EmpleadoId, Fecha).</summary>
    [HttpPost("admin/registros/manual")]
    [Authorize]
    public async Task<IActionResult> CargarManual([FromBody] CargarManualRequest req)
    {
        if (req.EmpleadoId <= 0) return BadRequest(new { error = "Empleado obligatorio" });
        if (req.Cantidad < 0 || req.Cantidad > 999) return BadRequest(new { error = "Cantidad inválida (0–999)" });

        var emp = await _db.HorasExtrasEmpleados.FindAsync(req.EmpleadoId);
        if (emp is null) return NotFound(new { error = "Empleado no existe" });

        var fecha = req.Fecha.Date;
        var existente = await _db.HorasExtrasRegistros.FirstOrDefaultAsync(r => r.EmpleadoId == emp.Id && r.Fecha == fecha);
        if (existente is null)
        {
            existente = new HorasExtrasRegistro
            {
                EmpleadoId = emp.Id,
                Fecha = fecha,
                Cantidad = req.Cantidad,
                Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? "Carga manual (admin)" : req.Observaciones.Trim(),
                CreatedAt = DateTime.UtcNow
            };
            _db.HorasExtrasRegistros.Add(existente);
        }
        else
        {
            existente.Cantidad = req.Cantidad;
            if (!string.IsNullOrWhiteSpace(req.Observaciones)) existente.Observaciones = req.Observaciones.Trim();
            existente.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ============================================================
    // Helpers
    // ============================================================

    /// <summary>Fecha "hoy" en horario Argentina (UTC-3).</summary>
    private static DateTime FechaArgentinaHoy()
    {
        return DateTime.UtcNow.AddHours(-3).Date;
    }
}
