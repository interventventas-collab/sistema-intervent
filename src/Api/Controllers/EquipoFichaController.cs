using System.Globalization;
using System.Text;
using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-13: "Equipo hoy" → ficha completa de cada empleado (pedido del dueño).
/// Junta lo que cada persona hizo en el sistema (ventas, repartos, preparación, cobranzas,
/// stock, tareas del depósito y fichadas) + plata, vacaciones, documentos y sus links.
///
/// El "quién" sale del nombre de operador que queda en cada tabla (GERMAN, repartidor: GABRIEL,
/// Nacho…). Lo que se hace con los usuarios compartidos OFICINA/DEPOSITO sin elegir operador
/// NO se le puede atribuir a nadie — por eso no aparece.
///
/// Solo el usuario "admin" (el dueño): tiene sueldos y datos personales. OFICINA también tiene
/// rol admin, así que se chequea el nombre de usuario, no el rol.
/// </summary>
[ApiController]
[Route("api/equipo-ficha")]
[Authorize]
public class EquipoFichaController : ControllerBase
{
    private readonly AppDbContext _db;
    public EquipoFichaController(AppDbContext db) => _db = db;

    private bool EsDueno => string.Equals(User.Identity?.Name, "admin", StringComparison.OrdinalIgnoreCase);

    public record MovDto(int EmpId, DateTime CuandoUtc, string Texto, string Donde, string Clase);
    public record UltimoDto(int EmpId, DateTime CuandoUtc, string Texto, string Donde);

    // Tipos de movimiento de stock que son a mano (las ventas descuentan solas, no cuentan).
    private static readonly string[] StockAMano = { "SUMA", "RESTA", "SET", "CONTEO", "AJUSTE_ADMIN", "DEVUELTO_VENTA" };

    /// <summary>Último movimiento de cada empleado activo (últimos 7 días).</summary>
    [HttpGet("ultimos")]
    public async Task<IActionResult> Ultimos()
    {
        if (!EsDueno) return Forbid();
        var hasta = DateTime.UtcNow.AddMinutes(1);
        var movs = await CargarMovsAsync(hasta.AddDays(-7), hasta, null);
        var res = movs.GroupBy(m => m.EmpId)
            .Select(g => g.OrderByDescending(m => m.CuandoUtc).First())
            .Select(m => new UltimoDto(m.EmpId, m.CuandoUtc, m.Texto, m.Donde))
            .ToList();
        return Ok(res);
    }

