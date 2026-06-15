using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>2026-06-15: Reportes de stock para reposición de proveedores + ranking de ventas.
/// El endpoint principal /reposicion devuelve una fila por producto con stock actual,
/// totales por tipo de movimiento (ventas/entradas/ajustes), neto y sugerido a reponer.
/// /ranking devuelve top productos por cantidad vendida, facturación o margen.
/// Filtros compartidos: período, marca, OEM, SKU, categoría, cliente.</summary>
[ApiController]
[Route("api/stock/reportes")]
[Authorize]
public class StockReportesController : ControllerBase
{
    private readonly AppDbContext _db;
    public StockReportesController(AppDbContext db) { _db = db; }

    // ---- Filtros comunes ----
    public record ReporteFiltros(
        DateTime? Desde, DateTime? Hasta,
        int? MarcaId, string? MarcaTexto,
        int? OemId, string? OemCodigo,
        string? Sku,
        string? Categoria,
        int? ClienteId,
        // tipos de movimiento a incluir en el cálculo (lista de strings TipoMov de Stock_Movimientos).
        // Si es null/vacío incluye todos.
        List<string>? TiposMovimiento
    );

    public record ReposicionRow(
        int ProductoId, string? Sku, string Nombre, string? Marca, string? OemCodigo, string? Categoria,
        int StockActual, int? StockMinimo,
        decimal Entradas, decimal Salidas, decimal Ajustes, decimal Neto,
        int VendidoUnidades, decimal FacturadoTotal,
        int SugeridoReponer);

    public record ReposicionResult(int Total, List<ReposicionRow> Filas);

    /// <summary>GET /api/stock/reportes/reposicion — devuelve una fila por producto con todo
    /// lo necesario para armar un pedido al proveedor. Los filtros van por query string.</summary>
    [HttpGet("reposicion")]
    public async Task<IActionResult> Reposicion(
        [FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] int? marcaId, [FromQuery] string? marcaTexto,
        [FromQuery] int? oemId, [FromQuery] string? oemCodigo,
        [FromQuery] string? sku, [FromQuery] string? categoria, [FromQuery] int? clienteId,
        [FromQuery] string? tiposMov)
    {
        var (productosFiltrados, ventasQuery, movsQuery) = await ArmarBase(desde, hasta, marcaId, marcaTexto,
            oemId, oemCodigo, sku, categoria, clienteId, tiposMov);

        // Vendido en el período por producto (a partir de los items de venta, NO de Stock_Movimientos —
        // es más confiable y trae también precios para mostrar facturado).
        var vendidoPorProd = await ventasQuery
            .GroupBy(it => it.ProductoId!.Value)
            .Select(g => new { ProductoId = g.Key, Unidades = g.Sum(x => x.Cantidad), Facturado = g.Sum(x => x.Subtotal) })
            .ToListAsync();
        var vendidoDic = vendidoPorProd.ToDictionary(x => x.ProductoId, x => (x.Unidades, x.Facturado));

        // Movimientos por producto agrupados por signo. Suma absoluta de entradas, salidas, ajustes.
        var movs = await movsQuery
            .Select(m => new { m.ProductoId, m.TipoMov, m.Cantidad })
            .ToListAsync();
        var movDic = movs.GroupBy(m => m.ProductoId).ToDictionary(g => g.Key, g => g.ToList());

        var prods = await productosFiltrados.ToListAsync();

        var filas = prods.Select(p =>
        {
            var mList = movDic.TryGetValue(p.Id, out var lst) ? lst : new();
            decimal entradas = 0, salidas = 0, ajustes = 0;
            foreach (var m in mList)
            {
                // VENTA_* siempre suma a salidas (Cantidad guarda el valor absoluto).
                // SUMA = entradas. RESTA = salidas. SET = ajustes.
                switch (m.TipoMov)
                {
                    case "VENTA_NUESTRA":
                    case "VENTA_MELI":
                    case "VENTA_MELI_FULL":
                        salidas += m.Cantidad; break;
                    case "SUMA":
                        entradas += m.Cantidad; break;
                    case "RESTA":
                        salidas += m.Cantidad; break;
                    case "SET":
                        ajustes += m.Cantidad; break;
                    default:
                        salidas += m.Cantidad; break;
                }
            }
            decimal neto = entradas - salidas + ajustes;

            int vendidoU = 0; decimal facturado = 0m;
            if (vendidoDic.TryGetValue(p.Id, out var v)) { vendidoU = v.Unidades; facturado = v.Facturado; }

            // Sugerido reponer = max(0, salidas_periodo + StockMinimo - stockActual).
            // Si no hay stock mínimo configurado se usa solo las salidas - stock.
            int minimo = p.StockMinimoMeLi ?? 0;
            int sugerido = (int)Math.Max(0, salidas + minimo - p.StockUnidades);

            return new ReposicionRow(
                p.Id, p.Sku, p.Nombre, p.Marca, p.OemNav?.Codigo, p.Categoria,
                p.StockUnidades, p.StockMinimoMeLi,
                entradas, salidas, ajustes, neto,
                vendidoU, facturado,
                sugerido);
        }).ToList();

        return Ok(new ReposicionResult(filas.Count, filas));
    }

    public record RankingRow(
        int ProductoId, string? Sku, string Nombre, string? Marca, string? OemCodigo, string? Categoria,
        int StockActual,
        int VendidoUnidades, decimal FacturadoTotal, decimal MargenTotal,
        int ClientesUnicos, int CantidadVentas);

    public record RankingResult(int Total, List<RankingRow> Filas);

