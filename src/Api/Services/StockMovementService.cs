using System.Text.Json;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Maneja los depositos y los movimientos manuales de stock (la pantalla
/// "Modificacion de stock"). Por ahora el stock real sigue viviendo en
/// Products.Stock (un solo numero); cada movimiento queda registrado con
/// el deposito asociado para futuras separaciones por deposito.
/// </summary>
public class StockMovementService
{
    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;

    private static readonly string[] AllowedTypes =
        new[] { "ingreso", "egreso", "ajuste", "rotura", "merma", "devolucion", "conteo", "otro" };

    public StockMovementService(AppDbContext db, AuditLogService audit)
    {
        _db = db;
        _audit = audit;
    }

    // === Depositos ===

    public async Task<List<WarehouseDto>> GetWarehousesAsync()
    {
        return await _db.Warehouses
            .OrderBy(w => w.SortOrder).ThenBy(w => w.Name)
            .Select(w => new WarehouseDto(w.Id, w.Code, w.Name, w.Address, w.Notes, w.IsDefault, w.IsActive, w.SortOrder))
            .ToListAsync();
    }

    public async Task<int?> GetDefaultWarehouseIdAsync()
    {
        return await _db.Warehouses
            .Where(w => w.IsDefault && w.IsActive)
            .Select(w => (int?)w.Id)
            .FirstOrDefaultAsync();
    }

    // === Movimientos ===

    /// <summary>
    /// Aplica un ajuste de stock: actualiza Products.Stock y registra el movimiento.
    /// Para 'ajuste' / 'conteo': Quantity es el valor absoluto final.
    /// Para 'ingreso' / 'devolucion': se suma Quantity al stock actual.
    /// Para 'egreso' / 'rotura' / 'merma' / 'otro': se resta Quantity del stock actual.
    /// </summary>
    public async Task<StockMovementDto> AdjustAsync(AdjustStockRequest req)
    {
        if (!AllowedTypes.Contains(req.MovementType))
            throw new InvalidOperationException($"Tipo de movimiento invalido: '{req.MovementType}'.");

        var product = await _db.Products.FindAsync(req.ProductId)
            ?? throw new InvalidOperationException("Producto no encontrado.");
        var warehouse = await _db.Warehouses.FindAsync(req.WarehouseId)
            ?? throw new InvalidOperationException("Deposito no encontrado.");

        var stockBefore = product.Stock;
        decimal newStock;
        decimal delta;

        switch (req.MovementType)
        {
            case "ajuste":
            case "conteo":
                // Quantity = stock final absoluto
                newStock = req.Quantity;
                delta = newStock - stockBefore;
                break;
            case "ingreso":
            case "devolucion":
                delta = Math.Abs(req.Quantity);
                newStock = stockBefore + delta;
                break;
            default: // egreso, rotura, merma, otro
                delta = -Math.Abs(req.Quantity);
                newStock = stockBefore + delta; // delta es negativo, resta
                break;
        }

        product.Stock = newStock;
        product.UpdatedAt = DateTime.UtcNow;

        var mov = new StockMovement
        {
            ProductId = product.Id,
            WarehouseId = warehouse.Id,
            MovementType = req.MovementType,
            DeltaQuantity = delta,
            StockBefore = stockBefore,
            StockAfter = newStock,
            Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim(),
            Notes = string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
            OperatorName = string.IsNullOrWhiteSpace(req.OperatorName) ? null : req.OperatorName.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _db.StockMovements.Add(mov);
        await _db.SaveChangesAsync();

        await _audit.LogAsync("Product", product.Id.ToString(), "STOCK_ADJUST",
            JsonSerializer.Serialize(new
            {
                productSku = product.Sku,
                warehouse = warehouse.Name,
                tipo = req.MovementType,
                stockAnterior = stockBefore,
                stockNuevo = newStock,
                delta,
                motivo = req.Reason,
                operador = req.OperatorName
            }), req.OperatorName);

        return ToDto(mov, product, warehouse);
    }

    /// <summary>Lista los ultimos movimientos. Filtrable por producto y/o deposito.</summary>
    public async Task<List<StockMovementDto>> GetMovementsAsync(int? productId = null, int? warehouseId = null, int take = 50)
    {
        var q = _db.StockMovements
            .Include(m => m.Product)
            .Include(m => m.Warehouse)
            .AsQueryable();

        if (productId.HasValue) q = q.Where(m => m.ProductId == productId.Value);
        if (warehouseId.HasValue) q = q.Where(m => m.WarehouseId == warehouseId.Value);

        return await q
            .OrderByDescending(m => m.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(m => new StockMovementDto(
                m.Id, m.ProductId, m.Product != null ? m.Product.Sku : null,
                m.Product != null ? m.Product.Title : "(eliminado)",
                m.WarehouseId, m.Warehouse != null ? m.Warehouse.Name : "—",
                m.MovementType, m.DeltaQuantity, m.StockBefore, m.StockAfter,
                m.Reason, m.Notes, m.OperatorName, m.CreatedAt))
            .ToListAsync();
    }

    private static StockMovementDto ToDto(StockMovement m, Product p, Warehouse w) => new(
        m.Id, m.ProductId, p.Sku, p.Title,
        m.WarehouseId, w.Name,
        m.MovementType, m.DeltaQuantity, m.StockBefore, m.StockAfter,
        m.Reason, m.Notes, m.OperatorName, m.CreatedAt);
}
