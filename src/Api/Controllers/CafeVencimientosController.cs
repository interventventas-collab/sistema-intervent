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
    public CafeVencimientosController(AppDbContext db) { _db = db; }

    public record FilaDto(int Id, decimal Cantidad, DateTime FechaVence, decimal Quedan);
    public record ProductoVencDto(int ProductoId, string? Sku, string Nombre, string? Marca, string? Ubicacion,
        bool EsCafe, decimal Stock, List<FilaDto> Filas, DateTime? ProximoVence, decimal ProximoQuedan,
        DateTime? SiguienteVence, decimal SinVencimiento, decimal Asignado);
    public record AsignarRequest(decimal Cantidad, DateTime FechaVence);
    public record AlertaDto(int ProductoId, string? Sku, string Nombre, bool EsCafe, DateTime FechaVence, decimal Quedan, int Dias);

    private string Operador()
    {
        var op = HttpContext.Request.Headers["X-Operator-Name"].ToString();
        return string.IsNullOrWhiteSpace(op) ? (User?.Identity?.Name ?? "Sistema") : op;
    }

    private static ProductoVencDto Map(CafeProducto p, VencimientosService.VencResumen? r)
    {
        var esCafe = VencimientosService.EsCafe(p);
        var stock = VencimientosService.StockDe(p);
        r ??= VencimientosService.Calcular(p.Id, esCafe, stock, Array.Empty<CafeProductoVencimiento>());
        var ubic = string.Join(" · ", new[] { p.UbicacionPlanta, p.UbicacionZona }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new ProductoVencDto(p.Id, p.Sku, p.Nombre, p.Marca ?? p.MarcaNav?.Nombre, ubic.Length > 0 ? ubic : null,
            esCafe, stock, r.Filas.Select(f => new FilaDto(f.Id, f.Cantidad, f.FechaVence, f.Quedan)).ToList(),
            r.ProximoVence, r.ProximoQuedan, r.SiguienteVence, r.SinVencimiento, r.Asignado);
    }

    private async Task<ProductoVencDto?> CargarAsync(int productoId)
    {
        var p = await _db.CafeProductos.AsNoTracking().Include(x => x.MarcaNav).FirstOrDefaultAsync(x => x.Id == productoId);
        if (p is null) return null;
        var res = await VencimientosService.ResumenAsync(_db, new[] { productoId });
        return Map(p, res.GetValueOrDefault(productoId));
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
        return Ok(prods.Select(p => Map(p, res.GetValueOrDefault(p.Id))).ToList());
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
