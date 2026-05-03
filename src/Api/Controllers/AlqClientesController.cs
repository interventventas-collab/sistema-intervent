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

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var list = await _db.AlqClientes
            .OrderBy(c => c.Nombre)
            .Select(c => new AlqClienteDto(
                c.Id, c.Nombre, c.Empresa, c.Telefono, c.Email,
                c.DireccionDefault, c.Notas,
                c.IsActive, c.CreatedAt, c.UpdatedAt))
            .ToListAsync();
        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var c = await _db.AlqClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        return Ok(new AlqClienteDto(c.Id, c.Nombre, c.Empresa, c.Telefono, c.Email,
            c.DireccionDefault, c.Notas, c.IsActive, c.CreatedAt, c.UpdatedAt));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAlqClienteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });

        var c = new AlqCliente
        {
            Nombre = req.Nombre.Trim(),
            Empresa = string.IsNullOrWhiteSpace(req.Empresa) ? null : req.Empresa.Trim(),
            Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim(),
            Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim(),
            DireccionDefault = string.IsNullOrWhiteSpace(req.DireccionDefault) ? null : req.DireccionDefault.Trim(),
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.AlqClientes.Add(c);
        await _db.SaveChangesAsync();
        return Ok(new AlqClienteDto(c.Id, c.Nombre, c.Empresa, c.Telefono, c.Email,
            c.DireccionDefault, c.Notas, c.IsActive, c.CreatedAt, c.UpdatedAt));
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
        if (req.Empresa is not null) c.Empresa = string.IsNullOrWhiteSpace(req.Empresa) ? null : req.Empresa.Trim();
        if (req.Telefono is not null) c.Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim();
        if (req.Email is not null) c.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
        if (req.DireccionDefault is not null) c.DireccionDefault = string.IsNullOrWhiteSpace(req.DireccionDefault) ? null : req.DireccionDefault.Trim();
        if (req.Notas is not null) c.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        if (req.IsActive.HasValue) c.IsActive = req.IsActive.Value;
        c.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new AlqClienteDto(c.Id, c.Nombre, c.Empresa, c.Telefono, c.Email,
            c.DireccionDefault, c.Notas, c.IsActive, c.CreatedAt, c.UpdatedAt));
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
}
