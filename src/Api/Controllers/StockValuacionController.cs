using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>2026-07-09: reporte "Valor de mi stock a costo".
/// Suma (costo × cantidad) de toda la mercadería, agrupado por marca.
/// El usuario puede DESTILDAR marcas o productos puntuales cuyo stock en realidad lo tiene
/// el proveedor ("stock falso") para que no ensucien el total. El café queda afuera por
/// defecto (se mide en kg y también tiene stock falso) pero se muestra aparte para transparencia.
/// Reglas de conteo (categoría OTROS):
///   suma  = producto activo, con stock &gt; 0, ExcluirDeValuacion=false, y su marca CuentaEnValuacion=true.
///   Marca nueva / producto sin marca: suma por defecto (se revisa después).</summary>
[ApiController]
[Route("api/stock/valuacion")]
[Authorize]
public class StockValuacionController : ControllerBase
{
    private readonly AppDbContext _db;
    public StockValuacionController(AppDbContext db) { _db = db; }

    public record MarcaRow(int? MarcaId, string Marca, bool Cuenta, int Productos, decimal Valor);
    public record ExcluidoRow(int ProductoId, string? Sku, string Nombre, string? Marca, decimal Valor);
    public record ValuacionResult(
        decimal TotalContado, int ProductosContados, int MarcasContadas,
        decimal TotalNoContado,
        List<MarcaRow> Marcas,             // marcas que SÍ suman (ordenadas por valor desc)
        List<MarcaRow> MarcasExcluidas,    // marcas destildadas (no suman)
        decimal ValorCafe, int ProductosCafe,
        List<ExcluidoRow> ProductosExcluidos); // productos puntuales destildados dentro de marcas que sí cuentan

    /// <summary>GET /api/stock/valuacion — arma el reporte completo listo para mostrar.</summary>
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        // Solo productos activos con stock. Café aparte (stock en gramos, costo por kg).
        var prods = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => new
            {
                p.Id, p.Sku, p.Nombre, p.Categoria, p.MarcaId, p.Marca,
                p.StockUnidades, p.StockGramos, p.Costo, p.ExcluirDeValuacion
            })
            .ToListAsync();

        var marcasDic = await _db.CafeMarcas.AsNoTracking()
            .Select(m => new { m.Id, m.Nombre, m.CuentaEnValuacion })
            .ToDictionaryAsync(m => m.Id, m => new { m.Nombre, m.CuentaEnValuacion });

        // ── Café: valor informativo, siempre fuera del total ──
        decimal valorCafe = 0m; int prodCafe = 0;
        foreach (var p in prods.Where(p => p.Categoria == "CAFE"))
        {
            var v = (p.StockGramos / 1000m) * p.Costo;
            if (v <= 0) continue;
            valorCafe += v; prodCafe++;
        }

        // ── OTROS: agrupar por marca ──
        var otros = prods.Where(p => p.Categoria != "CAFE" && p.StockUnidades > 0).ToList();

        var marcasContado = new List<MarcaRow>();
        var marcasExcluidas = new List<MarcaRow>();
        var productosExcluidos = new List<ExcluidoRow>();
        decimal totalContado = 0m; int productosContados = 0;
        decimal totalNoContado = 0m;

        foreach (var grupo in otros.GroupBy(p => p.MarcaId))
        {
            var marcaId = grupo.Key;
            bool cuentaMarca;
            string nombreMarca;
            if (marcaId.HasValue && marcasDic.TryGetValue(marcaId.Value, out var mInfo))
            {
                cuentaMarca = mInfo.CuentaEnValuacion;
                nombreMarca = mInfo.Nombre;
            }
            else
            {
                // Sin marca (o marca huérfana): suma por defecto, solo se puede excluir por producto.
                cuentaMarca = true;
                nombreMarca = grupo.Select(g => g.Marca).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "(Sin marca)";
            }

            if (!cuentaMarca)
            {
                // Marca entera afuera: todo su valor es "no contado".
                decimal valorMarca = grupo.Sum(g => g.StockUnidades * g.Costo);
                marcasExcluidas.Add(new MarcaRow(marcaId, nombreMarca, false, grupo.Count(), valorMarca));
                totalNoContado += valorMarca;
                continue;
            }

            // Marca cuenta: separo productos excluidos puntualmente.
            decimal valorCuenta = 0m; int nCuenta = 0;
            foreach (var p in grupo)
            {
                var v = p.StockUnidades * p.Costo;
                if (p.ExcluirDeValuacion)
                {
                    if (v > 0)
                    {
                        productosExcluidos.Add(new ExcluidoRow(p.Id, p.Sku, p.Nombre, nombreMarca, v));
                        totalNoContado += v;
                    }
                }
                else { valorCuenta += v; nCuenta++; }
            }
            if (nCuenta > 0)
            {
                marcasContado.Add(new MarcaRow(marcaId, nombreMarca, true, nCuenta, valorCuenta));
                totalContado += valorCuenta; productosContados += nCuenta;
            }
        }

        totalNoContado += valorCafe;

        marcasContado = marcasContado.OrderByDescending(m => m.Valor).ToList();
        marcasExcluidas = marcasExcluidas.OrderByDescending(m => m.Valor).ToList();
        productosExcluidos = productosExcluidos.OrderByDescending(p => p.Valor).ToList();

        return Ok(new ValuacionResult(
            totalContado, productosContados, marcasContado.Count,
            totalNoContado,
            marcasContado, marcasExcluidas,
            valorCafe, prodCafe,
            productosExcluidos));
    }

    public record ProdRow(int Id, string? Sku, string Nombre, int Stock, decimal Costo, decimal Valor, bool Excluido);

    /// <summary>GET /api/stock/valuacion/productos?marcaId= — productos OTROS con stock de una marca,
    /// para poder tildar/destildar uno por uno. Sin marcaId = productos sin marca.</summary>
    [HttpGet("productos")]
    public async Task<IActionResult> Productos([FromQuery] int? marcaId)
    {
        var q = _db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive && p.Categoria != "CAFE" && p.StockUnidades > 0);
        q = marcaId.HasValue ? q.Where(p => p.MarcaId == marcaId.Value) : q.Where(p => p.MarcaId == null);
        var list = await q.OrderBy(p => p.Nombre)
            .Select(p => new ProdRow(p.Id, p.Sku, p.Nombre, p.StockUnidades, p.Costo,
                                     p.StockUnidades * p.Costo, p.ExcluirDeValuacion))
            .ToListAsync();
        return Ok(list);
    }

    public record ToggleMarcaReq(bool Cuenta);

    /// <summary>PUT /api/stock/valuacion/marca/{id} — tilda/destilda una marca entera.</summary>
    [HttpPut("marca/{id:int}")]
    public async Task<IActionResult> SetMarca(int id, [FromBody] ToggleMarcaReq req)
    {
        var m = await _db.CafeMarcas.FindAsync(id);
        if (m is null) return NotFound(new { error = "Marca no encontrada" });
        m.CuentaEnValuacion = req.Cuenta;
        m.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, cuenta = m.CuentaEnValuacion });
    }

    public record ToggleProductoReq(bool Excluir);

    /// <summary>PUT /api/stock/valuacion/producto/{id} — excluye/incluye un producto puntual.</summary>
    [HttpPut("producto/{id:int}")]
    public async Task<IActionResult> SetProducto(int id, [FromBody] ToggleProductoReq req)
    {
        var p = await _db.CafeProductos.FindAsync(id);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });
        p.ExcluirDeValuacion = req.Excluir;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, excluido = p.ExcluirDeValuacion });
    }
}
