using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Modificacion masiva de stock por deposito.
/// Permite ver y editar el stock de todos los productos en un deposito especifico,
/// o ver stocks por deposito y mover entre depositos.
/// </summary>
[ApiController]
[Route("api/cafe/stock-masivo")]
[Authorize]
public class CafeStockMasivoController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CafeStockLogger _stockLogger;

    public CafeStockMasivoController(AppDbContext db, AuditLogService audit, IServiceScopeFactory scopeFactory, CafeStockLogger stockLogger)
    {
        _db = db; _audit = audit; _scopeFactory = scopeFactory; _stockLogger = stockLogger;
    }

    /// <summary>Dispara push de stock a MeLi en background para los productos cambiados (respeta kill switches).</summary>
    private void FireAndForgetPushMeli(List<int> cafeProductoIds)
    {
        if (cafeProductoIds.Count == 0) return;
        var scopeFactory = _scopeFactory;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var pushSvc = scope.ServiceProvider.GetRequiredService<MeliStockPushService>();
                foreach (var pid in cafeProductoIds)
                {
                    try { await pushSvc.PushStockForProductoAsync(pid); }
                    catch { /* swallow */ }
                }
            }
            catch { /* swallow */ }
        });
    }

    public record StockProductoDto(
        int ProductoId, string Codigo, string Nombre, string Categoria,
        decimal StockGramos, int StockUnidades);

    /// <summary>Lista productos con su stock en el deposito indicado.
    /// 2026-05-25: Si el depósito es 'Full MeLi', filtramos solo los productos que tienen
    /// al menos 1 publicación MeLi con logistic_type=fulfillment linkeada (sea via
    /// MeliItemComponente o legacy MeliItem.CafeProductoId). Para los demás depósitos
    /// se devuelven TODOS los productos (comportamiento original).</summary>
    [HttpGet("{depositoId:int}")]
    public async Task<IActionResult> ListarStockEnDeposito(int depositoId)
    {
        var dep = await _db.CafeDepositos.FindAsync(depositoId);
        if (dep is null) return NotFound(new { error = "Deposito no encontrado" });

        IQueryable<Models.CafeProducto> baseQ = _db.CafeProductos;

        if (dep.Nombre == "Full MeLi")
        {
            // Productos con al menos un MeliItem Full linkeado (componente o legacy).
            // Como LogisticType puede estar NULL para items que aún no se re-sincronizaron,
            // también incluimos productos que tienen registro en Cafe_StockPorDeposito[Full]
            // (ya pasaron por el sync de Full y se confirmó que están en meli_facility).
            var prodIdsFull = await (
                from p in _db.CafeProductos
                where _db.MeliItems.Any(mi =>
                          mi.LogisticType == "fulfillment" &&
                          (mi.CafeProductoId == p.Id ||
                           _db.MeliItemComponentes.Any(c => c.MeliItemId == mi.MeliItemId && c.CafeProductoId == p.Id)))
                   || _db.CafeStockPorDeposito.Any(s => s.DepositoId == depositoId && s.ProductoId == p.Id && (s.StockUnidades > 0 || s.StockGramos > 0))
                select p.Id
            ).Distinct().ToListAsync();

            baseQ = baseQ.Where(p => prodIdsFull.Contains(p.Id));
        }

        var query = from p in baseQ
                    join s in _db.CafeStockPorDeposito
                        on new { ProductoId = p.Id, DepositoId = depositoId }
                        equals new { s.ProductoId, s.DepositoId } into ss
                    from s in ss.DefaultIfEmpty()
                    orderby p.Categoria descending, p.Nombre
                    select new StockProductoDto(
                        p.Id, p.Sku ?? "", p.Nombre, p.Categoria,
                        s != null ? s.StockGramos : 0m,
                        s != null ? s.StockUnidades : 0);

        var list = await query.ToListAsync();
        return Ok(list);
    }

    public record UpdateStockItem(int ProductoId, decimal StockGramos, int StockUnidades);
    public record UpdateStockMasivoReq(int DepositoId, List<UpdateStockItem> Items);

    /// <summary>
    /// Actualiza el stock de varios productos en un deposito.
    /// Tambien actualiza Cafe_Productos.StockGramos/Unidades como el TOTAL across all depositos.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Actualizar([FromBody] UpdateStockMasivoReq req)
    {
        var dep = await _db.CafeDepositos.FindAsync(req.DepositoId);
        if (dep is null) return BadRequest(new { error = "Deposito no existe" });
        if (req.Items == null || req.Items.Count == 0) return BadRequest(new { error = "Sin items" });

        var productoIds = req.Items.Select(i => i.ProductoId).Distinct().ToList();
        var existingMap = await _db.CafeStockPorDeposito
            .Where(s => s.DepositoId == req.DepositoId && productoIds.Contains(s.ProductoId))
            .ToDictionaryAsync(s => s.ProductoId);
        var productosMap = await _db.CafeProductos
            .Where(p => productoIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        // 2026-06-02: capturamos el operador del usuario logueado para que quede en el historial.
        // El JWT trae el username en el claim "name" o "unique_name".
        var operadorNombre = User?.Identity?.Name ?? User?.FindFirst("name")?.Value ?? "admin";

        // Tracking de stock antes/después por producto para loguear en Stock_Movimientos al final.
        var changesParaHistorial = new List<(int ProdId, int AntesU, int DespuesU)>();

        int cambios = 0;
        foreach (var item in req.Items)
        {
            if (!productosMap.TryGetValue(item.ProductoId, out var prod)) continue;

            // 1) Actualizar / crear el registro StockPorDeposito
            int antesU = prod.StockUnidades;  // capturamos el TOTAL antes (sera el "antes" del historial)
            if (existingMap.TryGetValue(item.ProductoId, out var spd))
            {
                if (spd.StockGramos == item.StockGramos && spd.StockUnidades == item.StockUnidades) continue;
                spd.StockGramos = item.StockGramos;
                spd.StockUnidades = item.StockUnidades;
                spd.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.CafeStockPorDeposito.Add(new CafeStockPorDeposito
                {
                    ProductoId = item.ProductoId,
                    DepositoId = req.DepositoId,
                    StockGramos = item.StockGramos,
                    StockUnidades = item.StockUnidades
                });
            }
            cambios++;
        }
        await _db.SaveChangesAsync();

        // 2) Re-calcular el TOTAL en Cafe_Productos (suma de todos los depositos)
        var productosModificados = new List<int>();
        if (cambios > 0)
        {
            var totales = await _db.CafeStockPorDeposito
                .Where(s => productoIds.Contains(s.ProductoId))
                .GroupBy(s => s.ProductoId)
                .Select(g => new { ProductoId = g.Key, TotalG = g.Sum(x => x.StockGramos), TotalU = g.Sum(x => x.StockUnidades) })
                .ToListAsync();
            foreach (var t in totales)
            {
                if (productosMap.TryGetValue(t.ProductoId, out var prod))
                {
                    var antesU = prod.StockUnidades;  // total ANTES del re-cálculo (lo que tenía sumando todos los depósitos)
                    var changed = (prod.StockGramos != t.TotalG) || (prod.StockUnidades != t.TotalU);
                    prod.StockGramos = t.TotalG;
                    prod.StockUnidades = t.TotalU;
                    prod.UpdatedAt = DateTime.UtcNow;
                    if (changed)
                    {
                        prod.StockChangedAt = DateTime.UtcNow;  // ← dispara que el push event-driven y el background lo procesen
                        productosModificados.Add(prod.Id);
                        changesParaHistorial.Add((prod.Id, antesU, t.TotalU));
                    }
                }
            }
            await _db.SaveChangesAsync();

            // 2026-06-02: registrar cada cambio en Stock_Movimientos para que aparezca en
            // /cafe/historial-stock. Tipo "SUMA" o "RESTA" segun el delta. Antes este endpoint
            // actualizaba el stock pero no logueaba nada -> el historial quedaba ciego para los
            // cambios masivos desde la pantalla web (solo aparecian los cambios desde mobile).
            foreach (var (prodId, antesU, despuesU) in changesParaHistorial)
            {
                if (antesU == despuesU) continue;
                var tipoMov = despuesU > antesU ? "SUMA" : "RESTA";
                await _stockLogger.LogAsync(prodId, tipoMov, antesU, despuesU,
                    operadorId: null,
                    operadorNombre: operadorNombre,
                    comentario: $"Stock masivo · {dep.Nombre}",
                    saveChanges: false);
            }
            if (changesParaHistorial.Count > 0) await _db.SaveChangesAsync();
        }

        // 3) Push event-driven a MeLi para los productos que cambiaron
        FireAndForgetPushMeli(productosModificados);

        await _audit.LogAsync("CafeStockMasivo", req.DepositoId.ToString(), "BULK_UPDATE",
            $"Actualizados {cambios} productos en deposito {dep.Nombre}");

        return Ok(new { actualizados = cambios });
    }
}
