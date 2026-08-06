using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-08-05: estado de la foto de un producto de café A NIVEL SISTEMA.
/// Desde /cafe/preparacion el armador aprueba (✅) o reporta como errónea (❌) la foto de un
/// producto. Es por producto → apenas uno la marca, lo ven todos. NO toca la foto de MeLi.
/// </summary>
[ApiController]
[Route("api/cafe/producto-foto")]
[Authorize]
public class CafeProductoFotoController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeProductoFotoController(AppDbContext db) { _db = db; }

    public record MarcarFotoRequest(string? Estado, string? Comentario);

    public record ProductoFotoDto(int CafeProductoId, string? Estado, string? Usuario,
        string? Comentario, DateTime UpdatedAt);

    /// <summary>Devuelve el estado de foto de todos los productos que tienen alguna marca.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        var lista = await _db.CafeProductoFotos
            .Select(f => new ProductoFotoDto(f.CafeProductoId, f.Estado, f.Usuario, f.Comentario, f.UpdatedAt))
            .ToListAsync();
        return Ok(lista);
    }

    /// <summary>
    /// Marca la foto de un producto. Estado válido: "APROBADA" | "REPORTADA".
    /// Si Estado viene vacío/null, se LIMPIA la marca (vuelve a "sin marcar").
    /// </summary>
    [HttpPost("{productoId:int}")]
    public async Task<IActionResult> Marcar(int productoId, [FromBody] MarcarFotoRequest req)
    {
        var estado = (req?.Estado ?? "").Trim().ToUpperInvariant();
        if (estado != "APROBADA" && estado != "REPORTADA" && estado != "")
            return BadRequest(new { mensaje = "Estado inválido. Usá APROBADA, REPORTADA o vacío para limpiar." });

        // El producto tiene que existir (evita basura por ids inventados).
        var existeProd = await _db.CafeProductos.AnyAsync(p => p.Id == productoId);
        if (!existeProd) return NotFound(new { mensaje = "Producto no encontrado." });

        var usuario = HttpContext.User?.Identity?.Name;
        var reg = await _db.CafeProductoFotos.FirstOrDefaultAsync(f => f.CafeProductoId == productoId);

        if (estado == "")
        {
            // Limpiar: si había registro, lo borramos.
            if (reg is not null) _db.CafeProductoFotos.Remove(reg);
            await _db.SaveChangesAsync();
            return Ok(new ProductoFotoDto(productoId, null, null, null, DateTime.UtcNow));
        }

        if (reg is null)
        {
            reg = new CafeProductoFoto { CafeProductoId = productoId };
            _db.CafeProductoFotos.Add(reg);
        }
        reg.Estado = estado;
        reg.Usuario = usuario;
        reg.Comentario = string.IsNullOrWhiteSpace(req?.Comentario) ? null : req!.Comentario!.Trim();
        reg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new ProductoFotoDto(reg.CafeProductoId, reg.Estado, reg.Usuario, reg.Comentario, reg.UpdatedAt));
    }
}
