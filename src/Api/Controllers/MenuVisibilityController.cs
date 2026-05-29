using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace Api.Controllers;

/// <summary>
/// Visibilidad del sidebar por rol (deposito, oficina). El admin tilda/destilda items desde el
/// modo edición del propio sidebar — esta API persiste los cambios.
/// Pedido del usuario 2026-05-28.
/// </summary>
[ApiController]
[Route("api/menu-visibility")]
[Authorize]
public class MenuVisibilityController : ControllerBase
{
    private readonly AppDbContext _db;

    public MenuVisibilityController(AppDbContext db) { _db = db; }

    /// <summary>
    /// Devuelve dict {role: [keys]} con los items habilitados por rol.
    /// Cualquier usuario logueado puede leerlo (lo necesita el frontend para renderizar el sidebar).
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var rows = await _db.MenuVisibility
            .AsNoTracking()
            .Select(m => new { m.RoleName, m.MenuKey })
            .ToListAsync();
        var dict = rows
            .GroupBy(r => r.RoleName)
            .ToDictionary(g => g.Key, g => g.Select(x => x.MenuKey).ToList());
        return Ok(dict);
    }

    public record SetVisibilityRequest(string Role, string Key, bool Enabled);

    /// <summary>
    /// Toggle de un item para un rol. Solo admin puede modificar.
    /// Si enabled=true → inserta (si no existe). Si enabled=false → borra.
    /// </summary>
    [HttpPost("set")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Set([FromBody] SetVisibilityRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Role) || string.IsNullOrWhiteSpace(req.Key))
            return BadRequest(new { error = "role y key son obligatorios" });
        var role = req.Role.Trim().ToLowerInvariant();
        var key = req.Key.Trim();

        var existing = await _db.MenuVisibility
            .FirstOrDefaultAsync(m => m.RoleName == role && m.MenuKey == key);

        if (req.Enabled)
        {
            if (existing is null)
            {
                _db.MenuVisibility.Add(new MenuVisibility { RoleName = role, MenuKey = key });
                await _db.SaveChangesAsync();
            }
        }
        else
        {
            if (existing is not null)
            {
                _db.MenuVisibility.Remove(existing);
                await _db.SaveChangesAsync();
            }
        }
        return Ok(new { role, key, enabled = req.Enabled });
    }
}