    /// <summary>Ficha completa de un empleado. <paramref name="fecha"/> = día (hora argentina) del detalle.</summary>
    [HttpGet("{empId:int}")]
    public async Task<IActionResult> Ficha(int empId, [FromQuery] DateTime? fecha)
    {
        if (!EsDueno) return Forbid();
        var e = await _db.NomEmpleados.AsNoTracking().FirstOrDefaultAsync(x => x.Id == empId);
        if (e is null) return NotFound();

        var arNow = DateTime.UtcNow.AddHours(-3);
        var hoyAr = arNow.Date;
        var dia = (fecha ?? hoyAr).Date;
        if (dia > hoyAr) dia = hoyAr;
        var diaUtc = dia.AddHours(3);

        // Movimientos del día elegido + de los últimos 7 días (resumen de la semana).
        var semanaDesde = hoyAr.AddDays(-6).AddHours(3);
        var desde = diaUtc < semanaDesde ? diaUtc : semanaDesde;
        var movs = await CargarMovsAsync(desde, DateTime.UtcNow.AddMinutes(1), empId);
        var delDia = movs.Where(m => m.CuandoUtc >= diaUtc && m.CuandoUtc < diaUtc.AddDays(1))
            .OrderByDescending(m => m.CuandoUtc).ToList();
        var ritmo = new int[24];
        foreach (var m in delDia) ritmo[m.CuandoUtc.AddHours(-3).Hour]++;
        var semana = movs.Where(m => m.CuandoUtc >= semanaDesde && m.Clase != "")
            .GroupBy(m => m.Clase)
            .Select(g => new { Clase = g.Key, Cantidad = g.Count() })
            .OrderByDescending(x => x.Cantidad).ToList();
        var ultimo = movs.OrderByDescending(m => m.CuandoUtc).FirstOrDefault();
        var usaDesde = movs.Where(m => m.CuandoUtc >= semanaDesde).Select(m => m.Donde)
            .Where(d => d is "celu" or "compu" or "mapa").Distinct().ToList();

        // ── Plata ──
        var mesAnt = hoyAr.AddMonths(-1);
        var liqs = await _db.NomLiquidaciones.AsNoTracking()
            .Where(l => l.EmpleadoId == empId && ((l.Anio == hoyAr.Year && l.Mes == hoyAr.Month) || (l.Anio == mesAnt.Year && l.Mes == mesAnt.Month)))
            .ToListAsync();
        var liqIds = liqs.Select(l => l.Id).ToList();
        var pagos = await _db.NomPagos.AsNoTracking().Where(p => liqIds.Contains(p.LiquidacionId)).ToListAsync();
        object? LiqDto(int anio, int mes)
        {
            var l = liqs.FirstOrDefault(x => x.Anio == anio && x.Mes == mes);
            if (l is null) return null;
            var ps = pagos.Where(p => p.LiquidacionId == l.Id).ToList();
            return new
            {
                l.Id, l.Anio, l.Mes, l.NetoAPagar, l.Adelantos, l.Estado, l.DiasTrabajados, l.KgCafe,
                Pagado = ps.Sum(p => p.Monto),
                UltimoPago = ps.Count == 0 ? (DateTime?)null : ps.Max(p => p.FechaPago),
            };
        }

        // ── Vacaciones (misma regla que la pantalla de Vacaciones) ──
        var periodo = hoyAr.Month <= 4 ? hoyAr.Year - 1 : hoyAr.Year;
        var vacs = await _db.NomVacaciones.AsNoTracking().Where(v => v.EmpleadoId == empId).ToListAsync();
        int Dias(Models.NomVacacion v) => (v.Hasta.Date - v.Desde.Date).Days + 1;
        var proxima = vacs.Where(v => v.Hasta.Date >= hoyAr && v.Estado != "rechazada")
            .OrderBy(v => v.Desde).FirstOrDefault();

        // ── Documentos ──
        var docs = await _db.NomEmpleadoArchivos.AsNoTracking().Where(a => a.EmpleadoId == empId)
            .OrderByDescending(a => a.UploadedAt)
            .Select(a => new { a.Id, a.FileName, a.UploadedAt })
            .ToListAsync();

        // ── Links personales ──
        var ficha = await _db.HorasExtrasEmpleados.AsNoTracking().FirstOrDefaultAsync(f => f.NomEmpleadoId == empId);
        var rep = await _db.CafeRepartidores.AsNoTracking().FirstOrDefaultAsync(r => r.NomEmpleadoId == empId && r.IsActive);
        var links = new List<object>();
        if (ficha is not null && !string.IsNullOrEmpty(ficha.DniUltimos3))
            links.Add(new { Que = "Su área personal (fichar, horario)", Ruta = $"/he/{Slugify(ficha.Nombre)}/horario" });
        if (rep is not null && !string.IsNullOrEmpty(rep.PublicToken))
            links.Add(new { Que = "Sus pedidos de reparto", Ruta = $"/mis-pedidos/{rep.PublicToken}" });
        if (!string.IsNullOrEmpty(e.TareasToken))
            links.Add(new { Que = "Sus tareas del depósito", Ruta = $"/tareas/{e.TareasToken}" });

        string fichar = ficha is null ? "no está en el fichador"
            : !ficha.IsActive ? "dado de baja en el fichador"
            : ficha.MostrarEnFichador ? "ficha entrada y salida" : "no ficha";

        return Ok(new
        {
            e.Id, e.Nombre, e.Puesto, e.FechaIngreso, e.Documento,
            e.TelefonoContacto, e.TelefonoFamiliar, e.Email, e.Domicilio, e.Banco, e.Cbu, e.Alias,
            e.ModalidadSueldo, e.SueldoBase, e.JornalDiario, e.ValorHora, e.ComisionPorcentaje, e.ComisionPorKg, e.BonoFijo,
            Dia = dia,
            DelDia = delDia.Select(m => new { Hora = m.CuandoUtc.AddHours(-3).ToString("HH:mm"), m.Texto, m.Donde }),
            Ritmo = ritmo,
            Semana = semana,
            Ultimo = ultimo is null ? null : new { ultimo.CuandoUtc, ultimo.Texto, ultimo.Donde },
            UsaDesde = usaDesde,
            Fichar = fichar,
            MesActual = LiqDto(hoyAr.Year, hoyAr.Month),
            MesAnterior = LiqDto(mesAnt.Year, mesAnt.Month),
            Vacaciones = new
            {
                Periodo = periodo,
                LeTocan = LeTocan(e.FechaIngreso, periodo),
                Tomadas = vacs.Where(v => v.Periodo == periodo && v.Estado == "aprobada").Sum(Dias),
                Pedidas = vacs.Where(v => v.Periodo == periodo && v.Estado == "pedida").Sum(Dias),
                ProximaDesde = proxima?.Desde, ProximaHasta = proxima?.Hasta, ProximaEstado = proxima?.Estado,
            },
            Documentos = docs,
            Links = links,
        });
    }

