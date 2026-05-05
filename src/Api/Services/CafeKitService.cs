using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

// Servicio de Kits (productos compuestos / BOM).
// Stock virtual = MIN(stock componente / cantidad necesaria), redondeado abajo.
// Vender un kit descuenta componentes en una transaccion (no se almacena stock propio).
public class CafeKitService
{
    private readonly AppDbContext _db;

    public CafeKitService(AppDbContext db) { _db = db; }

    // Calcula el stock virtual del kit a partir de sus componentes.
    // Para cada componente: cuantos kits puedo armar = floor(stock_componente / cantidad).
    // El stock disponible del kit = MIN de todos los componentes.
    public static int CalcularStock(CafeKit kit)
    {
        if (kit.Items.Count == 0) return 0;
        int? minimo = null;
        foreach (var it in kit.Items)
        {
            if (it.Cantidad <= 0) continue;
            var stockComp = it.Producto?.StockUnidades ?? 0;
            var posibles = (int)Math.Floor(stockComp / it.Cantidad);
            if (minimo is null || posibles < minimo) minimo = posibles;
        }
        return minimo ?? 0;
    }

    public static decimal CalcularCosto(CafeKit kit)
    {
        decimal total = 0m;
        foreach (var it in kit.Items)
        {
            var costoComp = it.Producto?.Costo ?? 0m;
            total += costoComp * it.Cantidad;
        }
        return Math.Round(total, 2, MidpointRounding.AwayFromZero);
    }

    public async Task<CafeKitDto> MapAsync(CafeKit k)
    {
        // Asegurar que tenemos componentes con producto cargado.
        if (k.Items is null || k.Items.Count == 0 || k.Items.Any(i => i.Producto is null))
        {
            await _db.Entry(k).Collection(x => x.Items).LoadAsync();
            foreach (var it in k.Items) await _db.Entry(it).Reference(x => x.Producto).LoadAsync();
        }

        var stockVirtual = CalcularStock(k);
        var costoCalculado = CalcularCosto(k);

        var items = k.Items.Select(i => new CafeKitItemDto(
            i.Id, i.ProductoId,
            i.Producto?.Sku, i.Producto?.Nombre ?? "(producto eliminado)",
            i.Producto?.StockUnidades ?? 0,
            i.Cantidad,
            i.Producto is null ? 0 : (int)Math.Floor((i.Producto.StockUnidades) / Math.Max(0.001m, i.Cantidad))
        )).ToList();

        return new CafeKitDto(
            k.Id, k.Sku, k.Nombre, k.Descripcion,
            k.Categoria, k.Marca, k.MarcaId, k.MarcaNav?.Nombre,
            k.Pvp1, k.Pvp2, k.IvaPct,
            k.Notas, k.IsActive,
            stockVirtual, costoCalculado,
            items, k.CreatedAt, k.UpdatedAt);
    }

    // Vende N kits: descuenta stock de cada componente. Lanza si no hay stock suficiente.
    // Reutiliza el campo StockUnidades de Cafe_Productos (el sistema actual de stock).
    public async Task<List<(int productoId, string? sku, decimal cantidadDescontada, int stockAntes, int stockDespues)>>
        DescontarStockPorVentaAsync(int kitId, int unidadesVendidas)
    {
        if (unidadesVendidas <= 0) throw new ArgumentException("La cantidad debe ser mayor a 0.", nameof(unidadesVendidas));

        var kit = await _db.CafeKits
            .Include(k => k.Items).ThenInclude(i => i.Producto)
            .FirstOrDefaultAsync(k => k.Id == kitId);
        if (kit is null) throw new InvalidOperationException($"Kit {kitId} no encontrado.");
        if (kit.Items.Count == 0) throw new InvalidOperationException($"El kit {kit.Sku} no tiene componentes definidos.");

        // Validar stock suficiente PRIMERO (sin tocar nada).
        var stockDisponible = CalcularStock(kit);
        if (stockDisponible < unidadesVendidas)
            throw new InvalidOperationException(
                $"Stock insuficiente para el kit {kit.Sku}: disponible {stockDisponible}, se intentan vender {unidadesVendidas}.");

        // Descontar componente a componente, en una transaccion.
        using var tx = await _db.Database.BeginTransactionAsync();
        var movimientos = new List<(int, string?, decimal, int, int)>();
        try
        {
            foreach (var it in kit.Items)
            {
                if (it.Producto is null) throw new InvalidOperationException(
                    $"El kit {kit.Sku} referencia un producto eliminado (componente Id {it.ProductoId}).");

                var totalADescontar = it.Cantidad * unidadesVendidas;
                var stockAntes = it.Producto.StockUnidades;
                var stockDespues = stockAntes - (int)totalADescontar;
                if (stockDespues < 0) throw new InvalidOperationException(
                    $"Stock insuficiente del componente {it.Producto.Sku}: disponible {stockAntes}, se necesitan {totalADescontar}.");

                it.Producto.StockUnidades = stockDespues;
                it.Producto.UpdatedAt = DateTime.UtcNow;
                movimientos.Add((it.ProductoId, it.Producto.Sku, totalADescontar, stockAntes, stockDespues));
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return movimientos;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
