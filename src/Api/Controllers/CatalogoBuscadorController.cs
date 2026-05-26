using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Api.Data;

namespace Api.Controllers;

/// <summary>
/// Endpoint optimizado para el buscador móvil (componente ProductSearchInput).
/// Devuelve el catálogo completo (productos + combos) en formato chico, listo para
/// cargar como índice en memoria del cliente Blazor WASM.
/// Cobertura: solo SKUs activos. Se cachea agresivo en el cliente.
/// </summary>
[ApiController]
[Route("api/catalogo-buscador")]
[Authorize]
public class CatalogoBuscadorController : ControllerBase
{
    private readonly AppDbContext _db;

    public CatalogoBuscadorController(AppDbContext db) { _db = db; }

    public record CatalogoItemDto(
        string Sku,
        string Nombre,
        string Tipo,          // "producto" | "combo"
        int Id,               // CafeProductoId o CafeComboId
        string? Categoria,
        decimal Stock,        // unidades o gramos según EsCafe
        bool EsCafe,
        DateTime? StockChangedAt);

    /// <summary>Devuelve todos los SKUs activos en un solo blob. Pensado para cargar
    /// en memoria del cliente y buscar localmente (sin ida-y-vuelta por keystroke).</summary>
    [HttpGet("all")]
    public async Task<IActionResult> GetAll()
    {
        var productos = await _db.CafeProductos
            .AsNoTracking()
            .Where(p => p.IsActive && p.Sku != null && p.Sku != "")
            .Select(p => new CatalogoItemDto(
                p.Sku!,
                p.Nombre,
                "producto",
                p.Id,
                p.Categoria,
                p.Categoria == "CAFE" ? p.StockGramos : p.StockUnidades,
                p.Categoria == "CAFE",
                p.StockChangedAt))
            .ToListAsync();

        var combos = await _db.CafeCombos
            .AsNoTracking()
            .Where(c => c.IsActive && c.Sku != null)
            .Select(c => new CatalogoItemDto(
                c.Sku!,
                c.Nombre,
                "combo",
                c.Id,
                c.Categoria,
                0m,             // los combos no tienen stock propio (se calcula sobre los componentes)
                false,
                c.UpdatedAt))
            .ToListAsync();

        var all = productos.Concat(combos).ToList();
        return Ok(new { count = all.Count, items = all });
    }
}
