using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-08-03: Catalogo de empresas de transporte. CRUD basico para "Configuracion del sistema".
/// Se usa para elegir el transporte al marcar TRANSPORTE en la venta y armar el rotulo/etiqueta.
/// </summary>
[ApiController]
[Route("api/cafe/transportes")]
[Authorize]
public class CafeTransportesController : ControllerBase
{
    private readonly AppDbContext _db;

    public CafeTransportesController(AppDbContext db) { _db = db; }

    public record TransporteDto(int Id, string Nombre, string? Direccion, string? Telefono,
        string? Localidad, string? Provincia, string? CodigoPostal, bool PagoDestino, bool Activo);

    private static TransporteDto ToDto(CafeTransporte t) => new(
        t.Id, t.Nombre, t.Direccion, t.Telefono, t.Localidad, t.Provincia, t.CodigoPostal, t.PagoDestino, t.Activo);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool incluirInactivos = false)
    {
        var q = _db.CafeTransportes.AsQueryable();
        if (!incluirInactivos) q = q.Where(t => t.Activo);
        var items = await q.OrderBy(t => t.Nombre).ToListAsync();
        return Ok(items.Select(ToDto).ToList());
    }

    public record UpsertTransporteRequest(string? Nombre, string? Direccion, string? Telefono,
        string? Localidad, string? Provincia, string? CodigoPostal, bool? PagoDestino, bool? Activo);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertTransporteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre del transporte es obligatorio" });
        var nombre = req.Nombre.Trim();
        if (await _db.CafeTransportes.AnyAsync(t => t.Nombre == nombre))
            return Conflict(new { error = $"Ya existe un transporte con ese nombre: {nombre}" });
        var t = new CafeTransporte
        {
            Nombre = nombre,
            Direccion = Clean(req.Direccion),
            Telefono = Clean(req.Telefono),
            Localidad = Clean(req.Localidad),
            Provincia = Clean(req.Provincia),
            CodigoPostal = Clean(req.CodigoPostal),
            PagoDestino = req.PagoDestino ?? true,
            Activo = req.Activo ?? true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeTransportes.Add(t);
        await _db.SaveChangesAsync();
        return Ok(ToDto(t));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpsertTransporteRequest req)
    {
        var t = await _db.CafeTransportes.FindAsync(id);
        if (t is null) return NotFound();
        if (req.Nombre is not null)
        {
            var nombre = req.Nombre.Trim();
            if (string.IsNullOrWhiteSpace(nombre)) return BadRequest(new { error = "El nombre no puede quedar vacío" });
            if (await _db.CafeTransportes.AnyAsync(x => x.Id != id && x.Nombre == nombre))
                return Conflict(new { error = $"Ya existe otro transporte con ese nombre: {nombre}" });
            t.Nombre = nombre;
        }
        if (req.Direccion is not null) t.Direccion = Clean(req.Direccion);
        if (req.Telefono is not null) t.Telefono = Clean(req.Telefono);
        if (req.Localidad is not null) t.Localidad = Clean(req.Localidad);
        if (req.Provincia is not null) t.Provincia = Clean(req.Provincia);
        if (req.CodigoPostal is not null) t.CodigoPostal = Clean(req.CodigoPostal);
        if (req.PagoDestino.HasValue) t.PagoDestino = req.PagoDestino.Value;
        if (req.Activo.HasValue) t.Activo = req.Activo.Value;
        t.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(ToDto(t));
    }

    /// <summary>Borra solo si ninguna venta lo usa; sino sugerir desactivar (para no perder el historial).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var t = await _db.CafeTransportes.FindAsync(id);
        if (t is null) return NotFound();
        if (await _db.CafeVentas.AnyAsync(v => v.TransporteId == id))
            return BadRequest(new { error = "Este transporte ya se usó en ventas. Desactivalo en vez de borrarlo." });
        _db.CafeTransportes.Remove(t);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
