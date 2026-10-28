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
        DateTime? StockChangedAt,
        string? Marca);       // FRIKAF, COLOMBRARO, MASCARDI, etc. - usado para filtro rapido en buscador mobile

    /// <summary>Devuelve todos los SKUs activos en un solo blob. Pensado para cargar
    /// en memoria del cliente y buscar localmente (sin ida-y-vuelta por keystroke).
    ///
    /// Parámetro opcional onlyProductos (default false):
    ///   - false: devuelve productos + combos (compatibilidad con /test-search y otros)
    ///   - true: devuelve SOLO productos puros (~1.893 items en lugar de ~4.136)
    ///           usado por la pantalla de carga de stock móvil (no se cargan combos manuales)
    /// 2026-05-28 (Prompt 2 cargador stock móvil).
    /// </summary>
    /// <summary>28/10/2026: el mismo catálogo para buscar mientras se escribe en la CALCUPRECIOS del celu
    /// del repartidor. Solo con el tilde "calcuprecios" en su ficha (Services/RepartidorOficina.cs).</summary>
    [HttpGet("repartidor/{token}/all")]
    [AllowAnonymous]
    public async Task<IActionResult> GetAllRepartidor(string token)
    {
        if (await Api.Services.RepartidorOficina.ConPermisoAsync(_db, token, Api.Services.RepartidorOficina.CalcuPrecios) is null) return NotFound(new { error = "No tenés habilitado ver esto" });
        return await GetAll(false);
    }

    [HttpGet("all")]
    public async Task<IActionResult> GetAll([FromQuery] bool onlyProductos = false)
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
                p.StockChangedAt,
                p.Marca))
            .ToListAsync();

        if (onlyProductos)
        {
            // Cargador de stock móvil: solo productos puros, sin combos. Baja el JSON a la mitad.
            return Ok(new { count = productos.Count, items = productos });
        }

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
                c.UpdatedAt,
                null))         // los combos no tienen marca propia
            .ToListAsync();

        var all = productos.Concat(combos).ToList();
        return Ok(new { count = all.Count, items = all });
    }
}
