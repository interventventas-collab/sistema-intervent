using Api.Data;
using Api.Models;
using Fido2NetLib;
using Fido2NetLib.Objects;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

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
    private readonly IFido2 _fido2;
    private readonly IMemoryCache _cache;
    public HorasExtrasController(AppDbContext db, IFido2 fido2, IMemoryCache cache)
    {
        _db = db;
        _fido2 = fido2;
        _cache = cache;
    }

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
        // MostrarExtras: si el admin tildo el checkbox para que el empleado vea sus extras (verde/rojo al lado del dia).
        // MostrarCuadroCiclo (v2): si el admin tildo el checkbox para que el empleado vea el cuadro grande del ciclo.
        // MostrarHorasTrabajadasDia (v2): si true, muestra "11,5 h" azul al lado de cada dia.
        DateTime CicloDesde, DateTime CicloHasta, string CicloLabel,
        decimal TotalCiclo, decimal ExtrasCiclo, bool MostrarExtras,
        bool MostrarCuadroCiclo, bool MostrarHorasTrabajadasDia);

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
            totalCiclo, extrasCiclo, emp.MostrarExtrasAlEmpleado,
            emp.MostrarCuadroCiclo, emp.MostrarHorasTrabajadasDia));
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
        // 2026-06-03: piloto modo nuevo. Datos opcionales que el frontend puede mandar.
        // Solo se usan si el modo nuevo esta activo. El tipo indica si esta marcacion es de
        // ENTRADA o SALIDA (lo nuevo que se marco en esta llamada), para saber qué metadata loguear.
        public string? TipoMarcacionNueva { get; set; } // "ENTRADA" | "SALIDA" | null (si nada nuevo)
        public decimal? GpsLat { get; set; }
        public decimal? GpsLon { get; set; }
        public int? GpsAccuracy { get; set; }
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
        var hoy = FechaArgentinaHoy();
        var fechaCarga = req.Fecha?.Date ?? hoy;
        if (fechaCarga != hoy)
            return BadRequest(new { error = "Solo podés cargar el día de hoy. Pedile al admin que corrija otros días." });

        // 2026-06-03: piloto modo nuevo de fichada. Activacion en 2 niveles:
        //   - GLOBAL (cfg.ActivarModoNuevo): aplica a todos los empleados
        //   - POR EMPLEADO (emp.ProbarModoNuevoFichada): solo este empleado pasa por validacion
        //                                                 (sirve para testear sin afectar a otros)
        // Si ninguno esta ON -> comportamiento exacto de antes (no validamos nada).
        var cfg = await _db.HorasExtrasConfigFichadas.FindAsync(1);
        bool globalActivo = cfg?.ActivarModoNuevo ?? false;
        bool flagEmpleado = emp.ProbarModoNuevoFichada;
        bool modoNuevoActivo = globalActivo || flagEmpleado;
        string? ipCliente = ResolverIpCliente();
        bool ipAutorizada = false;
        if (modoNuevoActivo)
        {
            var ipsPermitidas = new[] { cfg?.Wifi1Ip, cfg?.Wifi2Ip }
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim())
                .ToList();
            ipAutorizada = !string.IsNullOrEmpty(ipCliente) && ipsPermitidas.Any(p => p == ipCliente);
            if (ipsPermitidas.Count > 0 && !ipAutorizada)
            {
                return BadRequest(new {
                    error = $"Tenés que estar conectado al WiFi del negocio para fichar. (Tu IP: {ipCliente ?? "desconocida"})"
                });
            }
        }

        var horaEnt = ParseHora(req.HoraEntrada);
        var horaSal = ParseHora(req.HoraSalida);

        var existente = await _db.HorasExtrasRegistros.FirstOrDefaultAsync(r => r.EmpleadoId == emp.Id && r.Fecha == fechaCarga);
        // Detectar qué cambio (entrada/salida nuevos) para loguear meta solo de eso.
        var entradaAnterior = existente?.HoraEntrada;
        var salidaAnterior = existente?.HoraSalida;
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

        // 2026-06-03: log de metadata para auditoria (IP + GPS) — solo si modo nuevo activo.
        // Loguea solo si esta marcacion (entrada o salida) es realmente nueva (no se repite).
        if (modoNuevoActivo)
        {
            bool entradaCambio = entradaAnterior != horaEnt && horaEnt.HasValue;
            bool salidaCambio = salidaAnterior != horaSal && horaSal.HasValue;
            if (entradaCambio)
            {
                _db.HorasExtrasFichadaMetas.Add(new HorasExtrasFichadaMeta
                {
                    RegistroId = existente.Id,
                    Tipo = "ENTRADA",
                    Ip = ipCliente,
                    IpAutorizada = ipAutorizada,
                    Lat = req.GpsLat,
                    Lon = req.GpsLon,
                    GpsAccuracyMeters = req.GpsAccuracy,
                    HuellaVerificada = false,  // Fase 2
                    UsoFallbackPin = true       // por ahora siempre fallback PIN
                });
            }
            if (salidaCambio)
            {
                _db.HorasExtrasFichadaMetas.Add(new HorasExtrasFichadaMeta
                {
                    RegistroId = existente.Id,
                    Tipo = "SALIDA",
                    Ip = ipCliente,
                    IpAutorizada = ipAutorizada,
                    Lat = req.GpsLat,
                    Lon = req.GpsLon,
                    GpsAccuracyMeters = req.GpsAccuracy,
                    HuellaVerificada = false,
                    UsoFallbackPin = true
                });
            }
            if (entradaCambio || salidaCambio) await _db.SaveChangesAsync();
        }

        return Ok(new { ok = true, fecha = fechaCarga, cantidad = req.Cantidad });
    }

    /// <summary>Resuelve la IP publica del cliente de la request. Cuando esta detras de Caddy/Nginx
    /// (prod), llega en X-Forwarded-For. Si no, toma la IP remota directa.</summary>
    private string? ResolverIpCliente()
    {
        var xff = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(xff)) return xff.Split(',')[0].Trim();
        return Request.HttpContext.Connection.RemoteIpAddress?.ToString();
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
        decimal TrabajadoCiclo, decimal EsperadoCiclo, decimal DiferenciaCiclo,
        // 2026-06-03 v2: flags granulares de visibilidad
        bool MostrarCuadroCiclo, bool MostrarHorasTrabajadasDia,
        // 2026-06-03 v3: flag para probar el modo nuevo de fichada solo en este empleado
        bool ProbarModoNuevoFichada,
        // 2026-06-03 v4: flag para mostrar/ocultar este empleado en el kiosco /fichador
        bool MostrarEnFichador);

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
                trabCiclo, espCiclo, trabCiclo - espCiclo,
                e.MostrarCuadroCiclo, e.MostrarHorasTrabajadasDia,
                e.ProbarModoNuevoFichada,
                e.MostrarEnFichador
            );
        }).ToList();
        return Ok(result);
    }

    /// <summary>2026-06-02: calcula cuantas horas trabajo el empleado segun entrada/salida marcadas.
    /// Si falta alguna, devuelve 0. Si la salida es antes que la entrada (cruzo medianoche), suma 24.</summary>
    private static decimal HorasTrabajadas(HorasExtrasRegistro r)
    {
        // 2026-06-06: si no hay horario marcado pero hay carga manual (Cantidad > 0),
        // tratamos Cantidad como horas trabajadas del día. La "Carga manual" desde la UI
        // guarda en este campo cuando el empleado no fichó (ej: lunes que no fichó pero
        // el admin le pone 12.5h manualmente).
        if (!r.HoraEntrada.HasValue || !r.HoraSalida.HasValue) return r.Cantidad;
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
        // 2026-06-03 v2: flags granulares de visibilidad
        public bool? MostrarCuadroCiclo { get; set; }
        public bool? MostrarHorasTrabajadasDia { get; set; }
        // 2026-06-03 v3: piloto - flag por empleado para probar el modo nuevo
        public bool? ProbarModoNuevoFichada { get; set; }
        // 2026-06-03 v4: mostrar este empleado en el kiosco /fichador
        public bool? MostrarEnFichador { get; set; }
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
        // 2026-06-03 v2: flags granulares de visibilidad
        if (req.MostrarCuadroCiclo.HasValue) emp.MostrarCuadroCiclo = req.MostrarCuadroCiclo.Value;
        if (req.MostrarHorasTrabajadasDia.HasValue) emp.MostrarHorasTrabajadasDia = req.MostrarHorasTrabajadasDia.Value;
        if (req.ProbarModoNuevoFichada.HasValue) emp.ProbarModoNuevoFichada = req.ProbarModoNuevoFichada.Value;
        if (req.MostrarEnFichador.HasValue) emp.MostrarEnFichador = req.MostrarEnFichador.Value;
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

    /// <summary>2026-06-08: request para editar manualmente entrada/salida/observación.
    /// HoraEntrada y HoraSalida son strings "HH:mm" o null. Si HoraEntrada > HoraSalida,
    /// asumimos que la jornada cruzó medianoche (no validamos eso, solo guardamos).
    /// Cantidad se recalcula automáticamente desde el horario (hsEsperadas se calculan en GET).</summary>
    public class EditarRegistroRequest
    {
        public string? HoraEntrada { get; set; }
        public string? HoraSalida { get; set; }
        public string? Observaciones { get; set; }
        public decimal? Cantidad { get; set; }
    }

    [HttpPut("admin/registros/{id:int}")]
    [Authorize]
    public async Task<IActionResult> UpdateRegistro(int id, [FromBody] EditarRegistroRequest req)
    {
        var r = await _db.HorasExtrasRegistros.FindAsync(id);
        if (r is null) return NotFound();

        // Parsear horas (opcionales). Formato "HH:mm" o "HH:mm:ss".
        TimeSpan? parseHora(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (TimeSpan.TryParse(s, out var t)) return t;
            return null;
        }
        r.HoraEntrada = parseHora(req.HoraEntrada);
        r.HoraSalida = parseHora(req.HoraSalida);

        // Si vinieron las dos, recalculamos Cantidad = horas trabajadas (puede cruzar medianoche)
        if (r.HoraEntrada.HasValue && r.HoraSalida.HasValue)
        {
            var diff = r.HoraSalida.Value - r.HoraEntrada.Value;
            if (diff.TotalMinutes < 0) diff = diff.Add(TimeSpan.FromHours(24));  // pasó medianoche
            r.Cantidad = (decimal)diff.TotalHours;
        }
        else if (req.Cantidad.HasValue)
        {
            r.Cantidad = req.Cantidad.Value;
        }

        // Observaciones (siempre se actualiza con lo que vino, aunque sea null)
        r.Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim();

        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, cantidad = r.Cantidad });
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

    // ============================================================
    // 2026-06-03: Endpoints de configuracion del modo nuevo de fichada
    // ============================================================

    public record ConfigFichadaDto(bool ActivarModoNuevo, string? Wifi1Ip, string? Wifi1Label,
        string? Wifi2Ip, string? Wifi2Label, bool RequiereHuella, bool LoguearGps,
        DateTime? UpdatedAt, string? UpdatedBy);

    [HttpGet("admin/config-fichada")]
    [Authorize]
    public async Task<IActionResult> GetConfigFichada()
    {
        var cfg = await _db.HorasExtrasConfigFichadas.FindAsync(1);
        if (cfg is null)
        {
            cfg = new HorasExtrasConfigFichada { Id = 1 };
            _db.HorasExtrasConfigFichadas.Add(cfg);
            await _db.SaveChangesAsync();
        }
        return Ok(new ConfigFichadaDto(cfg.ActivarModoNuevo, cfg.Wifi1Ip, cfg.Wifi1Label,
            cfg.Wifi2Ip, cfg.Wifi2Label, cfg.RequiereHuella, cfg.LoguearGps, cfg.UpdatedAt, cfg.UpdatedBy));
    }

    public class UpdateConfigFichadaRequest
    {
        public bool? ActivarModoNuevo { get; set; }
        public string? Wifi1Ip { get; set; }
        public string? Wifi1Label { get; set; }
        public string? Wifi2Ip { get; set; }
        public string? Wifi2Label { get; set; }
        public bool? RequiereHuella { get; set; }
        public bool? LoguearGps { get; set; }
        public string? UpdatedBy { get; set; }
    }

    [HttpPut("admin/config-fichada")]
    [Authorize]
    public async Task<IActionResult> UpdateConfigFichada([FromBody] UpdateConfigFichadaRequest req)
    {
        var cfg = await _db.HorasExtrasConfigFichadas.FindAsync(1);
        if (cfg is null) { cfg = new HorasExtrasConfigFichada { Id = 1 }; _db.HorasExtrasConfigFichadas.Add(cfg); }

        if (req.Wifi1Ip is not null) cfg.Wifi1Ip = string.IsNullOrWhiteSpace(req.Wifi1Ip) ? null : req.Wifi1Ip.Trim();
        if (req.Wifi1Label is not null) cfg.Wifi1Label = string.IsNullOrWhiteSpace(req.Wifi1Label) ? null : req.Wifi1Label.Trim();
        if (req.Wifi2Ip is not null) cfg.Wifi2Ip = string.IsNullOrWhiteSpace(req.Wifi2Ip) ? null : req.Wifi2Ip.Trim();
        if (req.Wifi2Label is not null) cfg.Wifi2Label = string.IsNullOrWhiteSpace(req.Wifi2Label) ? null : req.Wifi2Label.Trim();
        if (req.RequiereHuella.HasValue) cfg.RequiereHuella = req.RequiereHuella.Value;
        if (req.LoguearGps.HasValue) cfg.LoguearGps = req.LoguearGps.Value;

        // VALIDACION: no se puede activar el modo nuevo si no hay al menos UNA IP configurada.
        // Asi evitamos que se prenda sin querer y bloquee a todos.
        if (req.ActivarModoNuevo == true)
        {
            if (string.IsNullOrWhiteSpace(cfg.Wifi1Ip) && string.IsNullOrWhiteSpace(cfg.Wifi2Ip))
                return BadRequest(new { error = "No podés activar el modo nuevo sin al menos una IP de WiFi configurada. Capturá la IP del WiFi del negocio primero." });
            cfg.ActivarModoNuevo = true;
        }
        else if (req.ActivarModoNuevo == false)
        {
            cfg.ActivarModoNuevo = false;
        }

        cfg.UpdatedAt = DateTime.UtcNow;
        cfg.UpdatedBy = req.UpdatedBy?.Trim();
        await _db.SaveChangesAsync();
        return Ok(new ConfigFichadaDto(cfg.ActivarModoNuevo, cfg.Wifi1Ip, cfg.Wifi1Label,
            cfg.Wifi2Ip, cfg.Wifi2Label, cfg.RequiereHuella, cfg.LoguearGps, cfg.UpdatedAt, cfg.UpdatedBy));
    }

    /// <summary>Devuelve la IP publica del admin que esta llamando este endpoint. Sirve para
    /// que el admin (conectado al WiFi del negocio) capture la IP autorizada con un click.</summary>
    [HttpGet("admin/config-fichada/mi-ip")]
    [Authorize]
    public IActionResult MiIpActual()
    {
        return Ok(new { ip = ResolverIpCliente() });
    }

    // ============================================================
    // 2026-06-03: Endpoints del KIOSCO de fichada (/fichador)
    // El kiosco es una pantalla PUBLICA (sin login) pensada para correr en un celular
    // o tablet pegada a la pared del negocio. Los empleados tocan su nombre + PIN
    // (futuro: huella) para fichar. Misma logica de validacion de IP/GPS que el link individual.
    // ============================================================

    // 2026-06-03: el kiosco YA NO expone horarios (privacidad).
    // Solo color/estado: rojo (sin fichar), verde (trabajando), azul (salio).
    // Hora exacta solo se ve desde el admin /horas-extras.
    public record FichadorEmpleadoDto(int Id, string Nombre, string Slug,
        string Estado /* "sin-fichar" | "trabajando" | "salio" */,
        bool TieneHuella);

    /// <summary>Lista de empleados activos para mostrar en la pantalla del kiosco. Sin auth porque
    /// el kiosco no tiene sesion. Devuelve nombre, slug y estado de hoy (sin horas exactas).</summary>
    [HttpGet("fichador/empleados")]
    [AllowAnonymous]
    public async Task<IActionResult> FichadorEmpleados()
    {
        var hoy = FechaArgentinaHoy();
        // 2026-06-03 v4: solo empleados con MostrarEnFichador=true (el admin elige cuales)
        var emps = await _db.HorasExtrasEmpleados.Where(e => e.IsActive && e.MostrarEnFichador).OrderBy(e => e.Nombre).ToListAsync();
        var regsHoy = await _db.HorasExtrasRegistros.Where(r => r.Fecha == hoy).ToListAsync();
        var regDic = regsHoy.ToDictionary(r => r.EmpleadoId);
        // Set de empleadoIds con al menos una huella registrada
        var conHuella = await _db.HorasExtrasWebAuthnCredentials.Select(c => c.EmpleadoId).Distinct().ToListAsync();
        var conHuellaSet = conHuella.ToHashSet();

        var result = emps.Select(e =>
        {
            regDic.TryGetValue(e.Id, out var r);
            string estado;
            if (r is null || !r.HoraEntrada.HasValue) estado = "sin-fichar";
            else if (r.HoraSalida.HasValue) estado = "salio";
            else estado = "trabajando";
            return new FichadorEmpleadoDto(e.Id, e.Nombre, Slugify(e.Nombre), estado, conHuellaSet.Contains(e.Id));
        }).ToList();
        return Ok(result);
    }

    public class FichadorMarcarRequest
    {
        public int EmpleadoId { get; set; }
        public string Pin { get; set; } = "";
        // 2026-06-03: GPS opcional (se guarda en HorasExtras_FichadaMeta para auditoria)
        public decimal? GpsLat { get; set; }
        public decimal? GpsLon { get; set; }
        public int? GpsAccuracy { get; set; }
    }

    public record FichadorMarcarResult(bool Ok, string? Mensaje, string? Tipo, string? Hora, string? NombreEmpleado);

    /// <summary>Marca entrada o salida desde el kiosco. Detecta automaticamente cual segun
    /// si ya hay entrada cargada hoy. Valida PIN (3 ultimos del DNI). Respeta config de WiFi
    /// si el modo nuevo esta activo.</summary>
    [HttpPost("fichador/marcar")]
    [AllowAnonymous]
    public async Task<IActionResult> FichadorMarcar([FromBody] FichadorMarcarRequest req)
    {
        var emp = await _db.HorasExtrasEmpleados.FirstOrDefaultAsync(e => e.Id == req.EmpleadoId && e.IsActive);
        if (emp is null) return Ok(new FichadorMarcarResult(false, "Empleado no encontrado", null, null, null));
        if (string.IsNullOrEmpty(req.Pin) || req.Pin.Trim() != (emp.DniUltimos3 ?? ""))
            return Ok(new FichadorMarcarResult(false, "PIN incorrecto", null, null, emp.Nombre));

        // Validacion WiFi (mismo flujo que el endpoint del link individual)
        var cfg = await _db.HorasExtrasConfigFichadas.FindAsync(1);
        bool globalActivo = cfg?.ActivarModoNuevo ?? false;
        bool flagEmpleado = emp.ProbarModoNuevoFichada;
        bool modoNuevoActivo = globalActivo || flagEmpleado;
        string? ipCliente = ResolverIpCliente();
        bool ipAutorizada = false;
        if (modoNuevoActivo)
        {
            var ipsPermitidas = new[] { cfg?.Wifi1Ip, cfg?.Wifi2Ip }
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToList();
            ipAutorizada = !string.IsNullOrEmpty(ipCliente) && ipsPermitidas.Any(p => p == ipCliente);
            if (ipsPermitidas.Count > 0 && !ipAutorizada)
                return Ok(new FichadorMarcarResult(false,
                    $"Tenés que estar conectado al WiFi del negocio para fichar. (IP: {ipCliente ?? "?"})",
                    null, null, emp.Nombre));
        }

        var hoy = FechaArgentinaHoy();
        var ahora = DateTime.UtcNow.AddHours(-3); // ART
        var horaActual = new TimeSpan(ahora.Hour, ahora.Minute, 0);

        var existente = await _db.HorasExtrasRegistros
            .FirstOrDefaultAsync(r => r.EmpleadoId == emp.Id && r.Fecha == hoy);

        string tipo;
        if (existente is null)
        {
            // Primera marcacion del dia = ENTRADA
            existente = new HorasExtrasRegistro
            {
                EmpleadoId = emp.Id,
                Fecha = hoy,
                Cantidad = 0m,
                HoraEntrada = horaActual,
                CreatedAt = DateTime.UtcNow
            };
            _db.HorasExtrasRegistros.Add(existente);
            tipo = "ENTRADA";
        }
        else if (!existente.HoraEntrada.HasValue)
        {
            existente.HoraEntrada = horaActual;
            existente.UpdatedAt = DateTime.UtcNow;
            tipo = "ENTRADA";
        }
        else if (!existente.HoraSalida.HasValue)
        {
            existente.HoraSalida = horaActual;
            existente.UpdatedAt = DateTime.UtcNow;
            tipo = "SALIDA";
        }
        else
        {
            // Ya tiene entrada y salida del dia. Sobreescribimos la salida (probablemente se fue, volvio, se va de nuevo).
            existente.HoraSalida = horaActual;
            existente.UpdatedAt = DateTime.UtcNow;
            tipo = "SALIDA";
        }
        await _db.SaveChangesAsync();

        // Loguear meta (IP + GPS) si el modo nuevo esta activo
        if (modoNuevoActivo)
        {
            _db.HorasExtrasFichadaMetas.Add(new HorasExtrasFichadaMeta
            {
                RegistroId = existente.Id,
                Tipo = tipo,
                Ip = ipCliente,
                IpAutorizada = ipAutorizada,
                Lat = req.GpsLat,
                Lon = req.GpsLon,
                GpsAccuracyMeters = req.GpsAccuracy,
                HuellaVerificada = false,
                UsoFallbackPin = true   // todavia no implementamos huella en el kiosco; siempre PIN
            });
            await _db.SaveChangesAsync();
        }

        var horaTxt = $"{horaActual.Hours:D2}:{horaActual.Minutes:D2}";
        var saludo = tipo == "ENTRADA" ? $"¡Buen día {emp.Nombre}!" : $"¡Hasta luego {emp.Nombre}!";
        return Ok(new FichadorMarcarResult(true, saludo, tipo, horaTxt, emp.Nombre));
    }

    // ============================================================
    // 2026-06-03: WebAuthn (huella biometrica) para el fichador
    // ============================================================
    // Flujo:
    // 1) Registro:  begin → browser muestra prompt huella → complete → guardamos credencial.
    //    Requiere PIN + empleadoId para asociar la huella al empleado correcto.
    // 2) Login:     begin (sin user) → browser pregunta cual huella → complete → identificamos
    //    empleado por la credencial y marcamos entrada/salida automaticamente.

    public class HuellaRegistroBeginRequest
    {
        public int EmpleadoId { get; set; }
        public string Pin { get; set; } = "";
        public string? DeviceName { get; set; }
    }

    public record HuellaRegistroBeginResult(bool Ok, string? Mensaje, CredentialCreateOptions? Options, string? SessionId);

    [HttpPost("fichador/huella/registro/begin")]
    [AllowAnonymous]
    public async Task<IActionResult> HuellaRegistroBegin([FromBody] HuellaRegistroBeginRequest req)
    {
        var emp = await _db.HorasExtrasEmpleados.FirstOrDefaultAsync(e => e.Id == req.EmpleadoId && e.IsActive);
        if (emp is null) return Ok(new HuellaRegistroBeginResult(false, "Empleado no encontrado", null, null));
        if (string.IsNullOrEmpty(req.Pin) || req.Pin.Trim() != (emp.DniUltimos3 ?? ""))
            return Ok(new HuellaRegistroBeginResult(false, "PIN incorrecto. No se puede registrar huella sin validar identidad.", null, null));

        // UserHandle determinista por empleado (UTF8 del Id). Asi el browser sabe que es el mismo "user"
        // si se intenta registrar de nuevo y puede pisar credenciales viejas si se quiere.
        var userHandle = System.Text.Encoding.UTF8.GetBytes($"emp-{emp.Id}");
        var user = new Fido2User
        {
            Id = userHandle,
            Name = $"empleado-{emp.Id}",
            DisplayName = emp.Nombre
        };

        // Credenciales que ya tenemos para este empleado (para que el browser no las re-registre).
        var existentes = await _db.HorasExtrasWebAuthnCredentials.Where(c => c.EmpleadoId == emp.Id).ToListAsync();
        var excludeList = existentes.Select(c => new PublicKeyCredentialDescriptor(Convert.FromBase64String(c.CredentialId))).ToList();

        var authSelection = new AuthenticatorSelection
        {
            UserVerification = UserVerificationRequirement.Required, // exige huella o PIN del dispositivo
            AuthenticatorAttachment = AuthenticatorAttachment.Platform // huella del celu, no llavero externo
        };

        var options = _fido2.RequestNewCredential(user, excludeList, authSelection, AttestationConveyancePreference.None);

        // Guardamos las options en cache (ID = guid corto) para validar en el complete.
        var sessionId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _cache.Set($"webauthn:reg:{sessionId}", options.ToJson(), TimeSpan.FromMinutes(5));
        _cache.Set($"webauthn:reg:{sessionId}:empId", emp.Id, TimeSpan.FromMinutes(5));
        _cache.Set($"webauthn:reg:{sessionId}:device", req.DeviceName ?? "Dispositivo sin nombre", TimeSpan.FromMinutes(5));

        return Ok(new HuellaRegistroBeginResult(true, null, options, sessionId));
    }

    public class HuellaRegistroCompleteRequest
    {
        public string SessionId { get; set; } = "";
        public AuthenticatorAttestationRawResponse AttestationResponse { get; set; } = null!;
    }

    public record HuellaRegistroCompleteResult(bool Ok, string? Mensaje, string? DeviceName);

    [HttpPost("fichador/huella/registro/complete")]
    [AllowAnonymous]
    public async Task<IActionResult> HuellaRegistroComplete([FromBody] HuellaRegistroCompleteRequest req)
    {
        if (!_cache.TryGetValue<string>($"webauthn:reg:{req.SessionId}", out var optionsJson) || optionsJson is null)
            return Ok(new HuellaRegistroCompleteResult(false, "Sesion expirada. Volve a intentar.", null));
        if (!_cache.TryGetValue<int>($"webauthn:reg:{req.SessionId}:empId", out var empId))
            return Ok(new HuellaRegistroCompleteResult(false, "Sesion expirada (sin empleado).", null));
        var deviceName = _cache.Get<string>($"webauthn:reg:{req.SessionId}:device") ?? "Dispositivo";

        var options = CredentialCreateOptions.FromJson(optionsJson);

        // Callback: chequear que el CredentialId no este ya registrado.
        IsCredentialIdUniqueToUserAsyncDelegate callback = async (args, ct) =>
        {
            var b64 = Convert.ToBase64String(args.CredentialId);
            return !await _db.HorasExtrasWebAuthnCredentials.AnyAsync(c => c.CredentialId == b64, ct);
        };

        try
        {
            var success = await _fido2.MakeNewCredentialAsync(req.AttestationResponse, options, callback);
            if (success.Result is null)
                return Ok(new HuellaRegistroCompleteResult(false, "No se pudo registrar la huella.", null));

            var cred = new HorasExtrasWebAuthnCredential
            {
                EmpleadoId = empId,
                CredentialId = Convert.ToBase64String(success.Result.CredentialId),
                PublicKey = Convert.ToBase64String(success.Result.PublicKey),
                UserHandle = Convert.ToBase64String(success.Result.User.Id),
                AaGuid = success.Result.Aaguid.ToString(),
                SignatureCounter = success.Result.Counter,
                DeviceName = deviceName,
                CreatedAt = DateTime.UtcNow
            };
            _db.HorasExtrasWebAuthnCredentials.Add(cred);
            await _db.SaveChangesAsync();

            // Limpio cache
            _cache.Remove($"webauthn:reg:{req.SessionId}");
            _cache.Remove($"webauthn:reg:{req.SessionId}:empId");
            _cache.Remove($"webauthn:reg:{req.SessionId}:device");

            return Ok(new HuellaRegistroCompleteResult(true, "Huella registrada!", deviceName));
        }
        catch (Fido2VerificationException ex)
        {
            return Ok(new HuellaRegistroCompleteResult(false, $"Error de verificacion: {ex.Message}", null));
        }
    }

    public record HuellaLoginBeginResult(bool Ok, string? Mensaje, AssertionOptions? Options, string? SessionId);

    [HttpPost("fichador/huella/login/begin")]
    [AllowAnonymous]
    public async Task<IActionResult> HuellaLoginBegin([FromQuery] int? empleadoId = null)
    {
        // Si llega empleadoId, filtramos a las credenciales de ese empleado (UX: toco mi nombre y solo
        // me pide MI huella). Si no, devolvemos todas las credenciales del sistema (modo "boton global").
        var query = _db.HorasExtrasWebAuthnCredentials.AsQueryable();
        if (empleadoId.HasValue) query = query.Where(c => c.EmpleadoId == empleadoId.Value);
        var creds = await query.ToListAsync();
        var allowed = creds.Select(c => new PublicKeyCredentialDescriptor(Convert.FromBase64String(c.CredentialId))).ToList();

        if (allowed.Count == 0)
        {
            var msg = empleadoId.HasValue
                ? "Ese empleado todavia no registro su huella en este sistema."
                : "Todavia no hay huellas registradas en este sistema.";
            return Ok(new HuellaLoginBeginResult(false, msg, null, null));
        }

        var options = _fido2.GetAssertionOptions(allowed, UserVerificationRequirement.Required);

        var sessionId = Guid.NewGuid().ToString("N").Substring(0, 16);
        _cache.Set($"webauthn:auth:{sessionId}", options.ToJson(), TimeSpan.FromMinutes(5));

        return Ok(new HuellaLoginBeginResult(true, null, options, sessionId));
    }

    public class HuellaLoginCompleteRequest
    {
        public string SessionId { get; set; } = "";
        public AuthenticatorAssertionRawResponse AssertionResponse { get; set; } = null!;
        // GPS opcional, igual que en el endpoint con PIN
        public decimal? GpsLat { get; set; }
        public decimal? GpsLon { get; set; }
        public decimal? GpsAccuracy { get; set; }
    }

    [HttpPost("fichador/huella/login/complete")]
    [AllowAnonymous]
    public async Task<IActionResult> HuellaLoginComplete([FromBody] HuellaLoginCompleteRequest req)
    {
        if (!_cache.TryGetValue<string>($"webauthn:auth:{req.SessionId}", out var optionsJson) || optionsJson is null)
            return Ok(new FichadorMarcarResult(false, "Sesion expirada. Volve a intentar.", null, null, null));
        var options = AssertionOptions.FromJson(optionsJson);

        // Ubicar la credencial por su CredentialId
        var credIdB64 = Convert.ToBase64String(req.AssertionResponse.Id);
        var cred = await _db.HorasExtrasWebAuthnCredentials
            .Include(c => c.Empleado)
            .FirstOrDefaultAsync(c => c.CredentialId == credIdB64);
        if (cred is null || cred.Empleado is null || !cred.Empleado.IsActive)
            return Ok(new FichadorMarcarResult(false, "Huella no reconocida.", null, null, null));

        IsUserHandleOwnerOfCredentialIdAsync callback = async (args, ct) =>
        {
            var b64 = Convert.ToBase64String(args.CredentialId);
            return await _db.HorasExtrasWebAuthnCredentials.AnyAsync(c => c.CredentialId == b64
                && c.UserHandle == Convert.ToBase64String(args.UserHandle), ct);
        };

        AssertionVerificationResult verifyResult;
        try
        {
            verifyResult = await _fido2.MakeAssertionAsync(
                req.AssertionResponse,
                options,
                Convert.FromBase64String(cred.PublicKey),
                cred.SignatureCounter,
                callback);
        }
        catch (Fido2VerificationException ex)
        {
            return Ok(new FichadorMarcarResult(false, $"Error verificando huella: {ex.Message}", null, null, cred.Empleado.Nombre));
        }

        // Actualizar contador para detectar clones
        cred.SignatureCounter = verifyResult.Counter;
        cred.LastUsedAt = DateTime.UtcNow;

        var emp = cred.Empleado;
        _cache.Remove($"webauthn:auth:{req.SessionId}");

        // Validacion WiFi (misma que en el endpoint PIN)
        var cfg = await _db.HorasExtrasConfigFichadas.FindAsync(1);
        bool modoNuevoActivo = (cfg?.ActivarModoNuevo ?? false) || emp.ProbarModoNuevoFichada;
        string? ipCliente = ResolverIpCliente();
        bool ipAutorizada = false;
        if (modoNuevoActivo)
        {
            var ipsPermitidas = new[] { cfg?.Wifi1Ip, cfg?.Wifi2Ip }
                .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToList();
            ipAutorizada = !string.IsNullOrEmpty(ipCliente) && ipsPermitidas.Any(p => p == ipCliente);
            if (ipsPermitidas.Count > 0 && !ipAutorizada)
                return Ok(new FichadorMarcarResult(false,
                    $"Tenés que estar conectado al WiFi del negocio para fichar. (IP: {ipCliente ?? "?"})",
                    null, null, emp.Nombre));
        }

        // Marcar entrada/salida igual que FichadorMarcar
        var hoy = FechaArgentinaHoy();
        var ahora = DateTime.UtcNow.AddHours(-3);
        var horaActual = new TimeSpan(ahora.Hour, ahora.Minute, 0);
        var existente = await _db.HorasExtrasRegistros.FirstOrDefaultAsync(r => r.EmpleadoId == emp.Id && r.Fecha == hoy);

        string tipo;
        if (existente is null)
        {
            existente = new HorasExtrasRegistro { EmpleadoId = emp.Id, Fecha = hoy, Cantidad = 0m, HoraEntrada = horaActual, CreatedAt = DateTime.UtcNow };
            _db.HorasExtrasRegistros.Add(existente);
            tipo = "ENTRADA";
        }
        else if (!existente.HoraEntrada.HasValue) { existente.HoraEntrada = horaActual; existente.UpdatedAt = DateTime.UtcNow; tipo = "ENTRADA"; }
        else if (!existente.HoraSalida.HasValue) { existente.HoraSalida = horaActual; existente.UpdatedAt = DateTime.UtcNow; tipo = "SALIDA"; }
        else { existente.HoraSalida = horaActual; existente.UpdatedAt = DateTime.UtcNow; tipo = "SALIDA"; }
        await _db.SaveChangesAsync();

        if (modoNuevoActivo)
        {
            _db.HorasExtrasFichadaMetas.Add(new HorasExtrasFichadaMeta
            {
                RegistroId = existente.Id, Tipo = tipo,
                Ip = ipCliente, IpAutorizada = ipAutorizada,
                Lat = req.GpsLat, Lon = req.GpsLon, GpsAccuracyMeters = (int?)req.GpsAccuracy,
                HuellaVerificada = true, UsoFallbackPin = false
            });
            await _db.SaveChangesAsync();
        }

        var horaTxt = $"{horaActual.Hours:D2}:{horaActual.Minutes:D2}";
        var saludo = tipo == "ENTRADA" ? $"¡Buen día {emp.Nombre}!" : $"¡Hasta luego {emp.Nombre}!";
        return Ok(new FichadorMarcarResult(true, saludo, tipo, horaTxt, emp.Nombre));
    }

    // Endpoint auxiliar: lista las huellas registradas (admin)
    public record HuellaRegistradaDto(int Id, int EmpleadoId, string EmpleadoNombre, string? DeviceName, DateTime CreatedAt, DateTime? LastUsedAt);

    [HttpGet("huellas")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> ListHuellas()
    {
        var creds = await _db.HorasExtrasWebAuthnCredentials
            .Include(c => c.Empleado)
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new HuellaRegistradaDto(c.Id, c.EmpleadoId, c.Empleado!.Nombre, c.DeviceName, c.CreatedAt, c.LastUsedAt))
            .ToListAsync();
        return Ok(creds);
    }

    [HttpDelete("huellas/{id}")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> DeleteHuella(int id)
    {
        var cred = await _db.HorasExtrasWebAuthnCredentials.FindAsync(id);
        if (cred is null) return NotFound();
        _db.HorasExtrasWebAuthnCredentials.Remove(cred);
        await _db.SaveChangesAsync();
        return Ok();
    }
}
