using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-04: "Asignar vencimiento". Cada producto puede tener cantidades con fecha de vencimiento;
/// lo que queda de cada fecha lo calcula VencimientosService con el stock actual (sale primero lo que
/// vence antes). Si un producto no tiene nada asignado, no aparece en ningún lado.
/// </summary>
[ApiController]
[Route("api/cafe/vencimientos")]
[Authorize]
public class CafeVencimientosController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StockReservaService _reservas;
    public CafeVencimientosController(AppDbContext db, IServiceScopeFactory scopeFactory, StockReservaService reservas)
    { _db = db; _scopeFactory = scopeFactory; _reservas = reservas; }

    public record FilaDto(int Id, decimal Cantidad, DateTime FechaVence, decimal Quedan);
    public record ProductoVencDto(int ProductoId, string? Sku, string Nombre, string? Marca, string? Ubicacion,
        bool EsCafe, decimal Stock, List<FilaDto> Filas, DateTime? ProximoVence, decimal ProximoQuedan,
        DateTime? SiguienteVence, decimal SinVencimiento, decimal Asignado)
    {
        // 2026-11-04: la tarjeta junta 4 trabajos (stock, lugar, foto, vencimientos)
        public bool LugarDudoso { get; init; }
        public string? FotoPropiaArchivo { get; init; }
        public string? FotoEstado { get; init; }
        public string? MeliThumb { get; init; }
        public DateTime? UltimoConteoAt { get; init; }
        public string? UltimoConteoPor { get; init; }
    }
    public record ContarRequest(int Contado, bool Confirmado, bool Coincide = false);
    public record AsignarRequest(decimal Cantidad, DateTime FechaVence);
    public record AlertaDto(int ProductoId, string? Sku, string Nombre, bool EsCafe, DateTime FechaVence, decimal Quedan, int Dias);

    private string Operador()
    {
        var op = HttpContext.Request.Headers["X-Operator-Name"].ToString();
        return string.IsNullOrWhiteSpace(op) ? (User?.Identity?.Name ?? "Sistema") : op;
    }

    private record Extra(string? FotoPropia, string? FotoEstado, string? Thumb, DateTime? ConteoAt, string? ConteoPor);

    /// <summary>Foto (propia o de MeLi) y último conteo (movimiento "Conteo…" de los últimos 60 días) de varios productos.</summary>
    private async Task<Dictionary<int, Extra>> ExtrasAsync(List<int> ids)
    {
        var res = new Dictionary<int, Extra>();
        if (ids.Count == 0) return res;
        var fotos = await _db.CafeProductoFotos.AsNoTracking().Where(f => ids.Contains(f.CafeProductoId))
            .Select(f => new { f.CafeProductoId, f.FotoPropiaArchivo, f.Estado }).ToListAsync();
        var thumbsDir = await _db.MeliItems.AsNoTracking()
            .Where(m => m.CafeProductoId != null && ids.Contains(m.CafeProductoId.Value) && m.Thumbnail != null && m.Status != "deleted")
            .OrderBy(m => m.Status == "active" ? 0 : 1)
            .Select(m => new { Pid = m.CafeProductoId!.Value, m.Thumbnail }).ToListAsync();
        var thumbsComp = await (from c in _db.MeliItemComponentes
                                join m in _db.MeliItems on c.MeliItemId equals m.MeliItemId
                                where ids.Contains(c.CafeProductoId) && m.Thumbnail != null && m.Status != "deleted"
                                orderby m.Status == "active" ? 0 : 1
                                select new { Pid = c.CafeProductoId, m.Thumbnail }).ToListAsync();
        var desde = DateTime.UtcNow.AddDays(-60);
        var conteos = await _db.StockMovimientos.AsNoTracking()
            .Where(m => ids.Contains(m.ProductoId) && m.CreatedAt >= desde && m.Comentario != null && m.Comentario.StartsWith("Conteo"))
            .Select(m => new { m.ProductoId, m.CreatedAt, m.OperadorNombreSnap })
            .ToListAsync();
        conteos = conteos.OrderByDescending(m => m.CreatedAt).ToList();
        foreach (var id in ids)
        {
            var f = fotos.FirstOrDefault(x => x.CafeProductoId == id);
            var th = thumbsDir.FirstOrDefault(x => x.Pid == id)?.Thumbnail ?? thumbsComp.FirstOrDefault(x => x.Pid == id)?.Thumbnail;
            var c = conteos.FirstOrDefault(x => x.ProductoId == id);
            res[id] = new Extra(f?.FotoPropiaArchivo, f?.Estado, th, c?.CreatedAt, c?.OperadorNombreSnap);
        }
        return res;
    }

    private static ProductoVencDto Map(CafeProducto p, VencimientosService.VencResumen? r, Extra? x = null)
    {
        var esCafe = VencimientosService.EsCafe(p);
        var stock = VencimientosService.StockDe(p);
        r ??= VencimientosService.Calcular(p.Id, esCafe, stock, Array.Empty<CafeProductoVencimiento>());
        var ubic = string.Join(" · ", new[] { p.UbicacionPlanta, p.UbicacionZona }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new ProductoVencDto(p.Id, p.Sku, p.Nombre, p.Marca ?? p.MarcaNav?.Nombre, ubic.Length > 0 ? ubic : null,
            esCafe, stock, r.Filas.Select(f => new FilaDto(f.Id, f.Cantidad, f.FechaVence, f.Quedan)).ToList(),
            r.ProximoVence, r.ProximoQuedan, r.SiguienteVence, r.SinVencimiento, r.Asignado)
        {
            LugarDudoso = p.UbicacionDudosaAt != null,
            FotoPropiaArchivo = x?.FotoPropia, FotoEstado = x?.FotoEstado, MeliThumb = x?.Thumb,
            UltimoConteoAt = x?.ConteoAt, UltimoConteoPor = x?.ConteoPor
        };
    }

    private async Task<ProductoVencDto?> CargarAsync(int productoId)
    {
        var p = await _db.CafeProductos.AsNoTracking().Include(x => x.MarcaNav).FirstOrDefaultAsync(x => x.Id == productoId);
        if (p is null) return null;
        var res = await VencimientosService.ResumenAsync(_db, new[] { productoId });
        var ex = await ExtrasAsync(new List<int> { productoId });
        return Map(p, res.GetValueOrDefault(productoId), ex.GetValueOrDefault(productoId));
    }

    // ─── GET un producto ───
    [HttpGet("producto/{productoId:int}")]
    public async Task<IActionResult> GetProducto(int productoId)
    {
        var dto = await CargarAsync(productoId);
        return dto is null ? NotFound(new { error = "Producto no encontrado" }) : Ok(dto);
    }

    // ─── POST asignar vencimiento ───
    [HttpPost("producto/{productoId:int}")]
    public async Task<IActionResult> Asignar(int productoId, [FromBody] AsignarRequest req)
    {
        if (req.Cantidad <= 0) return BadRequest(new { error = "Poné cuántos vencen en esa fecha" });
        if (req.FechaVence.Year < 2000) return BadRequest(new { error = "Poné la fecha de vencimiento" });
        if (!await _db.CafeProductos.AnyAsync(p => p.Id == productoId)) return NotFound(new { error = "Producto no encontrado" });
        _db.CafeProductoVencimientos.Add(new CafeProductoVencimiento
        {
            ProductoId = productoId, Cantidad = req.Cantidad, FechaVence = req.FechaVence.Date,
            CreatedAt = DateTime.UtcNow, CreatedBy = Operador()
        });
        await _db.SaveChangesAsync();
        return Ok(await CargarAsync(productoId));
    }

    // ─── PUT corregir uno ───
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] AsignarRequest req)
    {
        var v = await _db.CafeProductoVencimientos.FindAsync(id);
        if (v is null) return NotFound(new { error = "No encontrado" });
        if (req.Cantidad <= 0) return BadRequest(new { error = "La cantidad tiene que ser mayor a cero" });
        if (req.FechaVence.Year < 2000) return BadRequest(new { error = "Poné la fecha de vencimiento" });
        v.Cantidad = req.Cantidad; v.FechaVence = req.FechaVence.Date; v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(await CargarAsync(v.ProductoId));
    }

    // ─── POST contar: arreglar el stock con lo contado (misma regla que el conteo de Tareas) ───
    /// <summary>2026-11-04: el stock pasa a lo contado MENOS lo vendido que sigue en el estante (MeLi con
    /// etiqueta sin imprimir + ventas propias sin armar). Si la diferencia es mucha, primero pide confirmar.
    /// Si coincide, deja igual un movimiento "CONTEO" (sin cambio) para saber que se contó.</summary>
    [HttpPost("producto/{productoId:int}/contar")]
    public async Task<IActionResult> Contar(int productoId, [FromBody] ContarRequest req)
    {
        var prod = await _db.CafeProductos.FirstOrDefaultAsync(p => p.Id == productoId);
        if (prod is null) return NotFound(new { error = "Producto no encontrado" });
        if (VencimientosService.EsCafe(prod)) return BadRequest(new { error = "El café se cuenta en kilos: corregilo desde Stock." });
        if (req.Coincide)
        {
            // "✓ está bien": lo que hay en el estante coincide con el sistema. No cambia nada, solo queda anotado.
            _db.StockMovimientos.Add(new StockMovimiento
            {
                ProductoId = prod.Id, OperadorNombreSnap = Operador(), DepositoNombreSnap = "9 de Abril",
                TipoMov = "CONTEO", Cantidad = 0, StockAntes = prod.StockUnidades, StockDespues = prod.StockUnidades,
                Comentario = "Conteo (Asignar vencimientos): está bien (coincide)", CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
            return Ok(new { confirmar = false, stockAntes = prod.StockUnidades, stockDespues = prod.StockUnidades, apartados = 0, producto = await CargarAsync(prod.Id) });
        }
        if (req.Contado < 0 || req.Contado > 1_000_000) return BadRequest(new { error = "Poné cuántos contaste." });
        var (apMeli, apPropias) = await DepositoTareasController.ApartadosDeAsync(_db, _reservas, prod.Id);
        var apartados = apMeli + apPropias;
        var antes = prod.StockUnidades;
        var despues = Math.Max(0, req.Contado - apartados);
        if (!req.Confirmado && DepositoTareasController.Nivel(antes, despues) == "MUCHA")
            return Ok(new { confirmar = true, stockAntes = antes, stockDespues = despues, apartados });
        var quien = Operador();
        var coment = $"Conteo (Asignar vencimientos): contaron {req.Contado}"
            + (apartados > 0 ? $" − {apartados} vendidos sin armar" + (apMeli > 0 ? $" ({apMeli} MeLi)" : "") : "");
        if (antes != despues)
        {
            prod.StockUnidades = despues;
            prod.UpdatedAt = DateTime.UtcNow;
            prod.StockChangedAt = DateTime.UtcNow;
            await CafeStockHelper.SyncStockPorDepositoAsync(_db, prod);
        }
        _db.StockMovimientos.Add(new StockMovimiento
        {
            ProductoId = prod.Id, OperadorNombreSnap = quien, DepositoNombreSnap = "9 de Abril",
            TipoMov = antes != despues ? "SET" : "CONTEO",
            Cantidad = antes != despues ? despues : 0, StockAntes = antes, StockDespues = despues,
            Comentario = antes != despues ? coment : coment + " (coincide)", CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        if (antes != despues)
        {
            var sf = _scopeFactory; var pid = prod.Id;
            _ = Task.Run(async () =>
            {
                try { using var sc = sf.CreateScope(); await sc.ServiceProvider.GetRequiredService<MeliStockPushService>().PushStockForProductoAsync(pid); }
                catch { /* queda en StockChangedAt, lo retoma el background */ }
            });
        }
        return Ok(new { confirmar = false, stockAntes = antes, stockDespues = despues, apartados, producto = await CargarAsync(prod.Id) });
    }

    // ─── GET ver qué nota le dejaría a una orden de MeLi (no manda nada) ───
    [HttpGet("nota-meli/{meliOrderId:long}")]
    public async Task<IActionResult> NotaMeli(long meliOrderId)
    {
        var filas = await _db.MeliOrders.AsNoTracking().Where(o => o.MeliOrderId == meliOrderId).ToListAsync();
        if (filas.Count == 0) return NotFound(new { error = "Orden no encontrada" });
        var notas = await MeliVencimientoNotasService.TextosAsync(_db, filas);
        var ganancia = await MeliVencimientoNotasService.GananciaTextoAsync(_db, filas);
        return Ok(new { notas, enviada = filas.Max(f => f.VencNoteSentAt), ganancia, gananciaEnviada = filas.Max(f => f.GananciaNoteSentAt) });
    }

    // ─── DELETE ───
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var v = await _db.CafeProductoVencimientos.FindAsync(id);
        if (v is null) return NotFound(new { error = "No encontrado" });
        var pid = v.ProductoId;
        _db.CafeProductoVencimientos.Remove(v);
        await _db.SaveChangesAsync();
        return Ok(await CargarAsync(pid));
    }

    // ─── GET lista para "Asignar vencimientos" (buscar por nombre/código/marca) ───
    [HttpGet("carga")]
    public async Task<IActionResult> Carga([FromQuery] string? q = null, [FromQuery] string? marca = null,
        [FromQuery] bool soloConStock = true, [FromQuery] bool soloAsignados = false)
    {
        var qry = _db.CafeProductos.AsNoTracking().Include(p => p.MarcaNav).Where(p => p.IsActive);
        var s = (q ?? "").Trim().ToLower();
        if (s.Length > 0)
            qry = qry.Where(p => p.Nombre.ToLower().Contains(s) || (p.Sku != null && p.Sku.ToLower().Contains(s))
                || (p.Marca != null && p.Marca.ToLower().Contains(s)) || (p.MarcaNav != null && p.MarcaNav.Nombre.ToLower().Contains(s)));
        var m = (marca ?? "").Trim();
        if (m.Length > 0) qry = qry.Where(p => p.Marca == m || (p.MarcaNav != null && p.MarcaNav.Nombre == m));
        if (soloConStock) qry = qry.Where(p => p.StockUnidades > 0 || p.StockGramos > 0);
        if (soloAsignados)
        {
            var conV = _db.CafeProductoVencimientos.Select(v => v.ProductoId);
            qry = qry.Where(p => conV.Contains(p.Id));
        }
        else if (s.Length == 0 && m.Length == 0)
            return Ok(new List<ProductoVencDto>());   // sin filtro no traemos todo el catálogo
        var prods = await qry.OrderBy(p => p.Marca).ThenBy(p => p.Nombre).Take(300).ToListAsync();
        var res = await VencimientosService.ResumenAsync(_db, prods.Select(p => p.Id));
        var ex = await ExtrasAsync(prods.Select(p => p.Id).ToList());
        return Ok(prods.Select(p => Map(p, res.GetValueOrDefault(p.Id), ex.GetValueOrDefault(p.Id))).ToList());
    }

    // ─── GET marcas con productos (para el filtro) ───
    [HttpGet("marcas")]
    public async Task<IActionResult> Marcas()
    {
        var marcas = await _db.CafeProductos.AsNoTracking().Where(p => p.IsActive && (p.StockUnidades > 0 || p.StockGramos > 0))
            .Select(p => p.MarcaNav != null ? p.MarcaNav.Nombre : p.Marca).Where(x => x != null && x != "")
            .Distinct().OrderBy(x => x).ToListAsync();
        return Ok(marcas);
    }

    // ─── POST resumen de varios productos (para venta y armado) ───
    [HttpPost("resumen")]
    public async Task<IActionResult> Resumen([FromBody] List<int> productoIds)
    {
        var res = await VencimientosService.ResumenAsync(_db, productoIds ?? new());
        return Ok(ResumenDtos(res));
    }

    /// <summary>2026-11-04: resumen de TODOS los productos que tienen vencimientos (listado de productos).</summary>
    [HttpGet("resumen-todos")]
    public async Task<IActionResult> ResumenTodos()
    {
        var ids = await _db.CafeProductoVencimientos.Select(v => v.ProductoId).Distinct().ToListAsync();
        var res = await VencimientosService.ResumenAsync(_db, ids);
        return Ok(ResumenDtos(res));
    }

    public record FechaQuedanDto(DateTime FechaVence, decimal Quedan);
    public record ResumenDto(int ProductoId, DateTime? ProximoVence, decimal ProximoQuedan, DateTime? SiguienteVence,
        bool EsCafe, decimal SinVencimiento, List<FechaQuedanDto> Fechas);

    /// <summary>Solo productos que todavía tienen algo con fecha. Fechas = cada fecha con lo que queda
    /// (misma fecha cargada dos veces se suma), de la más cercana a la más lejana.</summary>
    private static List<ResumenDto> ResumenDtos(Dictionary<int, VencimientosService.VencResumen> res) =>
        res.Values.Where(r => r.ProximoVence != null).Select(r => new ResumenDto(
            r.ProductoId, r.ProximoVence, r.ProximoQuedan, r.SiguienteVence, r.EsCafe, r.SinVencimiento,
            r.Filas.Where(f => f.Quedan > 0).GroupBy(f => f.FechaVence.Date).OrderBy(g => g.Key)
                .Select(g => new FechaQuedanDto(g.Key, g.Sum(f => f.Quedan))).ToList())).ToList();

    // ─── GET por vencer / vencidos (para Alertas) ───
    [HttpGet("alertas")]
    public async Task<IActionResult> Alertas([FromQuery] int dias = 30)
    {
        var hoy = ViajesAutoService.HoyAr().Date;
        var limite = hoy.AddDays(Math.Clamp(dias, 1, 365));
        var ids = await _db.CafeProductoVencimientos.Where(v => v.FechaVence <= limite).Select(v => v.ProductoId).Distinct().ToListAsync();
        var res = await VencimientosService.ResumenAsync(_db, ids);
        var prods = await _db.CafeProductos.AsNoTracking().Include(p => p.MarcaNav).Where(p => ids.Contains(p.Id)).ToListAsync();
        var lista = new List<AlertaDto>();
        foreach (var p in prods)
        {
            if (!res.TryGetValue(p.Id, out var r)) continue;
            foreach (var f in r.Filas.Where(f => f.Quedan > 0 && f.FechaVence <= limite))
                lista.Add(new AlertaDto(p.Id, p.Sku, p.Nombre, r.EsCafe, f.FechaVence, f.Quedan, (int)(f.FechaVence.Date - hoy).TotalDays));
        }
        return Ok(lista.OrderBy(x => x.FechaVence).ToList());
    }
}
