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

    /// <summary>2026-09-02 — El numero que DISPARA el pedido. Si hay piso cargado, es el piso;
    /// si no, el ideal (como venia funcionando antes de que existiera el piso).
    ///
    /// Idea del usuario: "si pongo ideal 100 y piso 10, me diria que tengo que pedir 90". O sea:
    /// mientras haya mas de 10 no molesta; cuando cruza para abajo, se pide hasta volver a 100.
    /// Sin piso, con ideal 100 y stock 99 ya avisaba "falta 1" — puro ruido.</summary>
    public static int? Disparador(int? ideal, int? piso) => piso ?? ideal;

    /// <summary>Si el producto tiene que estar en la lista de faltantes.
    ///
    /// OJO con el "o igual": si hay PISO, se avisa al TOCARLO (stock &lt;= piso), no al pasarlo.
    /// Es lo que pidió el usuario con su ejemplo — "ideal 100, piso 10, me diría que pida 90":
    /// con 10 justos ya tiene que avisar, si no habría que esperar a tener 9.
    /// Sin piso el disparador es el ideal, y ahí sí es &lt; (con el stock justo en el ideal no
    /// falta nada que pedir).</summary>
    public static bool HayQuePedir(int? ideal, int? piso, decimal stock)
    {
        if (piso.HasValue) return stock <= piso.Value;
        return ideal.HasValue && stock < ideal.Value;
    }

    /// <summary>Cuanto pedir: SIEMPRE hasta el ideal, no hasta el piso. Si el stock cayo a 8 con
    /// ideal 100, se piden 92 (no 90) — se lo aclare al usuario cuando lo propuso.</summary>
    public static decimal CuantoPedir(int? ideal, int? piso, decimal stock)
    {
        if (!HayQuePedir(ideal, piso, stock)) return 0m;
        var hasta = ideal ?? Disparador(ideal, piso) ?? 0;
        return Math.Max(0m, hasta - stock);
    }

    /// <summary>Anota los productos que quedaron por debajo del ideal y no estaban ya anotados.
    /// Devuelve cuantos entraron nuevos a la lista.</summary>
    public async Task<int> EngancharAsync(CancellationToken ct = default)
    {
        // Solo los que tienen ideal cargado: el resto no se controla.
        var candidatos = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive && (p.StockIdeal != null || p.StockPiso != null))
            .Select(p => new { p.Id, p.Categoria, p.StockUnidades, p.StockGramos, p.StockIdeal, p.StockPiso })
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
            // Entra recien cuando cruza el PISO (o el ideal, si no hay piso cargado).
            if (!HayQuePedir(p.StockIdeal, p.StockPiso, stock)) continue;

            nuevos.Add(new CafeStockFaltante
            {
                ProductoId = p.Id,
                DetectadoAt = DateTime.UtcNow,
                StockAlDetectar = stock,
                IdealAlDetectar = p.StockIdeal ?? p.StockPiso ?? 0,
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
