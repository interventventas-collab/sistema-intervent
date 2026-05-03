using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/alquileres/clientes")]
[Authorize]
public class AlqClientesController : ControllerBase
{
    private readonly AppDbContext _db;

    public AlqClientesController(AppDbContext db) { _db = db; }

    private static AlqClienteDto Map(AlqCliente c) => new(
        c.Id, c.Nombre, c.Empresa, c.DniCuit,
        c.Telefono, c.Telefono2, c.Email,
        c.DireccionDefault, c.Piso, c.Depto, c.Barrio, c.EntreCalles,
        c.Notas, c.IsActive, c.CreatedAt, c.UpdatedAt);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var list = await _db.AlqClientes.OrderBy(c => c.Nombre).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var c = await _db.AlqClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        return Ok(Map(c));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAlqClienteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });

        var c = new AlqCliente
        {
            Nombre = req.Nombre.Trim(),
            Empresa = Norm(req.Empresa),
            DniCuit = Norm(req.DniCuit),
            Telefono = Norm(req.Telefono),
            Telefono2 = Norm(req.Telefono2),
            Email = Norm(req.Email),
            DireccionDefault = Norm(req.DireccionDefault),
            Piso = Norm(req.Piso),
            Depto = Norm(req.Depto),
            Barrio = Norm(req.Barrio),
            EntreCalles = Norm(req.EntreCalles),
            Notas = Norm(req.Notas),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.AlqClientes.Add(c);
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateAlqClienteRequest req)
    {
        var c = await _db.AlqClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });

        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede ser vacio" });
            c.Nombre = req.Nombre.Trim();
        }
        if (req.Empresa is not null) c.Empresa = Norm(req.Empresa);
        if (req.DniCuit is not null) c.DniCuit = Norm(req.DniCuit);
        if (req.Telefono is not null) c.Telefono = Norm(req.Telefono);
        if (req.Telefono2 is not null) c.Telefono2 = Norm(req.Telefono2);
        if (req.Email is not null) c.Email = Norm(req.Email);
        if (req.DireccionDefault is not null) c.DireccionDefault = Norm(req.DireccionDefault);
        if (req.Piso is not null) c.Piso = Norm(req.Piso);
        if (req.Depto is not null) c.Depto = Norm(req.Depto);
        if (req.Barrio is not null) c.Barrio = Norm(req.Barrio);
        if (req.EntreCalles is not null) c.EntreCalles = Norm(req.EntreCalles);
        if (req.Notas is not null) c.Notas = Norm(req.Notas);
        if (req.IsActive.HasValue) c.IsActive = req.IsActive.Value;
        c.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.AlqClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        _db.AlqClientes.Remove(c);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
