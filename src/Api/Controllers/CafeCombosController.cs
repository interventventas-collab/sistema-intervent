using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/combos")]
[Authorize]
public class CafeCombosController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly string[] FormatosValidos = { "1KG", "MEDIO", "CUARTO", "UNIT" };
    private static readonly string[] MoliendasValidas = { "EN GRANOS", "MOLIDO FILTRO", "MOLIDO ESPRESS", "MOLIDO MOKA", "MOLIDO BODUM", "MINI EXPRESS" };

    public CafeCombosController(AppDbContext db) { _db = db; }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool? activos = null)
    {
        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        var q = _db.CafeCombos.Include(c => c.Items).ThenInclude(i => i.ProductoNav).AsQueryable();
        if (activos == true) q = q.Where(c => c.IsActive);

        var combos = await q.OrderBy(c => c.Nombre).ToListAsync();
        return Ok(combos.Select(c => Map(c, settings)).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        var c = await _db.CafeCombos.Include(x => x.Items).ThenInclude(i => i.ProductoNav)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound(new { error = "Combo no encontrado" });
        return Ok(Map(c, settings));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeComboRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "El combo debe tener al menos 1 item" });

        var combo = new CafeCombo
        {
            Nombre = req.Nombre.Trim(),
            Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var validacion = await ValidarYAgregarItemsAsync(combo, req.Items);
        if (validacion is not null) return BadRequest(new { error = validacion });

        _db.CafeCombos.Add(combo);
        await _db.SaveChangesAsync();

        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        return Ok(Map(await ReloadAsync(combo.Id), settings));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeComboRequest req)
    {
        var combo = await _db.CafeCombos.Include(c => c.Items)
            .FirstOrDefaultAsync(c => c.Id == id);
        if (combo is null) return NotFound(new { error = "Combo no encontrado" });

        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre))
                return BadRequest(new { error = "El nombre no puede estar vacio" });
            combo.Nombre = req.Nombre.Trim();
        }
        if (req.Descripcion is not null)
            combo.Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim();
        if (req.IsActive.HasValue) combo.IsActive = req.IsActive.Value;

        if (req.Items is not null)
        {
            if (req.Items.Count == 0)
                return BadRequest(new { error = "El combo debe tener al menos 1 item" });

            // Reemplazar todos los items
            _db.CafeComboItems.RemoveRange(combo.Items);
            combo.Items.Clear();

            var validacion = await ValidarYAgregarItemsAsync(combo, req.Items);
            if (validacion is not null) return BadRequest(new { error = validacion });
        }

        combo.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        return Ok(Map(await ReloadAsync(combo.Id), settings));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var combo = await _db.CafeCombos.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id);
        if (combo is null) return NotFound(new { error = "Combo no encontrado" });
        _db.CafeCombos.Remove(combo);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    // ============================================================
    // Helpers
    // ============================================================

    private async Task<CafeCombo> ReloadAsync(int id)
    {
        return (await _db.CafeCombos.Include(c => c.Items).ThenInclude(i => i.ProductoNav)
            .FirstAsync(c => c.Id == id));
    }

    /// <summary>Valida + carga los items en el combo. Devuelve null si todo bien, mensaje de error si no.</summary>
    private async Task<string?> ValidarYAgregarItemsAsync(CafeCombo combo, List<CafeComboItemRequest> items)
    {
        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Cantidad <= 0) return $"Item {i + 1}: la cantidad debe ser mayor a 0";
            if (!FormatosValidos.Contains(it.Formato)) return $"Item {i + 1}: formato invalido '{it.Formato}'";

            var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
            if (prod is null) return $"Item {i + 1}: producto {it.ProductoId} no encontrado";

            // Coherencia: formato unitario solo para OTROS, formatos kg solo para CAFE
            var esCafe = prod.Categoria == "CAFE";
            var esFormatoCafe = it.Formato is "1KG" or "MEDIO" or "CUARTO";
            if (esCafe != esFormatoCafe)
                return $"Item {i + 1} ({prod.Nombre}): " +
                    (esCafe ? "para cafe usa 1 kg / 1/2 kg / 1/4 kg" : "para otros productos usa 'unidad'");

            string? molienda = null;
            if (esCafe && !string.IsNullOrWhiteSpace(it.Molienda))
            {
                var m = it.Molienda.Trim().ToUpperInvariant();
                if (MoliendasValidas.Contains(m)) molienda = m;
            }

            combo.Items.Add(new CafeComboItem
            {
                ProductoId = prod.Id,
                Formato = it.Formato,
                Cantidad = it.Cantidad,
                Molienda = molienda,
                EsDoyPack = it.EsDoyPack && esCafe,
                EsEnvasePlateado = it.EsEnvasePlateado && esCafe && !it.EsDoyPack,
                SortOrder = it.SortOrder == 0 ? i : it.SortOrder
            });
        }
        return null;
    }

    private static CafeComboDto Map(CafeCombo c, CafeSetting settings)
    {
        decimal precioBar = 0m, precioOtro = 0m;
        foreach (var it in c.Items)
        {
            var prod = it.ProductoNav;
            if (prod is null) continue;
            precioBar += CafePricingService.CalcularPrecioUnitario(prod, it.Formato, "BAR", settings) * it.Cantidad;
            precioOtro += CafePricingService.CalcularPrecioUnitario(prod, it.Formato, "OTRO", settings) * it.Cantidad;
        }

        return new CafeComboDto(
            c.Id, c.Nombre, c.Descripcion,
            c.IsActive, c.CreatedAt, c.UpdatedAt,
            c.Items.Count,
            precioBar, precioOtro,
            c.Items.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => new CafeComboItemDto(
                x.Id, x.ProductoId,
                x.ProductoNav?.Nombre ?? "?",
                x.ProductoNav?.Categoria ?? "CAFE",
                x.ProductoNav?.Marca,
                x.ProductoNav?.Sku,
                x.ProductoNav?.Pvp1,
                x.ProductoNav?.Pvp2,
                x.Formato, x.Cantidad,
                x.Molienda, x.EsDoyPack,
                x.SortOrder,
                x.EsEnvasePlateado)).ToList()
        );
    }
}
