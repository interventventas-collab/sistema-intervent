using System.Security.Cryptography;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-03: "Tareas del depósito". Pedido del dueño: "no tenemos nada para darle trabajos a los de
/// depósito en su pantalla, onda tareas pendientes, y se las voy asignando". Que aparezcan TODOS los
/// empleados. Ver <see cref="DepTarea"/>.
///
/// Compu (con login): armar la tarea eligiendo qué hacer, de qué productos (stock dudoso, negativo,
/// sin mover, sin lugar, sin código, a mano), para quién y para cuándo; ver cómo van; y en los
/// conteos que no coinciden, decidir si se corrige el stock ("Aceptar conteo"). El stock NUNCA cambia
/// solo por un conteo, para no pisar MercadoLibre por un error.
///
/// Celu (sin login, por token): el mismo link del repartidor (/mis-pedidos/{token}) o, para los que
/// no tienen, uno propio (/tareas/{token}, en Nom_Empleados.TareasToken).
/// </summary>
[ApiController]
[Route("api/deposito-tareas")]
[Authorize]
public class DepositoTareasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;

    public DepositoTareasController(AppDbContext db, IServiceScopeFactory scopeFactory) { _db = db; _scopeFactory = scopeFactory; }

    private string Usuario => User.Identity?.Name ?? "?";

    public static readonly string[] Tipos = { "CONTAR", "UBICACION", "BARCODE", "LIBRE" };
    public static readonly string[] Paras = { "HOY", "SEMANA", "SIN_APURO" };

    // Movimientos que son "alguien tocó el stock a mano" (no ventas ni MeLi).
    private static readonly string[] MovsAMano = { "SUMA", "RESTA", "SET", "AJUSTE_ADMIN" };

    // ════════════════════════════════════════════════════════════════════
    // COMPU
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Todos los empleados activos + si ya tienen link en el celu (el de repartidor o el de tareas).</summary>
    [HttpGet("empleados")]
    public async Task<IActionResult> Empleados()
    {
        var emps = await _db.NomEmpleados.AsNoTracking().Where(e => e.IsActive).OrderBy(e => e.Nombre).ToListAsync();
        var reps = await _db.CafeRepartidores.AsNoTracking()
            .Where(r => r.IsActive && r.NomEmpleadoId != null)
            .Select(r => new { r.NomEmpleadoId, r.PublicToken }).ToListAsync();
        return Ok(emps.Select(e =>
        {
            var rep = reps.FirstOrDefault(r => r.NomEmpleadoId == e.Id);
            return new
            {
                id = e.Id,
                nombre = NombreCorto(e.Nombre),
                nombreCompleto = e.Nombre,
                linkRepartidor = rep?.PublicToken,
                linkTareas = e.TareasToken
            };
        }));
    }

    /// <summary>Crea (si no tiene) el link propio de tareas de un empleado sin link de repartidor.</summary>
    [HttpPost("empleados/{id:int}/link")]
    public async Task<IActionResult> CrearLink(int id)
    {
        var e = await _db.NomEmpleados.FirstOrDefaultAsync(x => x.Id == id);
        if (e is null) return NotFound();
        if (string.IsNullOrEmpty(e.TareasToken))
        {
            e.TareasToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            await _db.SaveChangesAsync();
        }
        return Ok(new { token = e.TareasToken });
    }

    /// <summary>Productos sugeridos para una tarea. fuente: dudosos | negativos | sin-mover | sin-lugar |
    /// sin-codigo | buscar (con q). Los que ya están en una tarea abierta del mismo tipo no se repiten.</summary>
    [HttpGet("candidatos")]
    public async Task<IActionResult> Candidatos([FromQuery] string fuente, [FromQuery] string? tipo = null, [FromQuery] string? q = null)
    {
        var prods = _db.CafeProductos.AsNoTracking().Where(p => p.IsActive);
        var hace60 = DateTime.UtcNow.AddDays(-60);
        var hace30 = DateTime.UtcNow.AddDays(-30);
        var hace90 = DateTime.UtcNow.AddDays(-90);

        // productoId → motivo
        var motivos = new Dictionary<int, string>();

        switch (fuente)
        {
            case "dudosos":
            {
                // 1) stock en negativo
                foreach (var id in await prods.Where(p => p.StockUnidades < 0).Select(p => p.Id).ToListAsync())
                    motivos[id] = "Stock en negativo";
                // 2) corregidos a mano 2 o más veces en 60 días
                var corregidos = await _db.StockMovimientos.AsNoTracking()
                    .Where(m => !m.Reverted && m.CreatedAt >= hace60 && MovsAMano.Contains(m.TipoMov))
                    .GroupBy(m => m.ProductoId)
                    .Select(g => new { Id = g.Key, N = g.Count() })
                    .Where(x => x.N >= 2)
                    .OrderByDescending(x => x.N)
                    .ToListAsync();
                foreach (var c in corregidos)
                    motivos.TryAdd(c.Id, $"Corregido a mano {c.N} veces en 2 meses");
                // 3) se venden mucho y quedan 1 a 3
                var vendidos = await (from m in _db.StockMovimientos.AsNoTracking()
                                      join p in prods on m.ProductoId equals p.Id
                                      where m.TipoMov.StartsWith("VENTA") && m.CreatedAt >= hace30
                                            && p.StockUnidades >= 1 && p.StockUnidades <= 3
                                      group m by m.ProductoId into g
                                      select new { Id = g.Key, N = g.Sum(x => x.Cantidad) })
                                     .Where(x => x.N >= 2)
                                     .OrderByDescending(x => x.N)
                                     .ToListAsync();
                foreach (var v in vendidos)
                    motivos.TryAdd(v.Id, $"Se vendieron {v.N} en 30 días y quedan pocos");
                break;
            }
            case "negativos":
                foreach (var id in await prods.Where(p => p.StockUnidades < 0).Select(p => p.Id).ToListAsync())
                    motivos[id] = "Stock en negativo";
                break;
            case "sin-mover":
            {
                var lista = await prods
                    .Where(p => p.StockUnidades > 0 && (p.StockChangedAt == null || p.StockChangedAt < hace90))
                    .OrderByDescending(p => p.StockUnidades * p.Costo)
                    .Select(p => new { p.Id, p.StockChangedAt })
                    .Take(300).ToListAsync();
                foreach (var p in lista)
                    motivos[p.Id] = p.StockChangedAt is null ? "Nunca se movió" : $"Sin moverse desde {p.StockChangedAt.Value.AddHours(-3):dd/MM}";
                break;
            }
            case "sin-lugar":
                foreach (var id in await prods.Where(p => p.StockUnidades > 0 && p.UbicacionPlanta == null)
                             .OrderBy(p => p.Nombre).Select(p => p.Id).Take(300).ToListAsync())
                    motivos[id] = "Sin lugar cargado";
                break;
            case "sin-codigo":
                foreach (var id in await prods.Where(p => p.StockUnidades > 0 && (p.Barcode == null || p.Barcode == ""))
                             .OrderBy(p => p.Nombre).Select(p => p.Id).Take(300).ToListAsync())
                    motivos[id] = "Sin código de barras";
                break;
            case "buscar":
            {
                var query = prods;
                var palabras = (q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (palabras.Length == 0) return Ok(Array.Empty<object>());
                foreach (var palabra in palabras)
                {
                    var w = palabra;
                    query = query.Where(p => p.Nombre.Contains(w) || (p.Sku != null && p.Sku.Contains(w)) || (p.Barcode != null && p.Barcode.Contains(w)));
                }
                foreach (var id in await query.OrderBy(p => p.Nombre).Select(p => p.Id).Take(50).ToListAsync())
                    motivos[id] = "";
                break;
            }
            default:
                return BadRequest(new { error = "Fuente desconocida." });
        }

        // Ya en una tarea abierta del mismo tipo → no repetir.
        var tipoN = (tipo ?? "").ToUpperInvariant();
        var yaEnTarea = await (from i in _db.DepTareaItems.AsNoTracking()
                               join t in _db.DepTareas.AsNoTracking() on i.TareaId equals t.Id
                               where t.TerminadaAt == null && t.CanceladaAt == null && t.Tipo == tipoN && i.HechoAt == null
                               select i.ProductoId).Distinct().ToListAsync();

        var ids = motivos.Keys.Where(id => !yaEnTarea.Contains(id)).ToList();
        var datos = await _db.CafeProductos.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.StockUnidades, p.UbicacionPlanta, p.UbicacionZona, p.Barcode })
            .ToDictionaryAsync(p => p.Id);

        return Ok(ids.Where(datos.ContainsKey).Select(id =>
        {
            var p = datos[id];
            return new
            {
                id = p.Id,
                sku = p.Sku,
                nombre = p.Nombre,
                stock = p.StockUnidades,
                lugar = UbicacionHelper.Texto(p.UbicacionPlanta, p.UbicacionZona),
                barcode = p.Barcode,
                motivo = motivos[id]
            };
        }));
    }

    public record CrearRequest(string Tipo, string? Titulo, string? Comentario, int? EmpleadoId, string? Para, List<int>? ProductoIds);

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearRequest req)
    {
        var tipo = (req?.Tipo ?? "").ToUpperInvariant();
        if (!Tipos.Contains(tipo)) return BadRequest(new { error = "Elegí qué tienen que hacer." });
        var para = Paras.Contains((req!.Para ?? "").ToUpperInvariant()) ? req.Para!.ToUpperInvariant() : "SIN_APURO";
        var ids = (req.ProductoIds ?? new()).Distinct().ToList();
        var titulo = (req.Titulo ?? "").Trim();

        if (tipo == "LIBRE")
        {
            if (titulo.Length == 0) return BadRequest(new { error = "Escribí qué tienen que hacer." });
            ids.Clear();
        }
        else
        {
            if (ids.Count == 0) return BadRequest(new { error = "No elegiste ningún producto." });
            if (ids.Count > 500) return BadRequest(new { error = "Son demasiados productos para una sola tarea (máximo 500)." });
            if (titulo.Length == 0) titulo = TituloPorDefecto(tipo);
        }

        if (req.EmpleadoId is int eid && !await _db.NomEmpleados.AnyAsync(e => e.Id == eid && e.IsActive))
            return BadRequest(new { error = "Ese empleado no existe." });

        var t = new DepTarea
        {
            Tipo = tipo,
            Titulo = titulo.Length > 200 ? titulo[..200] : titulo,
            Comentario = string.IsNullOrWhiteSpace(req.Comentario) ? null : req.Comentario.Trim(),
            EmpleadoId = req.EmpleadoId,
            Para = para,
            CreatedAt = DateTime.UtcNow,
            CreatedBy = Usuario
        };
        _db.DepTareas.Add(t);
        await _db.SaveChangesAsync();

        // Orden: por lugar (así recorren el depósito de un tirón) y después por nombre.
        var orden = await _db.CafeProductos.AsNoTracking().Where(p => ids.Contains(p.Id))
            .OrderBy(p => p.UbicacionPlanta == null).ThenBy(p => p.UbicacionPlanta).ThenBy(p => p.UbicacionZona).ThenBy(p => p.Nombre)
            .Select(p => p.Id).ToListAsync();
        var n = 0;
        foreach (var pid in orden)
            _db.DepTareaItems.Add(new DepTareaItem { TareaId = t.Id, ProductoId = pid, Orden = ++n });
        await _db.SaveChangesAsync();

        return Ok(new { ok = true, id = t.Id, items = orden.Count });
    }

    private static string TituloPorDefecto(string tipo) => tipo switch
    {
        "CONTAR" => "Contar stock",
        "UBICACION" => "Cargar dónde está",
        "BARCODE" => "Cargar código de barras",
        _ => "Tarea"
    };

    /// <summary>Lista de tareas. estado: curso (default) | terminadas (últimos 30 días).</summary>
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] string? estado = null)
    {
        var q = _db.DepTareas.AsNoTracking().Where(t => t.CanceladaAt == null);
        q = estado == "terminadas"
            ? q.Where(t => t.TerminadaAt != null && t.TerminadaAt >= DateTime.UtcNow.AddDays(-30)).OrderByDescending(t => t.TerminadaAt)
            : q.Where(t => t.TerminadaAt == null).OrderBy(t => t.CreatedAt);
        var tareas = await q.Take(200).ToListAsync();
        var ids = tareas.Select(t => t.Id).ToList();

        var stats = await _db.DepTareaItems.AsNoTracking().Where(i => ids.Contains(i.TareaId))
            .GroupBy(i => i.TareaId)
            .Select(g => new
            {
                Id = g.Key,
                Total = g.Count(),
                Hechos = g.Count(i => i.HechoAt != null),
                NoEncontrados = g.Count(i => i.NoEncontrado),
                Diferencias = g.Count(i => i.HechoAt != null && i.Contado != null && i.Contado != i.StockAlContar),
                SinRevisar = g.Count(i => i.HechoAt != null && (i.NoEncontrado || (i.Contado != null && i.Contado != i.StockAlContar)) && i.Revision == null)
            }).ToDictionaryAsync(x => x.Id);

        var emps = await _db.NomEmpleados.AsNoTracking().ToDictionaryAsync(e => e.Id, e => NombreCorto(e.Nombre));

        return Ok(tareas.Select(t =>
        {
            stats.TryGetValue(t.Id, out var s);
            return new
            {
                id = t.Id,
                tipo = t.Tipo,
                titulo = t.Titulo,
                comentario = t.Comentario,
                empleadoId = t.EmpleadoId,
                empleado = t.EmpleadoId is int e && emps.TryGetValue(e, out var nom) ? nom : "Cualquiera",
                para = t.Para,
                createdAt = t.CreatedAt,
                terminadaAt = t.TerminadaAt,
                terminadaPor = t.TerminadaPor,
                total = s?.Total ?? 0,
                hechos = s?.Hechos ?? 0,
                noEncontrados = s?.NoEncontrados ?? 0,
                diferencias = s?.Diferencias ?? 0,
                sinRevisar = s?.SinRevisar ?? 0
            };
        }));
    }

    /// <summary>Detalle con cada producto: lo que hicieron y, en los conteos, lo que decía el sistema.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Detalle(int id)
    {
        var t = await _db.DepTareas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return NotFound();
        var items = await (from i in _db.DepTareaItems.AsNoTracking()
                           join p in _db.CafeProductos.AsNoTracking() on i.ProductoId equals p.Id
                           where i.TareaId == id
                           orderby i.Orden
                           select new
                           {
                               id = i.Id,
                               productoId = p.Id,
                               sku = p.Sku,
                               nombre = p.Nombre,
                               stockAhora = p.StockUnidades,
                               lugar = UbicacionHelper.Texto(p.UbicacionPlanta, p.UbicacionZona),
                               barcode = p.Barcode,
                               hechoAt = i.HechoAt,
                               hechoPor = i.HechoPor,
                               contado = i.Contado,
                               stockAlContar = i.StockAlContar,
                               noEncontrado = i.NoEncontrado,
                               valor = i.Valor,
                               revision = i.Revision,
                               revisionPor = i.RevisionPor
                           }).ToListAsync();
        return Ok(new { id = t.Id, tipo = t.Tipo, titulo = t.Titulo, comentario = t.Comentario, items });
    }

    /// <summary>Corrige el stock con el conteo. Se suma la DIFERENCIA que vieron al contar (no se pisa con
    /// el número contado): si entre que contaron y ahora se vendió algo, esa venta no se pierde.</summary>
    [HttpPost("items/{itemId:int}/aceptar")]
    public async Task<IActionResult> Aceptar(int itemId)
    {
        var it = await _db.DepTareaItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (it is null) return NotFound();
        if (it.Revision is not null) return BadRequest(new { error = "Ese conteo ya se revisó." });
        if (it.Contado is not int contado || it.StockAlContar is not int alContar)
            return BadRequest(new { error = "Ese producto todavía no se contó." });

        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == it.ProductoId);
        if (prod is null) return NotFound();

        var antes = prod.StockUnidades;
        var despues = Math.Max(0, antes + (contado - alContar));
        prod.StockUnidades = despues;
        prod.UpdatedAt = DateTime.UtcNow;
        if (antes != despues)
        {
            prod.StockChangedAt = DateTime.UtcNow;
            await CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
        }

        _db.StockMovimientos.Add(new StockMovimiento
        {
            ProductoId = prod.Id,
            OperadorNombreSnap = Usuario,
            DepositoNombreSnap = "9 de Abril",
            TipoMov = "SET",
            Cantidad = despues,
            StockAntes = antes,
            StockDespues = despues,
            Comentario = $"Conteo de la tarea #{it.TareaId}: {it.HechoPor} contó {contado} (el sistema decía {alContar})",
            CreatedAt = DateTime.UtcNow
        });
        it.Revision = "ACEPTADO";
        it.RevisionAt = DateTime.UtcNow;
        it.RevisionPor = Usuario;
        await _db.SaveChangesAsync();

        if (antes != despues) FireAndForgetPushMeli(prod.Id);
        return Ok(new { ok = true, stockAntes = antes, stockDespues = despues });
    }

    /// <summary>"Dejarlo como está": el conteo queda anotado pero el stock no se toca.</summary>
    [HttpPost("items/{itemId:int}/descartar")]
    public async Task<IActionResult> Descartar(int itemId)
    {
        var it = await _db.DepTareaItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (it is null) return NotFound();
        it.Revision = "DESCARTADO";
        it.RevisionAt = DateTime.UtcNow;
        it.RevisionPor = Usuario;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Saca la tarea de los celus. Lo que ya hicieron queda guardado.</summary>
    [HttpPost("{id:int}/cancelar")]
    public async Task<IActionResult> Cancelar(int id)
    {
        var t = await _db.DepTareas.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return NotFound();
        t.CanceladaAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private void FireAndForgetPushMeli(int cafeProductoId)
    {
        var sf = _scopeFactory;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = sf.CreateScope();
                var pushSvc = scope.ServiceProvider.GetRequiredService<MeliStockPushService>();
                await pushSvc.PushStockForProductoAsync(cafeProductoId);
            }
            catch { /* queda en StockChangedAt, lo retoma el background */ }
        });
    }

    // ════════════════════════════════════════════════════════════════════
    // CELU (sin login, por token)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>El empleado de ese link: el de repartidor (si está atado a un empleado) o el propio de tareas.</summary>
    private async Task<NomEmpleado?> EmpleadoDelTokenAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 64) return null;
        var rep = await _db.CafeRepartidores.AsNoTracking()
            .Where(r => r.PublicToken == token && r.IsActive && r.NomEmpleadoId != null)
            .Select(r => r.NomEmpleadoId).FirstOrDefaultAsync();
        if (rep is int eid)
            return await _db.NomEmpleados.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eid && e.IsActive);
        return await _db.NomEmpleados.AsNoTracking().FirstOrDefaultAsync(e => e.TareasToken == token && e.IsActive);
    }

    private IQueryable<DepTarea> TareasDe(int empleadoId) =>
        _db.DepTareas.Where(t => t.CanceladaAt == null && (t.EmpleadoId == null || t.EmpleadoId == empleadoId));

    /// <summary>Pendientes + las terminadas hoy (hora argentina).</summary>
    [HttpGet("celu/{token}")]
    [AllowAnonymous]
    public async Task<IActionResult> CeluInicio(string token)
    {
        var emp = await EmpleadoDelTokenAsync(token);
        if (emp is null) return NotFound();

        var inicioHoyUtc = DateTime.UtcNow.AddHours(-3).Date.AddHours(3);
        var tareas = await TareasDe(emp.Id).AsNoTracking()
            .Where(t => t.TerminadaAt == null || t.TerminadaAt >= inicioHoyUtc)
            .OrderBy(t => t.TerminadaAt != null).ThenBy(t => t.Para == "HOY" ? 0 : t.Para == "SEMANA" ? 1 : 2).ThenBy(t => t.CreatedAt)
            .ToListAsync();
        var ids = tareas.Select(t => t.Id).ToList();
        var stats = await _db.DepTareaItems.AsNoTracking().Where(i => ids.Contains(i.TareaId))
            .GroupBy(i => i.TareaId)
            .Select(g => new { Id = g.Key, Total = g.Count(), Hechos = g.Count(i => i.HechoAt != null) })
            .ToDictionaryAsync(x => x.Id);

        return Ok(new
        {
            nombre = NombreCorto(emp.Nombre),
            tareas = tareas.Select(t =>
            {
                stats.TryGetValue(t.Id, out var s);
                return new
                {
                    id = t.Id,
                    tipo = t.Tipo,
                    titulo = t.Titulo,
                    comentario = t.Comentario,
                    para = t.Para,
                    paraTodos = t.EmpleadoId == null,
                    total = s?.Total ?? 0,
                    hechos = s?.Hechos ?? 0,
                    terminadaAt = t.TerminadaAt,
                    terminadaPor = t.TerminadaPor
                };
            })
        });
    }

    /// <summary>Los productos de una tarea (sin el stock del sistema: los conteos son a ciegas).</summary>
    [HttpGet("celu/{token}/tarea/{id:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> CeluTarea(string token, int id)
    {
        var emp = await EmpleadoDelTokenAsync(token);
        if (emp is null) return NotFound();
        var t = await TareasDe(emp.Id).AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return NotFound();

        var items = await (from i in _db.DepTareaItems.AsNoTracking()
                           join p in _db.CafeProductos.AsNoTracking() on i.ProductoId equals p.Id
                           where i.TareaId == id
                           orderby i.Orden
                           select new
                           {
                               id = i.Id,
                               sku = p.Sku,
                               nombre = p.Nombre,
                               barcode = p.Barcode,
                               planta = p.UbicacionPlanta,
                               zona = p.UbicacionZona,
                               hecho = i.HechoAt != null,
                               contado = i.Contado,
                               noEncontrado = i.NoEncontrado,
                               valor = i.Valor
                           }).ToListAsync();

        var zonas = t.Tipo == "UBICACION"
            ? await _db.CafeUbicacionZonas.AsNoTracking().OrderBy(z => z.Planta).ThenBy(z => z.Codigo)
                .Select(z => new { planta = z.Planta, codigo = z.Codigo, nombre = z.Nombre }).ToListAsync()
            : null;

        return Ok(new
        {
            id = t.Id,
            tipo = t.Tipo,
            titulo = t.Titulo,
            comentario = t.Comentario,
            terminada = t.TerminadaAt != null,
            items = items.Select(i => new
            {
                i.id, i.sku, i.nombre, i.barcode, i.hecho, i.contado, i.noEncontrado, i.valor,
                lugar = UbicacionHelper.Texto(i.planta, i.zona)
            }),
            plantas = UbicacionHelper.Plantas.Select(p => new { codigo = p.Codigo, nombre = p.Nombre }),
            zonas
        });
    }

    public record CeluItemRequest(int? Contado, bool NoEncontrado, string? Planta, string? Zona, string? Barcode);

    /// <summary>Guarda lo que hicieron con un producto. CONTAR: solo anota (el stock no cambia).
    /// UBICACION y BARCODE: se cargan directo en el producto.</summary>
    [HttpPost("celu/{token}/item/{itemId:int}")]
    [AllowAnonymous]
    public async Task<IActionResult> CeluItem(string token, int itemId, [FromBody] CeluItemRequest req)
    {
        var emp = await EmpleadoDelTokenAsync(token);
        if (emp is null) return NotFound();
        var it = await _db.DepTareaItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (it is null) return NotFound();
        var t = await TareasDe(emp.Id).FirstOrDefaultAsync(x => x.Id == it.TareaId);
        if (t is null) return NotFound();
        if (it.Revision is not null) return BadRequest(new { error = "Ese ya lo revisó la oficina, no se puede cambiar." });
        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == it.ProductoId);
        if (prod is null) return NotFound();

        var quien = NombreCorto(emp.Nombre);
        it.NoEncontrado = req.NoEncontrado;
        it.Contado = null;
        it.StockAlContar = null;
        it.Valor = null;

        if (!req.NoEncontrado)
        {
            switch (t.Tipo)
            {
                case "CONTAR":
                    if (req.Contado is not int c || c < 0 || c > 1_000_000) return BadRequest(new { error = "Poné cuántos contaste." });
                    it.Contado = c;
                    it.StockAlContar = prod.StockUnidades;
                    break;
                case "UBICACION":
                {
                    var planta = (req.Planta ?? "").Trim().ToUpperInvariant();
                    if (!UbicacionHelper.PlantaValida(planta)) return BadRequest(new { error = "Elegí la planta." });
                    var zona = UbicacionHelper.NormalizarZona(req.Zona);
                    if (zona is not null && !await _db.CafeUbicacionZonas.AnyAsync(z => z.Planta == planta && z.Codigo == zona))
                        _db.CafeUbicacionZonas.Add(new CafeUbicacionZona { Planta = planta, Codigo = zona });
                    prod.UbicacionPlanta = planta;
                    prod.UbicacionZona = zona;
                    prod.UbicacionAt = DateTime.UtcNow;
                    prod.UbicacionPor = quien;
                    prod.UbicacionDudosaAt = null;
                    prod.UbicacionDudosaPor = null;
                    it.Valor = UbicacionHelper.Texto(planta, zona);
                    break;
                }
                case "BARCODE":
                {
                    var bc = new string((req.Barcode ?? "").Where(char.IsLetterOrDigit).ToArray());
                    if (bc.Length < 4 || bc.Length > 40) return BadRequest(new { error = "Ese código no parece bien." });
                    prod.Barcode = bc;
                    prod.UpdatedAt = DateTime.UtcNow;
                    it.Valor = bc;
                    break;
                }
            }
        }

        it.HechoAt = DateTime.UtcNow;
        it.HechoPor = quien;
        await _db.SaveChangesAsync();
        await CerrarSiTerminoAsync(t, quien);
        return Ok(new { ok = true, terminada = t.TerminadaAt != null });
    }

    /// <summary>"✓ Listo" de una tarea libre (o de una con productos, si terminaron todos).</summary>
    [HttpPost("celu/{token}/tarea/{id:int}/listo")]
    [AllowAnonymous]
    public async Task<IActionResult> CeluListo(string token, int id)
    {
        var emp = await EmpleadoDelTokenAsync(token);
        if (emp is null) return NotFound();
        var t = await TareasDe(emp.Id).FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return NotFound();
        if (t.Tipo != "LIBRE" && await _db.DepTareaItems.AnyAsync(i => i.TareaId == id && i.HechoAt == null))
            return BadRequest(new { error = "Todavía faltan productos." });
        t.TerminadaAt ??= DateTime.UtcNow;
        t.TerminadaPor ??= NombreCorto(emp.Nombre);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private async Task CerrarSiTerminoAsync(DepTarea t, string quien)
    {
        if (t.TerminadaAt is not null || t.Tipo == "LIBRE") return;
        if (await _db.DepTareaItems.AnyAsync(i => i.TareaId == t.Id && i.HechoAt == null)) return;
        t.TerminadaAt = DateTime.UtcNow;
        t.TerminadaPor = quien;
        await _db.SaveChangesAsync();
    }

    /// <summary>"Alexis madotta" → "Alexis"; "Osmar Leandro palanica jamkowy" → "Osmar".</summary>
    private static string NombreCorto(string nombre)
    {
        var n = (nombre ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        return n.Length == 0 ? nombre ?? "" : char.ToUpper(n[0]) + n[1..].ToLowerInvariant();
    }
}