    // ═════════════════════════ Movimientos ═════════════════════════

    /// <summary>Todo lo que hicieron los empleados entre <paramref name="desdeUtc"/> y <paramref name="hastaUtc"/>.
    /// Si <paramref name="soloEmp"/> viene, devuelve solo los de esa persona.</summary>
    private async Task<List<MovDto>> CargarMovsAsync(DateTime desdeUtc, DateTime hastaUtc, int? soloEmp)
    {
        var alias = await AliasAsync();
        int? Quien(string? op)
        {
            var k = Clave(op);
            return k != null && alias.TryGetValue(k, out var id) && (soloEmp == null || soloEmp == id) ? id : null;
        }
        var res = new List<MovDto>();

        // 1) Historial de ventas: repartos, entregas, ediciones.
        var hist = await _db.CafeVentaHistorial.AsNoTracking()
            .Where(h => h.CreatedAt >= desdeUtc && h.CreatedAt < hastaUtc && h.Quien != null)
            .Join(_db.CafeVentas, h => h.VentaId, v => v.Id,
                (h, v) => new { h.Tipo, h.Quien, h.Desde, h.Detalle, h.CreatedAt, v.Numero, Cli = v.ClienteNombreSnapshot })
            .ToListAsync();
        foreach (var h in hist)
        {
            if (Quien(h.Quien) is not int id) continue;
            var det = PrimeraLinea(h.Detalle);
            string texto, clase;
            switch (h.Tipo)
            {
                case "ENTREGA": texto = $"Entregó la venta {Num(h.Numero)}"; clase = "entregas"; break;
                case "EDICION": texto = $"Editó la venta {Num(h.Numero)} · {det}"; clase = "ventas editadas"; break;
                default: texto = $"{Mayus(det)} · venta {Num(h.Numero)}"; clase = "movimientos de reparto"; break;
            }
            if (!string.IsNullOrWhiteSpace(h.Cli)) texto += $" · {h.Cli}";
            res.Add(new MovDto(id, h.CreatedAt, texto, h.Desde ?? "", clase));
        }

        // 2) Preparación: cambios de estado (las entregas ya salen del historial).
        var prep = await _db.CafeVentaPreparacionLogs.AsNoTracking()
            .Where(p => p.CreatedAt >= desdeUtc && p.CreatedAt < hastaUtc && p.OperadorNombre != null
                        && p.EstadoNuevo != "ENTREGADO" && p.EstadoAnterior != p.EstadoNuevo)
            .Join(_db.CafeVentas, p => p.VentaId, v => v.Id,
                (p, v) => new { p.OperadorNombre, p.EstadoNuevo, p.CreatedAt, v.Numero, Cli = v.ClienteNombreSnapshot })
            .ToListAsync();
        foreach (var p in prep)
        {
            if (Quien(p.OperadorNombre) is not int id) continue;
            var (texto, clase) = p.EstadoNuevo switch
            {
                "LISTO" => ($"Dejó lista la venta {Num(p.Numero)}", "ventas armadas"),
                "EN_PREPARACION" => ($"Empezó a armar la venta {Num(p.Numero)}", ""),
                "PARA_PREPARAR" => ($"Mandó a preparar la venta {Num(p.Numero)}", "mandadas a preparar"),
                "EN_CAMINO" => ($"Salió con la venta {Num(p.Numero)}", ""),
                _ => ($"Movió la venta {Num(p.Numero)} en Preparación", ""),
            };
            if (!string.IsNullOrWhiteSpace(p.Cli)) texto += $" · {p.Cli}";
            res.Add(new MovDto(id, p.CreatedAt, texto, "Preparación", clase));
        }

        // 3) Auditoría: cobranzas, bajas, stock masivo… (sin las sincronizaciones automáticas).
        var aud = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.CreatedAt >= desdeUtc && a.CreatedAt < hastaUtc && a.UserName != null && a.EntityType != "Sync")
            .Select(a => new { a.UserName, a.EntityType, a.Action, a.Changes, a.CreatedAt })
            .ToListAsync();
        foreach (var a in aud)
        {
            if (Quien(a.UserName) is not int id) continue;
            var (donde, clase) = a.EntityType switch
            {
                "CafeCobranza" => ("Cobranzas", a.Action == "CREATE" ? "cobranzas" : ""),
                "CafeStockMasivo" => ("Stock", "cargas de stock"),
                "CafeProducto" => ("Productos", "cambios en productos"),
                _ => (a.EntityType.Replace("Cafe", ""), ""),
            };
            var texto = string.IsNullOrWhiteSpace(a.Changes) ? $"{a.Action} {a.EntityType}" : Corto(LimpiarAudit(a.Changes), 110);
            res.Add(new MovDto(id, a.CreatedAt, texto, donde, clase));
        }

