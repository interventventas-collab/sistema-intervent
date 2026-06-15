using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>2026-06-15: PIN corto (4 dígitos) por operador para autenticar la sesión
/// de trabajo. La sesión admin se sigue manejando con el login normal de usuario;
/// el PIN solo identifica quién (de GERMAN/GABRIEL/OSMAR/...) está cargando.
/// La inactividad de 30 min la administra el frontend (limpia el "validatedAt").</summary>
[ApiController]
[Route("api/operadores")]
[Authorize] // requiere estar logueado en el sistema (admin)
public class OperadoresPinController : ControllerBase
{
    private readonly AppDbContext _db;
    public OperadoresPinController(AppDbContext db) { _db = db; }

    // Operadores conocidos — los mismos del front (OperatorService.Operators)
    private static readonly string[] OperadoresValidos =
        { "OSMAR", "GERMAN", "GABRIEL", "MIGUEL", "ALEXIS", "WALTER", "RODRIGO" };

    public record ValidarPinRequest(string Nombre, string Pin);
    public record CambiarPinRequest(string Nombre, string PinActual, string PinNuevo);
    public record ResetPinRequest(string Nombre, string PinNuevo);
    public record OperadorPinInfoDto(string Nombre, bool TienePin, DateTime? UpdatedAt, string? UpdatedBy);

    /// <summary>Valida el PIN del operador. 200 si ok, 401 si mal, 404 si no tiene PIN seteado.</summary>
    [HttpPost("pin/validar")]
    public async Task<IActionResult> ValidarPin([FromBody] ValidarPinRequest req)
    {
        var nombre = (req.Nombre ?? "").ToUpperInvariant().Trim();
        if (!OperadoresValidos.Contains(nombre)) return BadRequest(new { error = "Operador desconocido" });
        if (string.IsNullOrEmpty(req.Pin) || req.Pin.Length < 4)
            return BadRequest(new { error = "PIN inválido" });

        var row = await _db.CafeOperadoresPin.FindAsync(nombre);
        if (row is null)
            return NotFound(new { error = $"{nombre} todavía no tiene PIN configurado. Pedile al admin que se lo cree." });

        if (!BCrypt.Net.BCrypt.Verify(req.Pin, row.PinHash))
            return Unauthorized(new { error = "PIN incorrecto" });

        return Ok(new { ok = true, nombre });
    }

    /// <summary>El operador cambia su propio PIN — pide el actual + el nuevo.</summary>
    [HttpPost("pin/cambiar")]
    public async Task<IActionResult> CambiarPin([FromBody] CambiarPinRequest req)
    {
        var nombre = (req.Nombre ?? "").ToUpperInvariant().Trim();
        if (!OperadoresValidos.Contains(nombre)) return BadRequest(new { error = "Operador desconocido" });
        if (string.IsNullOrEmpty(req.PinNuevo) || req.PinNuevo.Length != 4 || !req.PinNuevo.All(char.IsDigit))
            return BadRequest(new { error = "El PIN nuevo debe tener exactamente 4 dígitos" });

        var row = await _db.CafeOperadoresPin.FindAsync(nombre);
        if (row is null) return NotFound(new { error = "Operador sin PIN configurado" });
        if (!BCrypt.Net.BCrypt.Verify(req.PinActual ?? "", row.PinHash))
            return Unauthorized(new { error = "PIN actual incorrecto" });

        row.PinHash = BCrypt.Net.BCrypt.HashPassword(req.PinNuevo);
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = nombre;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Admin: lista los operadores con info de si tienen PIN cargado (no devuelve hashes).</summary>
    [HttpGet("admin/lista")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> ListaAdmin()
    {
        var existentes = await _db.CafeOperadoresPin.ToDictionaryAsync(p => p.Nombre);
        var lista = OperadoresValidos.Select(nombre =>
        {
            existentes.TryGetValue(nombre, out var p);
            return new OperadorPinInfoDto(nombre, p is not null, p?.UpdatedAt, p?.UpdatedBy);
        }).ToList();
        return Ok(lista);
    }

    /// <summary>Admin: resetea (o crea) el PIN de un operador sin necesidad del PIN anterior.
    /// Para el caso "se olvidaron el PIN" o seteo inicial.</summary>
    [HttpPost("admin/reset")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> ResetPin([FromBody] ResetPinRequest req)
    {
        var nombre = (req.Nombre ?? "").ToUpperInvariant().Trim();
        if (!OperadoresValidos.Contains(nombre)) return BadRequest(new { error = "Operador desconocido" });
        if (string.IsNullOrEmpty(req.PinNuevo) || req.PinNuevo.Length != 4 || !req.PinNuevo.All(char.IsDigit))
            return BadRequest(new { error = "El PIN debe tener exactamente 4 dígitos" });

        var row = await _db.CafeOperadoresPin.FindAsync(nombre);
        if (row is null)
        {
            row = new CafeOperadorPin { Nombre = nombre };
            _db.CafeOperadoresPin.Add(row);
        }
        row.PinHash = BCrypt.Net.BCrypt.HashPassword(req.PinNuevo);
        row.UpdatedAt = DateTime.UtcNow;
        row.UpdatedBy = "admin";
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
