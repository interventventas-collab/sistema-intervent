using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-11-04: vencimientos por producto. Lo que QUEDA de cada fecha se calcula con el stock actual:
/// como al depósito siempre se le indica sacar primero lo que vence antes, el stock que hay hoy
/// pertenece a las fechas MÁS LEJANAS. Se reparte el stock de la fecha más lejana a la más cercana.
/// Así no hay que tocar ventas, MeLi ni ningún descuento de stock.
/// Café: todo en kg (StockGramos / 1000).
/// </summary>
public static class VencimientosService
{
    public record VencFila(int Id, decimal Cantidad, DateTime FechaVence, decimal Quedan);

    public record VencResumen(
        int ProductoId,
        bool EsCafe,
        decimal Stock,                 // unidades (o kg en café)
        List<VencFila> Filas,          // ordenadas por fecha (la más cercana primero)
        DateTime? ProximoVence,        // la fecha a sacar primero (la más cercana que todavía tiene)
        decimal ProximoQuedan,         // cuántos quedan de esa fecha
        DateTime? SiguienteVence,      // la que sigue (para avisar "sale de dos fechas")
        decimal SinVencimiento,        // stock que no tiene fecha asignada (> 0 = falta asignar)
        decimal Asignado);             // suma de lo que todavía queda con fecha

    public static decimal StockDe(CafeProducto p) =>
        EsCafe(p) ? Math.Round(p.StockGramos / 1000m, 3) : p.StockUnidades;

    public static bool EsCafe(CafeProducto p) =>
        string.Equals(p.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase);

    public static VencResumen Calcular(int productoId, bool esCafe, decimal stock, IEnumerable<CafeProductoVencimiento> vencs)
    {
        var lista = vencs.OrderByDescending(v => v.FechaVence).ThenByDescending(v => v.Id).ToList();
        var resto = Math.Max(0m, stock);
        var quedan = new Dictionary<int, decimal>();
        foreach (var v in lista)
        {
            var q = Math.Min(v.Cantidad, resto);
            quedan[v.Id] = q;
            resto -= q;
        }
        var filas = lista.OrderBy(v => v.FechaVence).ThenBy(v => v.Id)
            .Select(v => new VencFila(v.Id, v.Cantidad, v.FechaVence, quedan[v.Id])).ToList();
        var conStock = filas.Where(f => f.Quedan > 0).ToList();
        var prox = conStock.FirstOrDefault();
        // Varias filas con la misma fecha cuentan como una sola "fecha".
        var proxQuedan = prox is null ? 0 : conStock.Where(f => f.FechaVence == prox.FechaVence).Sum(f => f.Quedan);
        var sig = prox is null ? null : conStock.FirstOrDefault(f => f.FechaVence > prox.FechaVence)?.FechaVence;
        return new VencResumen(productoId, esCafe, stock, filas, prox?.FechaVence, proxQuedan, sig,
            Math.Max(0m, resto), conStock.Sum(f => f.Quedan));
    }

    /// <summary>2026-11-04: texto para el ARMADO. Cantidad en unidades (o kg en café).
    /// 1 fecha → "Sacar el que vence 15/01/2027"; si no alcanza → "Sacar 3 del 15/01/2027 y 3 del 20/06/2027".
    /// Null si el producto no tiene fechas con stock.</summary>
    public static string? TextoSacar(VencResumen? r, decimal cantidad)
    {
        if (r is null || cantidad <= 0) return null;
        var fechas = r.Filas.Where(f => f.Quedan > 0).GroupBy(f => f.FechaVence.Date).OrderBy(g => g.Key)
            .Select(g => (Fecha: g.Key, Quedan: g.Sum(f => f.Quedan))).ToList();
        if (fechas.Count == 0) return null;
        string Cant(decimal v) => r.EsCafe ? $"{v:0.##} kg" : (v / 1.000000000000000000000000000000000m).ToString("0.##");
        var partes = new List<string>();
        var resto = cantidad;
        foreach (var f in fechas)
        {
            if (resto <= 0) break;
            var toma = Math.Min(resto, f.Quedan);
            partes.Add($"{Cant(toma)} del {f.Fecha:dd/MM/yyyy}");
            resto -= toma;
        }
        return partes.Count == 1 ? $"Sacar el que vence {fechas[0].Fecha:dd/MM/yyyy}" : "Sacar " + string.Join(" y ", partes);
    }

    /// <summary>Resumen de varios productos de una vez. Solo devuelve los que tienen algún vencimiento asignado.</summary>
    public static async Task<Dictionary<int, VencResumen>> ResumenAsync(AppDbContext db, IEnumerable<int> productoIds, CancellationToken ct = default)
    {
        var ids = productoIds.Distinct().ToList();
        var res = new Dictionary<int, VencResumen>();
        if (ids.Count == 0) return res;
        var vencs = await db.CafeProductoVencimientos.AsNoTracking().Where(v => ids.Contains(v.ProductoId)).ToListAsync(ct);
        if (vencs.Count == 0) return res;
        var conVenc = vencs.Select(v => v.ProductoId).Distinct().ToList();
        var prods = await db.CafeProductos.AsNoTracking().Where(p => conVenc.Contains(p.Id))
            .Select(p => new { p.Id, p.Categoria, p.StockUnidades, p.StockGramos }).ToListAsync(ct);
        foreach (var p in prods)
        {
            var esCafe = string.Equals(p.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase);
            var stock = esCafe ? Math.Round(p.StockGramos / 1000m, 3) : p.StockUnidades;
            res[p.Id] = Calcular(p.Id, esCafe, stock, vencs.Where(v => v.ProductoId == p.Id));
        }
        return res;
    }
}
