using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>2026-09-02 — El "enganche" de la lista de para pedir: recorre los productos que
/// tienen stock ideal cargado y, si alguno quedo por debajo, le anota un renglon en
/// Cafe_StockFaltantes (si no tenia uno pendiente ya).
///
/// Lo llaman dos lugares: el robot StockFaltantesBackgroundService (cada 10 min) y la pantalla
/// al entrar. Es idempotente: correrlo dos veces seguidas no duplica nada.
///
/// OJO con la unidad: el cafe lleva el stock en GRAMOS (StockUnidades siempre 0) y su ideal se
/// cuenta en KILOS. Misma cuenta que StockIdealController — si se toca una, tocar la otra.</summary>
public class StockFaltantesService
{
    private readonly AppDbContext _db;
    private readonly ILogger<StockFaltantesService> _logger;

    public StockFaltantesService(AppDbContext db, ILogger<StockFaltantesService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>El cafe se mide en kilos (viene guardado en gramos); todo lo demas en unidades.</summary>
    public static bool EsCafe(string? categoria)
        => string.Equals(categoria, "CAFE", StringComparison.OrdinalIgnoreCase);

    public static decimal StockDe(string? categoria, int stockUnidades, decimal stockGramos)
        => EsCafe(categoria) ? Math.Round(stockGramos / 1000m, 2) : stockUnidades;

    public static string UnidadDe(string? categoria) => EsCafe(categoria) ? "kg" : "u";

    /// <summary>Anota los productos que quedaron por debajo del ideal y no estaban ya anotados.
    /// Devuelve cuantos entraron nuevos a la lista.</summary>
    public async Task<int> EngancharAsync(CancellationToken ct = default)
    {
        // Solo los que tienen ideal cargado: el resto no se controla.
        var candidatos = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive && p.StockIdeal != null)
            .Select(p => new { p.Id, p.Categoria, p.StockUnidades, p.StockGramos, p.StockIdeal })
            .ToListAsync(ct);
        if (candidatos.Count == 0) return 0;

        var pendientes = await _db.CafeStockFaltantes
            .Where(f => f.Estado == "PENDIENTE")
            .Select(f => f.ProductoId)
            .ToListAsync(ct);
        var yaAnotados = pendientes.ToHashSet();

        var nuevos = new List<CafeStockFaltante>();
        foreach (var p in candidatos)
        {
            if (yaAnotados.Contains(p.Id)) continue;
            var stock = StockDe(p.Categoria, p.StockUnidades, p.StockGramos);
            if (stock >= p.StockIdeal!.Value) continue;

            nuevos.Add(new CafeStockFaltante
            {
                ProductoId = p.Id,
                DetectadoAt = DateTime.UtcNow,
                StockAlDetectar = stock,
                IdealAlDetectar = p.StockIdeal.Value,
                Estado = "PENDIENTE",
            });
        }

        if (nuevos.Count == 0) return 0;

        _db.CafeStockFaltantes.AddRange(nuevos);
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Puede chocar contra el indice unico si el robot y la pantalla engancharon al mismo
            // tiempo. No es grave: el renglon ya existe, que es justo lo que queriamos.
            _logger.LogWarning(ex, "[StockFaltantes] no se pudieron anotar algunos productos (posible carrera)");
            foreach (var n in nuevos) _db.Entry(n).State = EntityState.Detached;
            return 0;
        }
        return nuevos.Count;
    }
}
