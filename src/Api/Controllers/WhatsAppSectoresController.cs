using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>CRUD de Sectores de WhatsApp + asignacion de operarios.</summary>
[ApiController]
[Route("api/whatsapp/sectores")]
[Authorize]
public class WhatsAppSectoresController : ControllerBase
{
    private readonly AppDbContext _db;
    public WhatsAppSectoresController(AppDbContext db) { _db = db; }

    public record SectorDto(int Id, string Nombre, string? Emoji, string? Descripcion, int Orden, bool Activo, int CantOperarios);
    public record SectorUpsertDto(string Nombre, string? Emoji, string? Descripcion, int Orden, bool Activo);
    public record OperarioDto(int UsuarioId, string Username, string? FirstName, string? LastName, bool IsActive);
    public record MatrizCelda(int SectorId, int UsuarioId, bool Asignado);
    public record MatrizPayload(int[] SectorIds, int[] UsuarioIds, List<MatrizCelda> Celdas);

    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] bool incluirInactivos = false)
    {
        var q = _db.WhatsAppSectores.AsQueryable();
        if (!incluirInactivos) q = q.Where(s => s.Activo);
        var sectores = await q.OrderBy(s => s.Orden).ThenBy(s => s.Nombre).ToListAsync();

        var counts = await _db.WhatsAppSectorOperarios
            .GroupBy(o => o.SectorId)
            .Select(g => new { SectorId = g.Key, Cant = g.Count() })
            .ToDictionaryAsync(x => x.SectorId, x => x.Cant);

        var dtos = sectores.Select(s => new SectorDto(
            s.Id, s.Nombre, s.Emoji, s.Descripcion, s.Orden, s.Activo,
            counts.TryGetValue(s.Id, out var c) ? c : 0
        )).ToList();

        return Ok(dtos);
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] SectorUpsertDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio." });

        var s = new WhatsAppSector
        {
            Nombre = dto.Nombre.Trim(),
            Emoji = string.IsNullOrWhiteSpace(dto.Emoji) ? null : dto.Emoji.Trim(),
            Descripcion = string.IsNullOrWhiteSpace(dto.Descripcion) ? null : dto.Descripcion.Trim(),
            Orden = dto.Orden,
            Activo = dto.Activo
        };
        _db.WhatsAppSectores.Add(s);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, id = s.Id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] SectorUpsertDto dto)
    {
        var s = await _db.WhatsAppSectores.FindAsync(id);
        if (s == null) return NotFound(new { error = "Sector no encontrado." });
        if (string.IsNullOrWhiteSpace(dto.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio." });

        s.Nombre = dto.Nombre.Trim();
        s.Emoji = string.IsNullOrWhiteSpace(dto.Emoji) ? null : dto.Emoji.Trim();
        s.Descripcion = string.IsNullOrWhiteSpace(dto.Descripcion) ? null : dto.Descripcion.Trim();
        s.Orden = dto.Orden;
        s.Activo = dto.Activo;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var s = await _db.WhatsAppSectores.FindAsync(id);
        if (s == null) return NotFound(new { error = "Sector no encontrado." });
        // CASCADE en SectorOperarios. (No referencian conversaciones todavia.)
        _db.WhatsAppSectores.Remove(s);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Devuelve la matriz operarios x sectores para la pantalla de asignacion.</summary>
    [HttpGet("matriz")]
    public async Task<IActionResult> Matriz()
    {
        var sectores = await _db.WhatsAppSectores
            .Where(s => s.Activo)
            .OrderBy(s => s.Orden).ThenBy(s => s.Nombre)
            .ToListAsync();

        var usuarios = await _db.Users
            .Where(u => u.IsActive)
            .OrderBy(u => u.Username)
            .Select(u => new OperarioDto(u.Id, u.Username, u.FirstName, u.LastName, u.IsActive))
            .ToListAsync();

        var asignaciones = await _db.WhatsAppSectorOperarios
            .Select(o => new { o.SectorId, o.UsuarioId })
            .ToListAsync();

        var setAsig = new HashSet<(int, int)>(asignaciones.Select(a => (a.SectorId, a.UsuarioId)));

        var celdas = new List<MatrizCelda>();
        foreach (var s in sectores)
            foreach (var u in usuarios)
                celdas.Add(new MatrizCelda(s.Id, u.UsuarioId, setAsig.Contains((s.Id, u.UsuarioId))));

        return Ok(new
        {
            sectores = sectores.Select(s => new { s.Id, s.Nombre, s.Emoji, s.Orden }),
            usuarios,
            celdas
        });
    }

    public record ToggleDto(int SectorId, int UsuarioId, bool Asignado);

    /// <summary>Asigna o desasigna un operario a un sector.</summary>
    [HttpPost("toggle-operario")]
    public async Task<IActionResult> ToggleOperario([FromBody] ToggleDto dto)
    {
        var existe = await _db.WhatsAppSectorOperarios
            .FirstOrDefaultAsync(o => o.SectorId == dto.SectorId && o.UsuarioId == dto.UsuarioId);

        if (dto.Asignado && existe == null)
        {
            _db.WhatsAppSectorOperarios.Add(new WhatsAppSectorOperario
            {
                SectorId = dto.SectorId,
                UsuarioId = dto.UsuarioId
            });
            await _db.SaveChangesAsync();
        }
        else if (!dto.Asignado && existe != null)
        {
            _db.WhatsAppSectorOperarios.Remove(existe);
            await _db.SaveChangesAsync();
        }
        return Ok(new { ok = true });
    }
}