        // 4) Stock cargado / contado a mano.
        var stock = await _db.StockMovimientos.AsNoTracking()
            .Where(m => m.CreatedAt >= desdeUtc && m.CreatedAt < hastaUtc && m.OperadorNombreSnap != null && StockAMano.Contains(m.TipoMov))
            .Select(m => new { m.OperadorNombreSnap, m.TipoMov, m.Cantidad, m.StockDespues, m.CreatedAt, Prod = m.Producto!.Nombre, m.Producto.Sku })
            .ToListAsync();
        foreach (var m in stock)
        {
            if (Quien(m.OperadorNombreSnap) is not int id) continue;
            var prod = string.IsNullOrWhiteSpace(m.Sku) ? m.Prod : $"{m.Sku} {m.Prod}";
            var texto = m.TipoMov switch
            {
                "SUMA" => $"Sumó {m.Cantidad} u de stock",
                "RESTA" => $"Restó {Math.Abs(m.Cantidad)} u de stock",
                "SET" => $"Dejó el stock en {m.StockDespues} u",
                "CONTEO" => "Contó el stock",
                "DEVUELTO_VENTA" => $"Volvió al stock {m.Cantidad} u (devolución)",
                _ => $"Ajustó el stock a {m.StockDespues} u",
            };
            res.Add(new MovDto(id, m.CreatedAt, $"{texto} · {Corto(prod, 60)}", "Stock", "movimientos de stock"));
        }

        // 5) Tareas del depósito hechas.
        var tareas = await _db.DepTareaItems.AsNoTracking()
            .Where(i => i.HechoAt != null && i.HechoAt >= desdeUtc && i.HechoAt < hastaUtc && i.HechoPor != null)
            .Join(_db.DepTareas, i => i.TareaId, t => t.Id, (i, t) => new { i.HechoPor, i.HechoAt, i.ProductoId, t.Titulo })
            .Join(_db.CafeProductos, x => x.ProductoId, p => p.Id, (x, p) => new { x.HechoPor, x.HechoAt, x.Titulo, p.Nombre, p.Sku })
            .ToListAsync();
        foreach (var t in tareas)
        {
            if (Quien(t.HechoPor) is not int id) continue;
            var prod = string.IsNullOrWhiteSpace(t.Sku) ? t.Nombre : $"{t.Sku} {t.Nombre}";
            res.Add(new MovDto(id, t.HechoAt!.Value, $"Hizo la tarea \"{Corto(t.Titulo, 40)}\" · {Corto(prod, 50)}", "Tareas depósito", "tareas"));
        }

        // 6) Fichadas (la hora se guarda en hora argentina).
        var diaDesde = desdeUtc.AddHours(-3).Date;
        var diaHasta = hastaUtc.AddHours(-3).Date;
        var fichas = await _db.HorasExtrasEmpleados.AsNoTracking()
            .Where(f => f.NomEmpleadoId != null && (soloEmp == null || f.NomEmpleadoId == soloEmp))
            .Select(f => new { f.Id, Emp = f.NomEmpleadoId!.Value }).ToListAsync();
        var empPorFicha = fichas.ToDictionary(f => f.Id, f => f.Emp);
        var fichaIds = empPorFicha.Keys.ToList();
        var regs = await _db.HorasExtrasRegistros.AsNoTracking()
            .Where(r => r.Fecha >= diaDesde && r.Fecha <= diaHasta && fichaIds.Contains(r.EmpleadoId) && r.HoraEntrada != null)
            .Select(r => new { r.EmpleadoId, r.Fecha, r.HoraEntrada, r.HoraSalida }).ToListAsync();
        foreach (var r in regs)
        {
            var id = empPorFicha[r.EmpleadoId];
            var ent = r.Fecha.Date.Add(r.HoraEntrada!.Value).AddHours(3);
            if (ent >= desdeUtc && ent < hastaUtc) res.Add(new MovDto(id, ent, "Fichó la entrada", "Fichador", ""));
            if (r.HoraSalida is TimeSpan hs)
            {
                var sal = r.Fecha.Date.Add(hs).AddHours(3);
                if (sal >= desdeUtc && sal < hastaUtc) res.Add(new MovDto(id, sal, "Fichó la salida", "Fichador", ""));
            }
        }

