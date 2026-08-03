using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-08-03: Remitentes alternativos para el rotulo de transporte. Por defecto el rotulo usa
/// los "Datos del negocio" de Cafe_Settings; esta lista es solo para despachar en nombre de otra
/// sociedad. Arranca vacia. CRUD basico para "Configuracion del sistema".
/// </summary>
[ApiController]
[Route("api/cafe/remitentes")]
[Authorize]
public class CafeRemitentesController : ControllerBase
{
    private readonly AppDbContext _db;

    public CafeRemitentesController(AppDbContext db) { _db = db; }

    public record RemitenteDto(int Id, string Nombre, string? Cuit, string? NombreFantasia,
        string? Direccion, string? Telefono, string? Localidad, string? Provincia, string? CodigoPostal, bool Activo);

    private static RemitenteDto ToDto(CafeRemitente r) => new(
        r.Id, r.Nombre, r.Cuit, r.NombreFantasia, r.Direccion, r.Telefono, r.Localidad, r.Provincia, r.CodigoPostal, r.Activo);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool incluirInactivos = false)
    {
        var q = _db.CafeRemitentes.AsQueryable();
        if (!incluirInactivos) q = q.Where(r => r.Activo);
        var items = await q.OrderBy(r => r.Nombre).ToListAsync();
        return Ok(items.Select(ToDto).ToList());
    }

    public record UpsertRemitenteRequest(string? Nombre, string? Cuit, string? NombreFantasia,
        string? Direccion, string? Telefono, string? Localidad, string? Provincia, string? CodigoPostal, bool? Activo);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertRemitenteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "La razón social del remitente es obligatoria" });
        var r = new CafeRemitente
        {
            Nombre = req.Nombre.Trim(),
            Cuit = Clean(req.Cuit),
            NombreFantasia = Clean(req.NombreFantasia),
            Direccion = Clean(req.Direccion),
            Telefono = Clean(req.Telefono),
            Localidad = Clean(req.Localidad),
            Provincia = Clean(req.Provincia),
            CodigoPostal = Clean(req.CodigoPostal),
            Activo = req.Activo ?? true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeRemitentes.Add(r);
        await _db.SaveChangesAsync();
        return Ok(ToDto(r));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpsertRemitenteRequest req)
    {
        var r = await _db.CafeRemitentes.FindAsync(id);
        if (r is null) return NotFound();
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "La razón social no puede quedar vacía" });
            r.Nombre = req.Nombre.Trim();
        }
        if (req.Cuit is not null) r.Cuit = Clean(req.Cuit);
        if (req.NombreFantasia is not null) r.NombreFantasia = Clean(req.NombreFantasia);
        if (req.Direccion is not null) r.Direccion = Clean(req.Direccion);
        if (req.Telefono is not null) r.Telefono = Clean(req.Telefono);
        if (req.Localidad is not null) r.Localidad = Clean(req.Localidad);
        if (req.Provincia is not null) r.Provincia = Clean(req.Provincia);
        if (req.CodigoPostal is not null) r.CodigoPostal = Clean(req.CodigoPostal);
        if (req.Activo.HasValue) r.Activo = req.Activo.Value;
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(r));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var r = await _db.CafeRemitentes.FindAsync(id);
        if (r is null) return NotFound();
        if (await _db.CafeVentas.AnyAsync(v => v.RemitenteId == id))
            return BadRequest(new { error = "Este remitente ya se usó en ventas. Desactivalo en vez de borrarlo." });
        _db.CafeRemitentes.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
