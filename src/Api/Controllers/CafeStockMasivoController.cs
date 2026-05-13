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

    public CafeStockMasivoController(AppDbContext db, AuditLogService audit)
    {
        _db = db; _audit = audit;
    }

    public record StockProductoDto(
        int ProductoId, string Codigo, string Nombre, string Categoria,
        decimal StockGramos, int StockUnidades);

    /// <summary>Lista todos los productos con su stock en el deposito indicado.</summary>
    [HttpGet("{depositoId:int}")]
    public async Task<IActionResult> ListarStockEnDeposito(int depositoId)
    {
        var dep = await _db.CafeDepositos.FindAsync(depositoId);
        if (dep is null) return NotFound(new { error = "Deposito no encontrado" });

        var query = from p in _db.CafeProductos
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

        int cambios = 0;
        foreach (var item in req.Items)
        {
            if (!productosMap.TryGetValue(item.ProductoId, out var prod)) continue;

            // 1) Actualizar / crear el registro StockPorDeposito
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
                    prod.StockGramos = t.TotalG;
                    prod.StockUnidades = t.TotalU;
                    prod.UpdatedAt = DateTime.UtcNow;
                }
            }
            await _db.SaveChangesAsync();
        }

        await _audit.LogAsync("CafeStockMasivo", req.DepositoId.ToString(), "BULK_UPDATE",
            $"Actualizados {cambios} productos en deposito {dep.Nombre}");

        return Ok(new { actualizados = cambios });
    }
}