        return res;
    }

    /// <summary>Cada forma de nombrar a un empleado (nombres de pila, apodo del fichador, nombre de
    /// repartidor) → su Id. Los nombres que comparten dos personas se descartan.</summary>
    private async Task<Dictionary<string, int>> AliasAsync()
    {
        var emps = await _db.NomEmpleados.AsNoTracking().Where(e => e.IsActive).Select(e => new { e.Id, e.Nombre }).ToListAsync();
        var fichas = await _db.HorasExtrasEmpleados.AsNoTracking().Where(f => f.NomEmpleadoId != null)
            .Select(f => new { Emp = f.NomEmpleadoId!.Value, f.Nombre }).ToListAsync();
        var reps = await _db.CafeRepartidores.AsNoTracking().Where(r => r.NomEmpleadoId != null)
            .Select(r => new { Emp = r.NomEmpleadoId!.Value, r.Nombre }).ToListAsync();

        var pares = new List<(string, int)>();
        foreach (var e in emps)
        {
            var toks = Norm(e.Nombre).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Nombres de pila: el primero; si tiene 3 o más palabras, también el segundo.
            if (toks.Length > 0) pares.Add((toks[0], e.Id));
            if (toks.Length >= 3) pares.Add((toks[1], e.Id));
        }
        var activos = emps.Select(e => e.Id).ToHashSet();
        foreach (var f in fichas.Where(f => activos.Contains(f.Emp))) if (Clave(f.Nombre) is string k) pares.Add((k, f.Emp));
        foreach (var r in reps.Where(r => activos.Contains(r.Emp))) if (Clave(r.Nombre) is string k) pares.Add((k, r.Emp));

        return pares.Distinct()
            .GroupBy(p => p.Item1)
            .Where(g => g.Select(x => x.Item2).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Item2);
    }

    /// <summary>"repartidor: GABRIEL" → "gabriel"; "Germán" → "german"; "zzzzzbenjamin" → "benjamin".</summary>
    private static string? Clave(string? op)
    {
        if (string.IsNullOrWhiteSpace(op)) return null;
        var s = Norm(op);
        if (s.StartsWith("repartidor:")) s = s["repartidor:".Length..];
        s = s.Trim();
        var tok = s.Split(' ', '(', ',')[0].TrimStart('z');
        return tok.Length >= 3 ? tok : null;
    }

    private static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString().Trim();
    }

    private static string Slugify(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder();
        foreach (var c in s.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-');
    }

    private static int LeTocan(DateTime fechaIngreso, int periodo)
    {
        var corte = new DateTime(periodo, 12, 31);
        var ingreso = fechaIngreso.Date;
        if (ingreso > corte) return 0;
        var anios = corte.Year - ingreso.Year;
        if (ingreso.AddYears(anios) > corte) anios--;
        if (ingreso.AddMonths(6) > corte)
            return (int)Math.Floor(((corte - ingreso).Days + 1) * 5.0 / 7.0 / 20.0);
        if (anios < 5) return 14;
        if (anios < 10) return 21;
        if (anios < 20) return 28;
        return 35;
    }

    private static string Num(string numero)
    {
        // "0001-00008123" → "8123"
        var n = numero.Contains('-') ? numero[(numero.LastIndexOf('-') + 1)..] : numero;
        return n.TrimStart('0') is { Length: > 0 } t ? t : n;
    }

    private static string PrimeraLinea(string? s)
        => string.IsNullOrWhiteSpace(s) ? "" : s.Split('\n')[0].Trim();

    private static string Mayus(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    private static string Corto(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>"Cobranza 0100-00004744 para cliente Maria UNCAL, total $45,000.00, fecha…" → "Cobranza 4744 · Maria UNCAL · $45.000".</summary>
    private static string LimpiarAudit(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^Cobranza (\S+) para cliente (.+?), total \$([\d,]+)(?:\.\d+)?");
        if (m.Success)
            return $"Cobró ${m.Groups[3].Value.Replace(',', '.')} · {m.Groups[2].Value} · cobranza {Num(m.Groups[1].Value)}";
        return PrimeraLinea(s);
    }
}