    /// <summary>GET /api/stock/reportes/ranking — top productos por ventas en el período.
    /// orderBy: "unidades" (default) | "facturado" | "margen" | "clientes". top: cuántas filas (default 50).</summary>
    [HttpGet("ranking")]
    public async Task<IActionResult> Ranking(
        [FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] int? marcaId, [FromQuery] string? marcaTexto,
        [FromQuery] int? oemId, [FromQuery] string? oemCodigo,
        [FromQuery] string? sku, [FromQuery] string? categoria, [FromQuery] int? clienteId,
        [FromQuery] string? orderBy = "unidades", [FromQuery] int top = 50)
    {
        var (productosFiltrados, ventasQuery, _) = await ArmarBase(desde, hasta, marcaId, marcaTexto,
            oemId, oemCodigo, sku, categoria, clienteId, null);

        // Agrupar items por producto. Cantidad de clientes únicos: a través del ClienteId de la venta.
        var raw = await ventasQuery
            .GroupBy(it => it.ProductoId!.Value)
            .Select(g => new
            {
                ProductoId = g.Key,
                Unidades = g.Sum(x => x.Cantidad),
                Facturado = g.Sum(x => x.Subtotal),
                Margen = g.Sum(x => x.Subtotal - (x.CostoUnitario * x.Cantidad)),
                ClientesUnicos = g.Select(x => x.VentaNav!.ClienteId).Distinct().Count(),
                CantVentas = g.Select(x => x.VentaId).Distinct().Count()
            })
            .ToListAsync();

        var prods = await productosFiltrados.ToDictionaryAsync(p => p.Id);

        var filas = raw
            .Where(r => prods.ContainsKey(r.ProductoId))
            .Select(r =>
            {
                var p = prods[r.ProductoId];
                return new RankingRow(p.Id, p.Sku, p.Nombre, p.Marca, p.OemNav?.Codigo, p.Categoria,
                    p.StockUnidades, r.Unidades, r.Facturado, r.Margen, r.ClientesUnicos, r.CantVentas);
            });

        filas = (orderBy?.ToLowerInvariant()) switch
        {
            "facturado" => filas.OrderByDescending(f => f.FacturadoTotal),
            "margen" => filas.OrderByDescending(f => f.MargenTotal),
            "clientes" => filas.OrderByDescending(f => f.ClientesUnicos),
            _ => filas.OrderByDescending(f => f.VendidoUnidades),
        };

        var totalAntesTop = filas.Count();
        var pagina = filas.Take(Math.Max(1, top)).ToList();
        return Ok(new RankingResult(totalAntesTop, pagina));
    }

    // ---- Helper: arma la query base con todos los filtros aplicados ----
    private async Task<(IQueryable<Models.CafeProducto> productos,
                        IQueryable<Models.CafeVentaItem> ventas,
                        IQueryable<Models.StockMovimiento> movs)> ArmarBase(
        DateTime? desde, DateTime? hasta,
        int? marcaId, string? marcaTexto,
        int? oemId, string? oemCodigo,
        string? sku, string? categoria, int? clienteId,
        string? tiposMov)
    {
        await Task.CompletedTask;

        // Período: si no viene desde/hasta, default últimos 30 días.
        var dDesde = desde ?? DateTime.UtcNow.AddDays(-30);
        var dHasta = hasta ?? DateTime.UtcNow.AddDays(1);

        IQueryable<Models.CafeProducto> productos = _db.Set<Models.CafeProducto>().Include(p => p.OemNav).AsNoTracking();
        if (marcaId.HasValue) productos = productos.Where(p => p.MarcaId == marcaId.Value);
        if (!string.IsNullOrWhiteSpace(marcaTexto))
            productos = productos.Where(p => p.Marca != null && p.Marca.Contains(marcaTexto));
        if (oemId.HasValue) productos = productos.Where(p => p.OemId == oemId.Value);
        if (!string.IsNullOrWhiteSpace(oemCodigo))
            productos = productos.Where(p => p.OemNav != null && p.OemNav.Codigo.Contains(oemCodigo));
        if (!string.IsNullOrWhiteSpace(sku))
            productos = productos.Where(p => p.Sku != null && p.Sku.Contains(sku));
        if (!string.IsNullOrWhiteSpace(categoria))
            productos = productos.Where(p => p.Categoria == categoria);
        productos = productos.Where(p => p.IsActive);

        // Ventas: items de ventas en el período, con producto vinculado, filtrando por cliente si aplica.
        var ventas = _db.Set<Models.CafeVentaItem>().AsNoTracking()
            .Include(v => v.VentaNav)
            .Where(v => v.ProductoId != null
                     && v.VentaNav != null
                     && v.VentaNav.Estado != "anulado"
                     && v.VentaNav.Fecha >= dDesde && v.VentaNav.Fecha < dHasta);
        if (clienteId.HasValue) ventas = ventas.Where(v => v.VentaNav!.ClienteId == clienteId.Value);
        // Restringir a productos que pasaron los filtros del catálogo.
        var prodIdsQuery = productos.Select(p => p.Id);
        ventas = ventas.Where(v => prodIdsQuery.Contains(v.ProductoId!.Value));

        // Movimientos: filtrar por período + producto, y por tipos si vinieron.
        var movs = _db.Set<Models.StockMovimiento>().AsNoTracking()
            .Where(m => m.CreatedAt >= dDesde && m.CreatedAt < dHasta
                     && !m.Reverted
                     && prodIdsQuery.Contains(m.ProductoId));
        if (!string.IsNullOrWhiteSpace(tiposMov))
        {
            var lista = tiposMov.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (lista.Count > 0) movs = movs.Where(m => lista.Contains(m.TipoMov));
        }

        return (productos, ventas, movs);
    }
}
