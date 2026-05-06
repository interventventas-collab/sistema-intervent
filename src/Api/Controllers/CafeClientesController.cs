using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/clientes")]
[Authorize]
public class CafeClientesController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly string[] TiposValidos = { "BAR", "OTRO" };

    public CafeClientesController(AppDbContext db) { _db = db; }

    private static CafeClienteDto Map(CafeCliente c) => new(
        c.Id, c.Codigo, c.Nombre, c.RazonSocial, c.Tipo,
        c.Cuit, c.Telefono, c.Email,
        c.Direccion, c.Localidad, c.Ciudad, c.Cp,
        c.CondicionIvaDefault,
        c.DomicilioEntrega,
        c.Notas, c.ComentariosComprobante,
        c.IsActive, c.CreatedAt, c.UpdatedAt);

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var list = await _db.CafeClientes.OrderBy(c => c.Nombre).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        return Ok(Map(c));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeClienteRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "El nombre es obligatorio" });
        var tipo = NormTipo(req.Tipo);
        var c = new CafeCliente
        {
            Codigo = await GenerarCodigoAsync(),
            Nombre = req.Nombre.Trim(),
            RazonSocial = Norm(req.RazonSocial),
            Tipo = tipo,
            Cuit = Norm(req.Cuit),
            Telefono = Norm(req.Telefono),
            Email = Norm(req.Email),
            Direccion = Norm(req.Direccion),
            Localidad = Norm(req.Localidad),
            Ciudad = Norm(req.Ciudad),
            Cp = Norm(req.Cp),
            CondicionIvaDefault = Norm(req.CondicionIvaDefault),
            DomicilioEntrega = Norm(req.DomicilioEntrega),
            Notas = Norm(req.Notas),
            ComentariosComprobante = Norm(req.ComentariosComprobante),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeClientes.Add(c);
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    /// <summary>
    /// Devuelve el siguiente codigo secuencial. Pad a 4 digitos para los primeros 9999.
    /// Si ya existe alguno >= 9999 (improbable pero por las dudas), arranca con 5 digitos.
    /// </summary>
    private async Task<string> GenerarCodigoAsync()
    {
        var maxNum = await _db.CafeClientes
            .Where(c => c.Codigo != null)
            .Select(c => c.Codigo!)
            .ToListAsync();
        int max = 0;
        foreach (var s in maxNum)
        {
            if (int.TryParse(s, out var n) && n > max) max = n;
        }
        var siguiente = max + 1;
        return siguiente < 10000 ? siguiente.ToString("D4") : siguiente.ToString();
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeClienteRequest req)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede ser vacio" });
            c.Nombre = req.Nombre.Trim();
        }
        if (req.RazonSocial is not null) c.RazonSocial = Norm(req.RazonSocial);
        if (req.Tipo is not null) c.Tipo = NormTipo(req.Tipo);
        if (req.Cuit is not null) c.Cuit = Norm(req.Cuit);
        if (req.Telefono is not null) c.Telefono = Norm(req.Telefono);
        if (req.Email is not null) c.Email = Norm(req.Email);
        if (req.Direccion is not null) c.Direccion = Norm(req.Direccion);
        if (req.Localidad is not null) c.Localidad = Norm(req.Localidad);
        if (req.Ciudad is not null) c.Ciudad = Norm(req.Ciudad);
        if (req.Cp is not null) c.Cp = Norm(req.Cp);
        if (req.CondicionIvaDefault is not null) c.CondicionIvaDefault = Norm(req.CondicionIvaDefault);
        if (req.DomicilioEntrega is not null) c.DomicilioEntrega = Norm(req.DomicilioEntrega);
        if (req.Notas is not null) c.Notas = Norm(req.Notas);
        if (req.ComentariosComprobante is not null) c.ComentariosComprobante = Norm(req.ComentariosComprobante);
        if (req.IsActive.HasValue) c.IsActive = req.IsActive.Value;
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.CafeClientes.FindAsync(id);
        if (c is null) return NotFound(new { error = "Cliente no encontrado" });
        _db.CafeClientes.Remove(c);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private static string NormTipo(string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return "OTRO";
        var v = t.Trim().ToUpperInvariant();
        return TiposValidos.Contains(v) ? v : "OTRO";
    }

    private static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
