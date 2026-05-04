using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/compras")]
[Authorize]
public class CafeComprasController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeComprasController(AppDbContext db) { _db = db; }

    private const string EST_BORRADOR = "BORRADOR";
    private const string EST_CONFIRMADA = "CONFIRMADA";
    private const string EST_PAGADA = "PAGADA";
    private const string EST_ANULADA = "ANULADA";

    private static CafeCompraDto Map(CafeCompra c) => new(
        c.Id, c.Numero, c.ProveedorId,
        c.ProveedorNav?.Nombre ?? c.ProveedorNombreSnapshot,
        c.Fecha, c.NumeroComprobante, c.Estado, c.Total, c.Observaciones,
        c.CreatedAt, c.UpdatedAt, c.ConfirmadaAt, c.PagadaAt, c.AnuladaAt,
        c.Items.Select(i => new CafeCompraItemDto(
            i.Id, i.ProductoId, i.ProductoNombreSnapshot,
            i.ProductoNav?.Sku, i.Categoria,
            i.Cantidad, i.CostoUnitario, i.Subtotal,
            i.ProductoNav?.StockGramos ?? 0m,
            i.ProductoNav?.StockUnidades ?? 0,
            i.ProductoNav?.Costo ?? 0m)).ToList());

    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] DateTime? from = null,
        [FromQuery] DateTime? to = null,
        [FromQuery] string? estado = null,
        [FromQuery] int? proveedorId = null)
    {
        var q = _db.CafeCompras
            .Include(c => c.ProveedorNav)
            .Include(c => c.Items).ThenInclude(i => i.ProductoNav)
            .AsQueryable();
        if (from.HasValue) q = q.Where(c => c.Fecha >= from.Value.Date);
        if (to.HasValue) q = q.Where(c => c.Fecha <= to.Value.Date.AddDays(1));
        if (!string.IsNullOrWhiteSpace(estado)) q = q.Where(c => c.Estado == estado);
        if (proveedorId.HasValue) q = q.Where(c => c.ProveedorId == proveedorId.Value);
        var list = await q.OrderByDescending(c => c.Fecha).ThenByDescending(c => c.Id).Take(500).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var c = await _db.CafeCompras
            .Include(x => x.ProveedorNav)
            .Include(x => x.Items).ThenInclude(i => i.ProductoNav)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound(new { error = "Compra no encontrada" });
        return Ok(Map(c));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeCompraRequest req)
    {
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "La compra debe tener al menos un item" });

        // Validar proveedor si vino
        CafeProveedor? prov = null;
        if (req.ProveedorId.HasValue && req.ProveedorId.Value > 0)
        {
            prov = await _db.CafeProveedores.FindAsync(req.ProveedorId.Value);
            if (prov is null) return BadRequest(new { error = "Proveedor no encontrado" });
        }

        var c = new CafeCompra
        {
            Numero = await GenerarNumeroAsync(),
            ProveedorId = prov?.Id,
            ProveedorNombreSnapshot = prov?.Nombre,
            Fecha = (req.Fecha ?? DateTime.Today).Date,
            NumeroComprobante = NullIfEmpty(req.NumeroComprobante),
            Estado = EST_BORRADOR,
            Observaciones = NullIfEmpty(req.Observaciones),
            CreatedAt = DateTime.UtcNow
        };

        var validacion = await ValidarYAgregarItemsAsync(c, req.Items);
        if (validacion is not null) return BadRequest(new { error = validacion });

        c.Total = c.Items.Sum(i => i.Subtotal);
        _db.CafeCompras.Add(c);
        await _db.SaveChangesAsync();
        return Ok(Map(await ReloadAsync(c.Id)));
    }

    /// <summary>Edita una compra solo si esta en BORRADOR. Una vez confirmada no se puede editar items.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeCompraRequest req)
    {
        var c = await _db.CafeCompras.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound(new { error = "Compra no encontrada" });
        if (c.Estado != EST_BORRADOR)
            return BadRequest(new { error = $"Solo se puede editar una compra en BORRADOR (esta es {c.Estado})." });

        if (req.ProveedorId.HasValue && req.ProveedorId.Value > 0)
        {
            var prov = await _db.CafeProveedores.FindAsync(req.ProveedorId.Value);
            if (prov is null) return BadRequest(new { error = "Proveedor no encontrado" });
            c.ProveedorId = prov.Id;
            c.ProveedorNombreSnapshot = prov.Nombre;
        }
        else if (req.ClearProveedor)
        {
            c.ProveedorId = null;
            c.ProveedorNombreSnapshot = null;
        }
        if (req.Fecha.HasValue) c.Fecha = req.Fecha.Value.Date;
        if (req.NumeroComprobante is not null) c.NumeroComprobante = NullIfEmpty(req.NumeroComprobante);
        if (req.Observaciones is not null) c.Observaciones = NullIfEmpty(req.Observaciones);

        if (req.Items is not null)
        {
            if (req.Items.Count == 0)
                return BadRequest(new { error = "La compra debe tener al menos un item" });
            _db.CafeCompraItems.RemoveRange(c.Items);
            c.Items.Clear();
            var v = await ValidarYAgregarItemsAsync(c, req.Items);
            if (v is not null) return BadRequest(new { error = v });
            c.Total = c.Items.Sum(i => i.Subtotal);
        }

        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(await ReloadAsync(c.Id)));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.CafeCompras.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound(new { error = "Compra no encontrada" });
        if (c.Estado != EST_BORRADOR)
            return BadRequest(new { error = $"Solo se puede eliminar una compra en BORRADOR (esta es {c.Estado}). Si necesitas anular usa el endpoint /anular." });
        _db.CafeCompras.Remove(c);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    /// <summary>CONFIRMA una compra: incrementa stock + actualiza costo de cada producto al ultimo costo.
    /// Operacion transaccional. Solo valida desde BORRADOR.</summary>
    [HttpPost("{id:int}/confirmar")]
    public async Task<IActionResult> Confirmar(int id)
    {
        var c = await _db.CafeCompras
            .Include(x => x.Items).ThenInclude(i => i.ProductoNav)
            .Include(x => x.ProveedorNav)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound(new { error = "Compra no encontrada" });
        if (c.Estado != EST_BORRADOR)
            return BadRequest(new { error = $"Solo se puede confirmar desde BORRADOR (esta esta en {c.Estado})." });
        if (c.Items.Count == 0)
            return BadRequest(new { error = "La compra no tiene items" });

        using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var ahora = DateTime.UtcNow;
            foreach (var item in c.Items)
            {
                var prod = item.ProductoNav ?? await _db.CafeProductos.FindAsync(item.ProductoId);
                if (prod is null) throw new InvalidOperationException($"Producto {item.ProductoId} no encontrado");

                // 1. Incrementar stock segun categoria.
                if (prod.Categoria == "CAFE")
                {
                    // Cantidad viene en kg → sumar a gramos.
                    prod.StockGramos += item.Cantidad * 1000m;
                }
                else
                {
                    // Cantidad en unidades (decimal pero generalmente entero).
                    prod.StockUnidades += (int)Math.Round(item.Cantidad, MidpointRounding.AwayFromZero);
                }

                // 2. Estrategia "ultimo costo": pisar Costo del producto.
                prod.Costo = item.CostoUnitario;
                prod.UpdatedAt = ahora;
            }

            c.Estado = EST_CONFIRMADA;
            c.ConfirmadaAt = ahora;
            c.UpdatedAt = ahora;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return StatusCode(500, new { error = "Error al confirmar: " + ex.Message });
        }
        return Ok(Map(await ReloadAsync(c.Id)));
    }

    /// <summary>Marca como PAGADA (sin efectos sobre stock/costo). Solo desde CONFIRMADA.</summary>
    [HttpPost("{id:int}/pagar")]
    public async Task<IActionResult> Pagar(int id)
    {
        var c = await _db.CafeCompras.FindAsync(id);
        if (c is null) return NotFound(new { error = "Compra no encontrada" });
        if (c.Estado != EST_CONFIRMADA)
            return BadRequest(new { error = $"Solo se puede pagar una compra CONFIRMADA (esta es {c.Estado})." });
        c.Estado = EST_PAGADA;
        c.PagadaAt = DateTime.UtcNow;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(await ReloadAsync(c.Id)));
    }

    /// <summary>ANULAR una compra confirmada/pagada: revierte el stock que sumo. El costo NO se revierte
    /// (la estrategia "ultimo costo" no tiene historial — si querias el costo anterior, lo seteas a mano).
    /// Solo valida desde CONFIRMADA o PAGADA.</summary>
    [HttpPost("{id:int}/anular")]
    public async Task<IActionResult> Anular(int id)
    {
        var c = await _db.CafeCompras.Include(x => x.Items).ThenInclude(i => i.ProductoNav)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound(new { error = "Compra no encontrada" });
        if (c.Estado != EST_CONFIRMADA && c.Estado != EST_PAGADA)
            return BadRequest(new { error = $"Solo se puede anular una compra CONFIRMADA o PAGADA (esta es {c.Estado})." });

        using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var ahora = DateTime.UtcNow;
            foreach (var item in c.Items)
            {
                var prod = item.ProductoNav ?? await _db.CafeProductos.FindAsync(item.ProductoId);
                if (prod is null) continue;
                // Revertir stock: restar lo que se habia sumado al confirmar.
                if (prod.Categoria == "CAFE")
                {
                    prod.StockGramos = Math.Max(0m, prod.StockGramos - item.Cantidad * 1000m);
                }
                else
                {
                    prod.StockUnidades = Math.Max(0, prod.StockUnidades - (int)Math.Round(item.Cantidad, MidpointRounding.AwayFromZero));
                }
                prod.UpdatedAt = ahora;
            }
            c.Estado = EST_ANULADA;
            c.AnuladaAt = ahora;
            c.UpdatedAt = ahora;
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            await tx.RollbackAsync();
            return StatusCode(500, new { error = "Error al anular: " + ex.Message });
        }
        return Ok(Map(await ReloadAsync(c.Id)));
    }

    // ============================================================
    // Helpers
    // ============================================================

    private async Task<CafeCompra> ReloadAsync(int id)
    {
        return await _db.CafeCompras
            .Include(c => c.ProveedorNav)
            .Include(c => c.Items).ThenInclude(i => i.ProductoNav)
            .FirstAsync(c => c.Id == id);
    }

    private async Task<string?> ValidarYAgregarItemsAsync(CafeCompra compra, List<CafeCompraItemRequest> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Cantidad <= 0) return $"Item {i + 1}: la cantidad debe ser mayor a 0";
            if (it.CostoUnitario < 0) return $"Item {i + 1}: el costo unitario no puede ser negativo";
            var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
            if (prod is null) return $"Item {i + 1}: producto {it.ProductoId} no encontrado";

            var cantidad = Math.Round(it.Cantidad, 3, MidpointRounding.AwayFromZero);
            var costoUnit = Math.Round(it.CostoUnitario, 2, MidpointRounding.AwayFromZero);
            var subtotal = Math.Round(cantidad * costoUnit, 2, MidpointRounding.AwayFromZero);

            compra.Items.Add(new CafeCompraItem
            {
                ProductoId = prod.Id,
                ProductoNombreSnapshot = prod.Nombre,
                Categoria = prod.Categoria,
                Cantidad = cantidad,
                CostoUnitario = costoUnit,
                Subtotal = subtotal
            });
        }
        return null;
    }

    private async Task<string> GenerarNumeroAsync()
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"COMPRA-{year}-";
        var existing = await _db.CafeCompras
            .Where(c => c.Numero.StartsWith(prefix))
            .Select(c => c.Numero)
            .ToListAsync();
        int max = 0;
        foreach (var s in existing)
        {
            if (int.TryParse(s.Substring(prefix.Length), out var n) && n > max) max = n;
        }
        return $"{prefix}{(max + 1):D4}";
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
