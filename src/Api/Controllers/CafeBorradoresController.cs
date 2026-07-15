using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>2026-07-14: Borradores de venta COMPARTIDOS por todo el equipo (viven en el servidor,
/// no en el navegador). Sirven para dejar una venta "en espera" (ej: esperando confirmación) y
/// seguir con otra. Hasta 10 a la vez. Cualquier operador los ve y los puede retomar.</summary>
[ApiController]
[Route("api/cafe/borradores")]
[Authorize]
public class CafeBorradoresController : ControllerBase
{
    private readonly AppDbContext _db;
    public const int MaxBorradores = 10;

    public CafeBorradoresController(AppDbContext db) => _db = db;

    private string? OperadorActual()
    {
        var raw = Request.Headers["X-Operator-Name"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
    }

    /// <summary>Lista todos los borradores del equipo (máx 10), del más nuevo al más viejo.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var list = await _db.CafeVentaBorradores
            .OrderByDescending(b => b.UpdatedAt)
            .Take(MaxBorradores)
            .Select(b => new BorradorServerDto(
                b.Id, b.ClienteNombre, b.ItemsCount, b.Total,
                b.CreadoPorOperador, b.UpdatedAt, b.PayloadJson))
            .ToListAsync();
        return Ok(list);
    }

    /// <summary>Crea un borrador nuevo. Si ya hay 10, rechaza (409) para que el operador descarte alguno.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveBorradorRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.PayloadJson))
            return BadRequest(new { error = "Borrador vacío" });

        var count = await _db.CafeVentaBorradores.CountAsync();
        if (count >= MaxBorradores)
            return Conflict(new { error = $"Ya hay {MaxBorradores} borradores guardados. Descartá alguno para guardar uno nuevo.", full = true });

        var b = new CafeVentaBorradorServer
        {
            PayloadJson = req.PayloadJson,
            ClienteNombre = string.IsNullOrWhiteSpace(req.ClienteNombre) ? null : req.ClienteNombre.Trim(),
            ItemsCount = req.ItemsCount,
            Total = req.Total,
            CreadoPorOperador = OperadorActual(),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.CafeVentaBorradores.Add(b);
        await _db.SaveChangesAsync();
        return Ok(new { id = b.Id });
    }

    /// <summary>Actualiza un borrador existente (auto-guardado mientras se sigue cargando).</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SaveBorradorRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.PayloadJson))
            return BadRequest(new { error = "Borrador vacío" });

        var b = await _db.CafeVentaBorradores.FindAsync(id);
        if (b is null) return NotFound(new { error = "El borrador ya no existe (quizás alguien lo retomó o lo descartó)." });

        b.PayloadJson = req.PayloadJson;
        b.ClienteNombre = string.IsNullOrWhiteSpace(req.ClienteNombre) ? null : req.ClienteNombre.Trim();
        b.ItemsCount = req.ItemsCount;
        b.Total = req.Total;
        b.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { id = b.Id });
    }

    /// <summary>Descarta un borrador (al retomarlo y emitir, o manual).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var b = await _db.CafeVentaBorradores.FindAsync(id);
        if (b is null) return Ok(new { ok = true }); // ya no está, listo igual
        _db.CafeVentaBorradores.Remove(b);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
