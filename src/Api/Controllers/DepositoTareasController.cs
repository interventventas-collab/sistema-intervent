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
/// conteos viejos que quedaron sin revisar, decidir si se corrige el stock ("Aceptar conteo").
/// 2026-11-03 (2ª vuelta): pedido del dueño "quiero que ya lo deje arreglado": desde ahora el conteo
/// CORRIGE el stock en el momento (y MercadoLibre), desde el celu o desde la PC del depósito
/// (botón 📋 Tareas en Imprimir pedidos). Queda anotado quién y en Stock_Movimientos.
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
    private readonly StockReservaService _reservas;

    public DepositoTareasController(AppDbContext db, IServiceScopeFactory scopeFactory, StockReservaService reservas)
    {
        _db = db; _scopeFactory = scopeFactory; _reservas = reservas;
    }

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
            case "codigos":
            {
                // 2026-11-03: pegar una lista de códigos (SKU o código de barras), separados por
                // espacios, comas o renglones. Ej: los de la hoja impresa que ya estaban contando a mano.
                var cods = (q ?? "").Split(new[] { ' ', ',', ';', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Take(300).ToList();
                if (cods.Count == 0) return Ok(Array.Empty<object>());
                var hall = await prods.Where(p => (p.Sku != null && cods.Contains(p.Sku)) || (p.Barcode != null && cods.Contains(p.Barcode)))
                    .Select(p => new { p.Id, p.Sku, p.Barcode }).ToListAsync();
                // en el orden en que los pegaron
                foreach (var c in cods)
                    foreach (var h in hall.Where(h => string.Equals(h.Sku, c, StringComparison.OrdinalIgnoreCase) || h.Barcode == c))
                        motivos.TryAdd(h.Id, "De la lista pegada");
                break;
            }
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

        // El café se lleva en GRAMOS: el conteo es de unidades, así que para contar queda afuera.
        if (tipoN0(tipo) == "CONTAR")
        {
            var enGramos = await _db.CafeProductos.AsNoTracking()
                .Where(p => motivos.Keys.Contains(p.Id) && (p.Categoria == "CAFE" || p.StockGramos != 0))
                .Select(p => p.Id).ToListAsync();
            foreach (var id in enGramos) motivos.Remove(id);
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

    private static string tipoN0(string? tipo) => (tipo ?? "").ToUpperInvariant();

    /// <summary>2026-11-03: cuánto se aleja lo contado de lo que decía el sistema. OK = igual;
    /// REVISA = hasta 2 unidades o hasta el 10%; MUCHA = más del 10% Y más de 2 unidades (en ese caso,
    /// antes de guardar se les pregunta si lo contaron bien). Mismos márgenes en Web (TareasNivel).</summary>
    public static string Nivel(int antes, int ahora)
    {
        var dif = Math.Abs(ahora - antes);
        if (dif == 0) return "OK";
        if (dif <= 2 || dif <= 0.10m * Math.Max(Math.Abs(antes), 1)) return "REVISA";
        return "MUCHA";
    }

    /// <summary>
    /// 2026-11-03: unidades VENDIDAS que todavía están en el estante. Pregunta del dueño: "¿y qué pasa si
    /// hay reservado de alguna venta?". Al cargar una venta el stock se descuenta en el momento, pero el
    /// producto sigue en el depósito hasta que lo arman/despachan. Si contaran eso y el stock pasara a ser
    /// lo contado, volverían a aparecer unidades ya vendidas. Se restan:
    ///  - MeLi: órdenes con la etiqueta sin imprimir (<see cref="StockReservaService"/>, el mismo criterio
    ///    del "reservado" de la carga de stock del celu).
    ///  - Ventas propias en el tablero del depósito SIN ARMAR (PARA_PREPARAR / EN_PREPARACION, mismos
    ///    7 días del tablero), sumando lo que su venta le descontó a ESTE producto (incluye las partes de
    ///    los compuestos y las ediciones).
    /// Lo ya ARMADO (LISTO) no se resta: la regla para el que cuenta es "no cuentes lo que ya está armado".
    /// </summary>
    private Task<(int Meli, int Propias)> ApartadosAsync(int productoId) => ApartadosDeAsync(_db, _reservas, productoId);

    /// <summary>2026-11-04: misma cuenta, reusable (la usa también "Asignar vencimientos" para arreglar stock).</summary>
    public static async Task<(int Meli, int Propias)> ApartadosDeAsync(AppDbContext _db, StockReservaService _reservas, int productoId)
    {
        var meli = await _reservas.GetReservaAsync(productoId);
        var desde = DateTime.UtcNow.Date.AddDays(-7);
        var numeros = await _db.CafeVentas.AsNoTracking()
            .Where(v => v.PreparacionOcultoAt == null && v.Estado != "anulado"
                && (v.EstadoPreparacion == "PARA_PREPARAR" || v.EstadoPreparacion == "EN_PREPARACION")
                && (v.CreatedAt >= desde || (v.PreparacionUpdatedAt != null && v.PreparacionUpdatedAt >= desde)))
            .Select(v => v.Numero).ToListAsync();
        var propias = 0;
        if (numeros.Count > 0)
        {
            var movs = await _db.StockMovimientos.AsNoTracking()
                .Where(m => m.ProductoId == productoId && !m.Reverted
                    && (m.TipoMov == "VENTA_NUESTRA" || m.TipoMov == "VENTA_NUESTRA_COMP" || m.TipoMov == "DEVUELTO_VENTA")
                    && m.CreatedAt >= desde.AddDays(-30))
                .Select(m => new { m.Comentario, m.StockAntes, m.StockDespues }).ToListAsync();
            foreach (var n in numeros)
            {
                var marca = "#" + n + " ";
                propias += movs.Where(m => m.Comentario != null && m.Comentario.Contains(marca)).Sum(m => m.StockAntes - m.StockDespues);
            }
        }
        return (meli, Math.Max(0, propias));
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

        // En memoria: "no coincide" = lo contado MENOS los vendidos sin armar (Valor) ≠ lo que decía el sistema.
        var filas = await _db.DepTareaItems.AsNoTracking().Where(i => ids.Contains(i.TareaId))
            .Select(i => new { i.TareaId, i.HechoAt, i.Contado, i.StockAlContar, i.NoEncontrado, i.Revision, i.Valor })
            .ToListAsync();
        bool Dif(int? contado, int? alContar, string? valor) =>
            contado is int c && Math.Max(0, c - (int.TryParse(valor, out var ap) ? ap : 0)) != alContar;
        var stats = filas.GroupBy(i => i.TareaId).Select(g => new
        {
            Id = g.Key,
            Total = g.Count(),
            Hechos = g.Count(i => i.HechoAt != null),
            NoEncontrados = g.Count(i => i.NoEncontrado),
            Diferencias = g.Count(i => i.HechoAt != null && Dif(i.Contado, i.StockAlContar, i.Valor)),
            Mucha = g.Count(i => i.HechoAt != null && i.Contado is int c && i.StockAlContar is int a
                && Nivel(a, Math.Max(0, c - (int.TryParse(i.Valor, out var ap) ? ap : 0))) == "MUCHA"),
            SinRevisar = g.Count(i => i.HechoAt != null && (i.NoEncontrado || Dif(i.Contado, i.StockAlContar, i.Valor)) && i.Revision == null)
        }).ToDictionary(x => x.Id);

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
                muchaDiferencia = s?.Mucha ?? 0,
                sinRevisar = s?.SinRevisar ?? 0
            };
        }));
    }

    /// <summary>Para el botón 📋 Tareas y el aviso de la pantalla del depósito (Imprimir pedidos).</summary>
    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen()
    {
        var tareas = await _db.DepTareas.AsNoTracking()
            .Where(t => t.CanceladaAt == null && t.TerminadaAt == null)
            .OrderBy(t => t.Para == "HOY" ? 0 : t.Para == "SEMANA" ? 1 : 2).ThenBy(t => t.CreatedAt)
            .Select(t => new { t.Titulo, t.EmpleadoId, t.Para })
            .ToListAsync();
        var emps = await _db.NomEmpleados.AsNoTracking().ToDictionaryAsync(e => e.Id, e => NombreCorto(e.Nombre));
        return Ok(new
        {
            pendientes = tareas.Count,
            lista = tareas.Take(5).Select(t => new
            {
                titulo = t.Titulo,
                empleado = t.EmpleadoId is int e && emps.TryGetValue(e, out var n) ? n : null,
                para = t.Para
            })
        });
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
                               productoId = p.Id,
                               sku = p.Sku,
                               nombre = p.Nombre,
                               barcode = p.Barcode,
                               planta = p.UbicacionPlanta,
                               zona = p.UbicacionZona,
                               hecho = i.HechoAt != null,
                               contado = i.Contado,
                               stockAntes = i.StockAlContar,
                               noEncontrado = i.NoEncontrado,
                               valor = i.Valor,
                               hechoPor = i.HechoPor,
                               hechoAt = i.HechoAt,
                               nombreSugerido = i.NombreSugerido,
                               nombreRevision = i.NombreRevision,
                               fotoReportadaAt = i.FotoReportadaAt,
                               fotoRevision = i.FotoRevision,
                               fotoPropuesta = i.FotoPropuestaArchivo
                           }).ToListAsync();

        // 2026-11-03: la foto de cada producto: la propia (Cafe_ProductoFoto) y, si no tiene, la de MeLi.
        var fotos = await FotosAsync(items.Select(i => i.productoId).ToList());

        // las zonas van siempre: mientras cuentan también pueden corregir dónde está
        var zonas = await _db.CafeUbicacionZonas.AsNoTracking().OrderBy(z => z.Planta).ThenBy(z => z.Codigo)
            .Select(z => new { planta = z.Planta, codigo = z.Codigo, nombre = z.Nombre }).ToListAsync();

        return Ok(new
        {
            id = t.Id,
            tipo = t.Tipo,
            titulo = t.Titulo,
            comentario = t.Comentario,
            terminada = t.TerminadaAt != null,
            items = items.Select(i => new
            {
                i.id, i.sku, i.nombre, i.barcode, i.hecho, i.contado, i.noEncontrado, i.valor, i.hechoPor, i.hechoAt,
                i.nombreSugerido, i.nombreRevision, i.planta, i.zona,
                foto = fotos.TryGetValue(i.productoId, out var f) ? f.Url : null,
                fotoPropia = fotos.TryGetValue(i.productoId, out var f2) && f2.Propia,
                // "📷 foto en revisión": la reportaron y el dueño todavía no la miró
                fotoEnRevision = i.fotoReportadaAt != null && i.fotoRevision == null,
                fotoRevision = i.fotoRevision,
                fotoPropuesta = i.fotoPropuesta is null ? null : $"api/deposito-tareas/foto-propuesta/{i.fotoPropuesta}",
                // lo que decía el sistema: recién DESPUÉS de contar (mientras cuentan, a ciegas)
                stockAntes = i.hecho ? i.stockAntes : null,
                // CONTAR: unidades vendidas sin armar que se restaron a lo contado (guardadas en Valor)
                apartados = t.Tipo == "CONTAR" && int.TryParse(i.valor, out var ap) ? ap : 0,
                lugar = UbicacionHelper.Texto(i.planta, i.zona)
            }),
            plantas = UbicacionHelper.Plantas.Select(p => new { codigo = p.Codigo, nombre = p.Nombre }),
            zonas
        });
    }

    public record CeluItemRequest(int? Contado, bool NoEncontrado, string? Planta, string? Zona, string? Barcode, bool Confirmado = false);

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
        if (it.Revision is "ACEPTADO" or "DESCARTADO") return BadRequest(new { error = "Ese ya lo revisó la oficina, no se puede cambiar." });
        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == it.ProductoId);
        if (prod is null) return NotFound();

        // 2026-11-03: si la diferencia es MUCHA, antes de tocar el stock se les pregunta si lo contaron
        // bien (frena los errores de tipeo: 30 en vez de 300). No se guarda nada hasta que confirman.
        if (t.Tipo == "CONTAR" && !req.NoEncontrado && !req.Confirmado && req.Contado is int cc && cc >= 0)
        {
            var (m0, p0) = await ApartadosAsync(prod.Id);
            var ahora0 = Math.Max(0, cc - m0 - p0);
            if (Nivel(prod.StockUnidades, ahora0) == "MUCHA")
                return Ok(new { ok = false, confirmar = true, stockAntes = prod.StockUnidades, stockDespues = ahora0, apartados = m0 + p0 });
        }

        var quien = NombreCorto(emp.Nombre);
        it.NoEncontrado = req.NoEncontrado;
        it.Contado = null;
        it.StockAlContar = null;
        it.Valor = null;
        it.Revision = null;
        int? stockAntes = null, stockDespues = null;

        if (!req.NoEncontrado)
        {
            switch (t.Tipo)
            {
                case "CONTAR":
                    if (req.Contado is not int c || c < 0 || c > 1_000_000) return BadRequest(new { error = "Poné cuántos contaste." });
                    it.Contado = c;
                    it.StockAlContar = prod.StockUnidades;
                    // Queda arreglado en el momento: el stock pasa a ser lo que contaron MENOS lo que ya
                    // está vendido y sigue en el estante (ver ApartadosAsync).
                    var (apMeli, apPropias) = await ApartadosAsync(prod.Id);
                    var apartados = apMeli + apPropias;
                    if (apartados > 0) it.Valor = apartados.ToString();
                    stockAntes = prod.StockUnidades;
                    stockDespues = Math.Max(0, c - apartados);
                    if (stockAntes != stockDespues)
                    {
                        prod.StockUnidades = stockDespues.Value;
                        prod.UpdatedAt = DateTime.UtcNow;
                        prod.StockChangedAt = DateTime.UtcNow;
                        await CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
                        _db.StockMovimientos.Add(new StockMovimiento
                        {
                            ProductoId = prod.Id,
                            OperadorNombreSnap = quien,
                            DepositoNombreSnap = "9 de Abril",
                            TipoMov = "SET",
                            Cantidad = stockDespues.Value,
                            StockAntes = stockAntes.Value,
                            StockDespues = stockDespues.Value,
                            Comentario = $"Conteo de la tarea #{t.Id} ({t.Titulo}): contaron {c}"
                                + (apartados > 0 ? $" − {apartados} vendidos sin armar" + (apMeli > 0 ? $" ({apMeli} MeLi)" : "") : ""),
                            CreatedAt = DateTime.UtcNow
                        });
                    }
                    it.Revision = "AUTO";
                    it.RevisionAt = DateTime.UtcNow;
                    it.RevisionPor = quien;
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
        if (stockAntes is int a && stockDespues is int d && a != d) FireAndForgetPushMeli(prod.Id);
        return Ok(new { ok = true, terminada = t.TerminadaAt != null, stockAntes, stockDespues, apartados = t.Tipo == "CONTAR" && int.TryParse(it.Valor, out var apx) ? apx : 0 });
    }

    public record CeluLugarRequest(string? Planta, string? Zona);

    /// <summary>2026-11-03: mientras cuentan, "📍 está en otro lugar": corrige la ubicación del producto
    /// en el momento (no hace falta aprobación), sin marcar el producto como hecho.</summary>
    [HttpPost("celu/{token}/item/{itemId:int}/lugar")]
    [AllowAnonymous]
    public async Task<IActionResult> CeluLugar(string token, int itemId, [FromBody] CeluLugarRequest req)
    {
        var emp = await EmpleadoDelTokenAsync(token);
        if (emp is null) return NotFound();
        var it = await _db.DepTareaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId);
        if (it is null || !await TareasDe(emp.Id).AnyAsync(x => x.Id == it.TareaId)) return NotFound();
        var planta = (req?.Planta ?? "").Trim().ToUpperInvariant();
        if (!UbicacionHelper.PlantaValida(planta)) return BadRequest(new { error = "Elegí la planta." });
        var zona = UbicacionHelper.NormalizarZona(req!.Zona);
        if (zona is not null && !await _db.CafeUbicacionZonas.AnyAsync(z => z.Planta == planta && z.Codigo == zona))
            _db.CafeUbicacionZonas.Add(new CafeUbicacionZona { Planta = planta, Codigo = zona });
        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == it.ProductoId);
        if (prod is null) return NotFound();
        prod.UbicacionPlanta = planta;
        prod.UbicacionZona = zona;
        prod.UbicacionAt = DateTime.UtcNow;
        prod.UbicacionPor = NombreCorto(emp.Nombre);
        prod.UbicacionDudosaAt = null;
        prod.UbicacionDudosaPor = null;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, lugar = UbicacionHelper.Texto(planta, zona) });
    }

    public record CeluNombreRequest(string? Nombre);

    /// <summary>2026-11-03: "✏️ proponer otro nombre". NO cambia el producto: queda pendiente hasta que el
    /// dueño lo aprueba en Tareas del depósito (pedido: "que lo pueda hacer pero que me pida aprobación").</summary>
    [HttpPost("celu/{token}/item/{itemId:int}/nombre")]
    [AllowAnonymous]
    public async Task<IActionResult> CeluNombre(string token, int itemId, [FromBody] CeluNombreRequest req)
    {
        var emp = await EmpleadoDelTokenAsync(token);
        if (emp is null) return NotFound();
        var it = await _db.DepTareaItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (it is null || !await TareasDe(emp.Id).AnyAsync(x => x.Id == it.TareaId)) return NotFound();
        var nombre = string.Join(' ', (req?.Nombre ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (nombre.Length < 3) return BadRequest(new { error = "Escribí el nombre nuevo." });
        if (nombre.Length > 300) nombre = nombre[..300];
        it.NombreSugerido = nombre;
        it.NombreSugeridoPor = NombreCorto(emp.Nombre);
        it.NombreSugeridoAt = DateTime.UtcNow;
        it.NombreRevision = null;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Nombres propuestos desde el depósito que esperan aprobación.</summary>
    [HttpGet("nombres-pendientes")]
    public async Task<IActionResult> NombresPendientes()
    {
        var filas = await (from i in _db.DepTareaItems.AsNoTracking()
                           join p in _db.CafeProductos.AsNoTracking() on i.ProductoId equals p.Id
                           where i.NombreSugerido != null && i.NombreRevision == null
                           orderby i.NombreSugeridoAt
                           select new
                           {
                               itemId = i.Id,
                               productoId = p.Id,
                               sku = p.Sku,
                               nombreActual = p.Nombre,
                               nombrePropuesto = i.NombreSugerido,
                               propuestoPor = i.NombreSugeridoPor,
                               propuestoAt = i.NombreSugeridoAt
                           }).ToListAsync();
        return Ok(filas);
    }

    public record AprobarNombreRequest(string? Nombre);

    /// <summary>Aprueba el nombre (se puede retocar antes de aprobar) y lo pone en el producto.</summary>
    [HttpPost("items/{itemId:int}/nombre/aprobar")]
    public async Task<IActionResult> AprobarNombre(int itemId, [FromBody] AprobarNombreRequest? req)
    {
        var it = await _db.DepTareaItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (it?.NombreSugerido is null) return NotFound();
        var nombre = string.IsNullOrWhiteSpace(req?.Nombre) ? it.NombreSugerido : req!.Nombre!.Trim();
        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == it.ProductoId);
        if (prod is null) return NotFound();
        var viejo = prod.Nombre;
        prod.Nombre = nombre.Length > 300 ? nombre[..300] : nombre;
        prod.UpdatedAt = DateTime.UtcNow;
        it.NombreRevision = "APROBADO";
        it.NombreRevisionAt = DateTime.UtcNow;
        it.NombreRevisionPor = Usuario;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, antes = viejo, ahora = prod.Nombre });
    }

    [HttpPost("items/{itemId:int}/nombre/rechazar")]
    public async Task<IActionResult> RechazarNombre(int itemId)
    {
        var it = await _db.DepTareaItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (it?.NombreSugerido is null) return NotFound();
        it.NombreRevision = "RECHAZADO";
        it.NombreRevisionAt = DateTime.UtcNow;
        it.NombreRevisionPor = Usuario;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private const string FotosDir = "/data/files/producto-fotos";

    /// <summary>productoId → (url de la foto, es propia). Propia = la que sacaron en el depósito
    /// (/api/public/producto-foto/img/...); si no hay, la miniatura de su publicación de MeLi.</summary>
    private async Task<Dictionary<int, (string Url, bool Propia)>> FotosAsync(List<int> ids)
    {
        var res = new Dictionary<int, (string, bool)>();
        if (ids.Count == 0) return res;
        var regs = await _db.CafeProductoFotos.AsNoTracking()
            .Where(f => ids.Contains(f.CafeProductoId))
            .Select(f => new { f.CafeProductoId, f.FotoPropiaArchivo, f.Estado }).ToListAsync();
        foreach (var f in regs.Where(f => f.FotoPropiaArchivo != null))
            res[f.CafeProductoId] = ($"api/public/producto-foto/img/{f.FotoPropiaArchivo}", true);
        // REPORTADA sin foto propia = la de MeLi está mal y no se muestra en el sistema
        var meliOculta = regs.Where(f => f.FotoPropiaArchivo == null && f.Estado == "REPORTADA").Select(f => f.CafeProductoId).ToHashSet();
        var faltan = ids.Where(id => !res.ContainsKey(id) && !meliOculta.Contains(id)).ToList();
        if (faltan.Count > 0)
        {
            var meli = await (from c in _db.MeliItemComponentes.AsNoTracking()
                              join m in _db.MeliItems.AsNoTracking() on c.MeliItemId equals m.MeliItemId
                              where faltan.Contains(c.CafeProductoId) && m.Thumbnail != null
                              orderby m.Status == "active" descending, m.SoldQuantity descending
                              select new { c.CafeProductoId, m.Thumbnail }).ToListAsync();
            foreach (var m in meli)
                if (!res.ContainsKey(m.CafeProductoId))
                    res[m.CafeProductoId] = (m.Thumbnail!.Replace("http://", "https://"), false);
        }
        return res;
    }

    /// <summary>2026-11-03: "📷 la foto está mal" desde la tarea (celu o PC del depósito). Pedido del dueño:
    /// "que puedan reportar la imagen que está mal y subir una nueva en el momento, con enlace o sacando
    /// foto; yo solo la apruebo, y mientras tanto aparece como foto en revisión". Se puede mandar solo el
    /// aviso, o el aviso + una foto (archivo/cámara) o un link. NADA cambia hasta que el dueño aprueba.</summary>
    [HttpPost("celu/{token}/item/{itemId:int}/foto")]
    [AllowAnonymous]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<IActionResult> CeluFoto(string token, int itemId, [FromForm] IFormFile? file, [FromForm] string? motivo, [FromForm] string? url)
    {
        var emp = await EmpleadoDelTokenAsync(token);
        if (emp is null) return NotFound();
        var it = await _db.DepTareaItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (it is null || !await TareasDe(emp.Id).AnyAsync(x => x.Id == it.TareaId)) return NotFound();

        byte[]? bytes = null;
        string? ext = null;
        if (file is not null && file.Length > 0)
        {
            if (file.Length > 12 * 1024 * 1024) return BadRequest(new { error = "La foto es muy grande." });
            if (!file.ContentType.StartsWith("image/")) return BadRequest(new { error = "Tiene que ser una foto." });
            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);
            bytes = ms.ToArray();
            ext = Path.GetExtension(file.FileName);
        }
        else if (!string.IsNullOrWhiteSpace(url))
        {
            // Este link lo pega alguien SIN login (el link del celu): solo direcciones públicas de internet,
            // nunca máquinas de adentro (localhost, IPs privadas, nombres de contenedores sin punto).
            if (!await LinkPublicoAsync(url)) return BadRequest(new { error = "Ese link no sirve. Copiá el link de la imagen (que empiece con https)." });
            var (b, e, error) = await CafeProductoFotoController.BajarImagenAsync(url);
            if (error is not null) return BadRequest(new { error });
            bytes = b; ext = e;
        }

        string? nuevo = null;
        if (bytes is not null)
        {
            Directory.CreateDirectory(FotosDir);
            if (string.IsNullOrEmpty(ext) || ext.Length > 6) ext = ".jpg";
            nuevo = $"propuesta-{it.ProductoId}-{Guid.NewGuid():N}{ext}";
            await System.IO.File.WriteAllBytesAsync(Path.Combine(FotosDir, nuevo), bytes);
        }
        // si ya había una propuesta pendiente, la reemplaza
        if (it.FotoRevision == null) BorrarFoto(it.FotoPropuestaArchivo);
        it.FotoPropuestaArchivo = nuevo;
        it.FotoMotivo = string.IsNullOrWhiteSpace(motivo) ? null : (motivo.Trim().Length > 300 ? motivo.Trim()[..300] : motivo.Trim());
        it.FotoReportadaAt = DateTime.UtcNow;
        it.FotoReportadaPor = NombreCorto(emp.Nombre);
        it.FotoRevision = null; it.FotoRevisionAt = null; it.FotoRevisionPor = null;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, conFoto = nuevo != null });
    }

    private static async Task<bool> LinkPublicoAsync(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)) return false;
        if (u.Scheme != Uri.UriSchemeHttps && u.Scheme != Uri.UriSchemeHttp) return false;
        if (!u.Host.Contains('.') || u.IsLoopback) return false;
        try
        {
            var ips = await System.Net.Dns.GetHostAddressesAsync(u.Host);
            if (ips.Length == 0) return false;
            foreach (var ip in ips)
            {
                if (System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;
                var b = ip.MapToIPv4().GetAddressBytes();
                if (b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)) return false;
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>La foto propuesta (todavía sin aprobar). Pública como las demás fotos de producto.</summary>
    [HttpGet("foto-propuesta/{archivo}")]
    [AllowAnonymous]
    public IActionResult FotoPropuesta(string archivo)
    {
        if (!archivo.StartsWith("propuesta-") || archivo.Contains('/') || archivo.Contains("..")) return NotFound();
        var path = Path.Combine(FotosDir, archivo);
        if (!System.IO.File.Exists(path)) return NotFound();
        var ct = Path.GetExtension(archivo).ToLowerInvariant() switch { ".png" => "image/png", ".webp" => "image/webp", ".gif" => "image/gif", _ => "image/jpeg" };
        return PhysicalFile(path, ct);
    }

    private static void BorrarFoto(string? archivo)
    {
        if (string.IsNullOrEmpty(archivo)) return;
        try { var path = Path.Combine(FotosDir, archivo); if (System.IO.File.Exists(path)) System.IO.File.Delete(path); }
        catch { /* best-effort */ }
    }

    /// <summary>Aprueba el reporte de foto. Con foto nueva: pasa a ser la foto propia del producto (la vieja
    /// propia se borra). Sin foto nueva: se saca la que estaba mal (la propia se borra; si era la de MeLi,
    /// se marca REPORTADA = no se muestra en el sistema). No toca MercadoLibre.</summary>
    [HttpPost("items/{itemId:int}/foto/aprobar")]
    public async Task<IActionResult> AprobarFoto(int itemId)
    {
        var it = await _db.DepTareaItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (it?.FotoReportadaAt is null) return NotFound();
        var foto = await _db.CafeProductoFotos.FirstOrDefaultAsync(f => f.CafeProductoId == it.ProductoId);
        if (foto is null) { foto = new CafeProductoFoto { CafeProductoId = it.ProductoId }; _db.CafeProductoFotos.Add(foto); }
        var viejo = foto.FotoPropiaArchivo;
        var originalViejo = foto.FotoOriginalArchivo;
        if (it.FotoPropuestaArchivo is not null)
        {
            // la propuesta pasa a ser la foto propia (se renombra a prod-… como las demás)
            var ext = Path.GetExtension(it.FotoPropuestaArchivo);
            var final = $"prod-{it.ProductoId}-{Guid.NewGuid():N}{ext}";
            try { System.IO.File.Move(Path.Combine(FotosDir, it.FotoPropuestaArchivo), Path.Combine(FotosDir, final)); }
            catch { return BadRequest(new { error = "No encontré la foto nueva, pedí que la vuelvan a mandar." }); }
            foto.FotoPropiaArchivo = final;
            foto.FotoPropiaAt = DateTime.UtcNow;
            foto.Estado = "APROBADA";
            it.FotoPropuestaArchivo = null;
        }
        else
        {
            foto.FotoPropiaArchivo = null;
            foto.Estado = "REPORTADA";
        }
        foto.FotoOriginalArchivo = null;
        foto.Comentario = it.FotoMotivo;
        foto.Usuario = it.FotoReportadaPor;
        foto.UpdatedAt = DateTime.UtcNow;
        it.FotoRevision = "APROBADA";
        it.FotoRevisionAt = DateTime.UtcNow;
        it.FotoRevisionPor = Usuario;
        await _db.SaveChangesAsync();
        BorrarFoto(viejo);
        BorrarFoto(originalViejo);
        return Ok(new { ok = true });
    }

    [HttpPost("items/{itemId:int}/foto/rechazar")]
    public async Task<IActionResult> RechazarFoto(int itemId)
    {
        var it = await _db.DepTareaItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (it?.FotoReportadaAt is null) return NotFound();
        BorrarFoto(it.FotoPropuestaArchivo);
        it.FotoPropuestaArchivo = null;
        it.FotoRevision = "RECHAZADA";
        it.FotoRevisionAt = DateTime.UtcNow;
        it.FotoRevisionPor = Usuario;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ════════════════════════════════════════════════════════════════════
    // AVISOS (botón flotante 📋 del dueño)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>2026-11-03: lo que el dueño tiene para mirar (nombres propuestos, diferencias grandes,
    /// "no lo encuentro", tareas terminadas sin ver) + lo que está en curso. Pedido: "que me lo notifique
    /// en el botón flotante y poder enviarlas desde ahí".</summary>
    [HttpGet("avisos")]
    public async Task<IActionResult> Avisos()
    {
        var emps = await _db.NomEmpleados.AsNoTracking().ToDictionaryAsync(e => e.Id, e => NombreCorto(e.Nombre));
        var abiertas = await _db.DepTareas.AsNoTracking().Where(t => t.CanceladaAt == null).Select(t => t.Id).ToListAsync();

        var filas = await (from i in _db.DepTareaItems.AsNoTracking()
                           join p in _db.CafeProductos.AsNoTracking() on i.ProductoId equals p.Id
                           where abiertas.Contains(i.TareaId) && i.HechoAt != null
                                 && ((i.Revision == "AUTO" && i.Contado != null) || (i.NoEncontrado && i.Revision == null))
                           orderby i.HechoAt descending
                           select new { i.Id, i.TareaId, p.Sku, p.Nombre, i.Contado, i.StockAlContar, i.Valor, i.NoEncontrado, i.HechoPor, i.HechoAt })
                          .Take(300).ToListAsync();
        var mucha = filas.Where(f => !f.NoEncontrado && f.StockAlContar is int a
                && Nivel(a, Math.Max(0, (f.Contado ?? 0) - (int.TryParse(f.Valor, out var ap) ? ap : 0))) == "MUCHA")
            .Select(f => new
            {
                itemId = f.Id, tareaId = f.TareaId, sku = f.Sku, nombre = f.Nombre,
                decia = f.StockAlContar, ahora = Math.Max(0, (f.Contado ?? 0) - (int.TryParse(f.Valor, out var ap2) ? ap2 : 0)),
                quien = f.HechoPor, at = f.HechoAt
            }).ToList();
        var noEncontrados = filas.Where(f => f.NoEncontrado)
            .Select(f => new { itemId = f.Id, tareaId = f.TareaId, sku = f.Sku, nombre = f.Nombre, quien = f.HechoPor, at = f.HechoAt }).ToList();

        var nombres = await (from i in _db.DepTareaItems.AsNoTracking()
                             join p in _db.CafeProductos.AsNoTracking() on i.ProductoId equals p.Id
                             where i.NombreSugerido != null && i.NombreRevision == null
                             orderby i.NombreSugeridoAt
                             select new
                             {
                                 itemId = i.Id, productoId = p.Id, sku = p.Sku, nombreActual = p.Nombre,
                                 nombrePropuesto = i.NombreSugerido, propuestoPor = i.NombreSugeridoPor, propuestoAt = i.NombreSugeridoAt
                             }).ToListAsync();

        var fotosRep = await (from i in _db.DepTareaItems.AsNoTracking()
                              join p in _db.CafeProductos.AsNoTracking() on i.ProductoId equals p.Id
                              where i.FotoReportadaAt != null && i.FotoRevision == null
                              orderby i.FotoReportadaAt
                              select new { i.Id, as_ = p.Id, p.Sku, p.Nombre, i.FotoMotivo, i.FotoPropuestaArchivo, i.FotoReportadaPor, i.FotoReportadaAt })
                             .ToListAsync();
        var fotosAct = await FotosAsync(fotosRep.Select(f => f.as_).Distinct().ToList());
        var fotos = fotosRep.Select(f => new
        {
            itemId = f.Id, sku = f.Sku, nombre = f.Nombre, motivo = f.FotoMotivo,
            fotoActual = fotosAct.TryGetValue(f.as_, out var fa) ? fa.Url : null,
            fotoNueva = f.FotoPropuestaArchivo is null ? null : $"api/deposito-tareas/foto-propuesta/{f.FotoPropuestaArchivo}",
            quien = f.FotoReportadaPor, at = f.FotoReportadaAt
        }).ToList();

        var tareas = await _db.DepTareas.AsNoTracking()
            .Where(t => t.CanceladaAt == null && (t.TerminadaAt == null || t.VistaAt == null))
            .OrderBy(t => t.TerminadaAt == null).ThenByDescending(t => t.TerminadaAt).ThenBy(t => t.CreatedAt)
            .Take(30).ToListAsync();
        var ids = tareas.Select(t => t.Id).ToList();
        var stats = (await _db.DepTareaItems.AsNoTracking().Where(i => ids.Contains(i.TareaId))
                .Select(i => new { i.TareaId, i.HechoAt }).ToListAsync())
            .GroupBy(i => i.TareaId).ToDictionary(g => g.Key, g => (Total: g.Count(), Hechos: g.Count(i => i.HechoAt != null)));
        object Tarea(DepTarea t)
        {
            stats.TryGetValue(t.Id, out var s);
            return new
            {
                id = t.Id, tipo = t.Tipo, titulo = t.Titulo, para = t.Para,
                empleado = t.EmpleadoId is int e && emps.TryGetValue(e, out var n) ? n : "Cualquiera",
                total = s.Total, hechos = s.Hechos, terminadaAt = t.TerminadaAt, terminadaPor = t.TerminadaPor
            };
        }
        var terminadas = tareas.Where(t => t.TerminadaAt != null).Select(Tarea).ToList();
        var enCurso = tareas.Where(t => t.TerminadaAt == null).Select(Tarea).ToList();

        return Ok(new
        {
            pendientes = nombres.Count + fotos.Count + mucha.Count + noEncontrados.Count + terminadas.Count,
            nombres, fotos, mucha, noEncontrados, terminadas, enCurso
        });
    }

    /// <summary>"Visto": la diferencia grande o el "no lo encuentro" ya lo miró el dueño.</summary>
    [HttpPost("items/{itemId:int}/visto")]
    public async Task<IActionResult> ItemVisto(int itemId)
    {
        var it = await _db.DepTareaItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (it is null) return NotFound();
        it.Revision = "VISTO";
        it.RevisionAt = DateTime.UtcNow;
        it.RevisionPor = Usuario;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>"Pedir que lo recuente": el producto vuelve a quedar pendiente en su celu (y la tarea se
    /// reabre si estaba terminada). El stock queda como está hasta que lo vuelvan a contar.</summary>
    [HttpPost("items/{itemId:int}/recontar")]
    public async Task<IActionResult> ItemRecontar(int itemId)
    {
        var it = await _db.DepTareaItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (it is null) return NotFound();
        var t = await _db.DepTareas.FirstOrDefaultAsync(x => x.Id == it.TareaId);
        if (t is null || t.CanceladaAt != null) return BadRequest(new { error = "Esa tarea ya no está." });
        it.HechoAt = null; it.HechoPor = null; it.Contado = null; it.StockAlContar = null; it.Valor = null;
        it.NoEncontrado = false; it.Revision = null; it.RevisionAt = null; it.RevisionPor = null;
        t.TerminadaAt = null; t.TerminadaPor = null; t.VistaAt = null;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>"Visto" de una tarea terminada (deja de avisar).</summary>
    [HttpPost("{id:int}/visto")]
    public async Task<IActionResult> TareaVista(int id)
    {
        var t = await _db.DepTareas.FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return NotFound();
        t.VistaAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
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
