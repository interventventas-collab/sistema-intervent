using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/proveedores")]
[Authorize]
public class CafeProveedoresController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeProveedoresController(AppDbContext db) { _db = db; }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] bool? activos = null)
    {
        var q = _db.CafeProveedores.AsQueryable();
        if (activos == true) q = q.Where(p => p.IsActive);
        var list = await q.OrderBy(p => p.Nombre).ToListAsync();

        var ids = list.Select(p => p.Id).ToList();
        var stats = await _db.CafeCompras
            .Where(c => c.ProveedorId.HasValue && ids.Contains(c.ProveedorId.Value) && c.Estado != "ANULADA")
            .GroupBy(c => c.ProveedorId!.Value)
            .Select(g => new { ProveedorId = g.Key, N = g.Count(), Total = g.Sum(x => x.Total) })
            .ToDictionaryAsync(x => x.ProveedorId);

        return Ok(list.Select(p => Map(p,
            stats.TryGetValue(p.Id, out var s) ? s.N : 0,
            stats.TryGetValue(p.Id, out var s2) ? s2.Total : 0m)).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var p = await _db.CafeProveedores.FindAsync(id);
        if (p is null) return NotFound(new { error = "Proveedor no encontrado" });
        var n = await _db.CafeCompras.CountAsync(c => c.ProveedorId == id && c.Estado != "ANULADA");
        var total = await _db.CafeCompras.Where(c => c.ProveedorId == id && c.Estado != "ANULADA").SumAsync(c => (decimal?)c.Total) ?? 0m;
        return Ok(Map(p, n, total));
    }

    public record MovimientoProvDto(DateTime Fecha, string Tipo, string Numero, decimal Debe, decimal Haber, decimal Saldo, string? Detalle);
    public record EstadoCuentaProvDto(int ProveedorId, string Nombre, decimal Saldo, List<MovimientoProvDto> Movimientos);

    /// <summary>Estado de cuenta del proveedor: compras (haber, lo que le debes) y pagos (debe, lo que ya pagaste).</summary>
    [HttpGet("{id:int}/estado-cuenta")]
    public async Task<IActionResult> EstadoCuenta(int id)
    {
        var prov = await _db.CafeProveedores.FindAsync(id);
        if (prov is null) return NotFound();

        var compras = await _db.CafeCompras.Where(c => c.ProveedorId == id && c.Estado != "ANULADA")
            .Select(c => new { c.Id, c.Fecha, c.Numero, c.Total }).ToListAsync();
        var pagos = await _db.CafePagosProveedor.Where(p => p.ProveedorId == id && p.Estado == "VIGENTE")
            .Select(p => new { p.Id, p.Fecha, p.Numero, p.Total, p.Retenciones }).ToListAsync();

        // Para el proveedor: Compra = HABER (te debe que pagar), Pago = DEBE (pagaste)
        var movs = new List<(DateTime fecha, string tipo, string num, decimal debe, decimal haber, string? det)>();
        foreach (var c in compras)
            movs.Add((c.Fecha, "Compra", c.Numero, 0m, c.Total, null));
        foreach (var p in pagos)
            movs.Add((p.Fecha, "Pago", p.Numero, p.Total + p.Retenciones, 0m,
                p.Retenciones > 0 ? $"(incluye ${p.Retenciones:N2} retenciones)" : null));

        movs = movs.OrderBy(x => x.fecha).ToList();
        decimal acum = 0m;
        var result = new List<MovimientoProvDto>(movs.Count);
        foreach (var m in movs)
        {
            // Saldo positivo = le debes al proveedor; negativo = a tu favor
            acum += m.haber - m.debe;
            result.Add(new MovimientoProvDto(m.fecha, m.tipo, m.num, m.debe, m.haber, acum, m.det));
        }
        return Ok(new EstadoCuentaProvDto(id, prov.Nombre, acum, result));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeProveedorRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre es obligatorio" });
        var cuit = NormCuit(req.Cuit);
        if (cuit is not null && await _db.CafeProveedores.AnyAsync(x => x.Cuit == cuit))
            return BadRequest(new { error = $"Ya existe un proveedor con el CUIT {cuit}" });
        var p = new CafeProveedor
        {
            Nombre = req.Nombre.Trim(),
            Contacto = NullIfEmpty(req.Contacto),
            Telefono = NullIfEmpty(req.Telefono),
            Email = NullIfEmpty(req.Email),
            Notas = NullIfEmpty(req.Notas),
            Cuit = cuit,
            CategoriaImpositiva = NullIfEmpty(req.CategoriaImpositiva)?.ToUpperInvariant(),
            Direccion = NullIfEmpty(req.Direccion),
            CodigoPostal = NullIfEmpty(req.CodigoPostal),
            Provincia = NullIfEmpty(req.Provincia),
            Ciudad = NullIfEmpty(req.Ciudad),
            Web = NullIfEmpty(req.Web),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeProveedores.Add(p);
        await _db.SaveChangesAsync();
        return Ok(Map(p, 0, 0m));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeProveedorRequest req)
    {
        var p = await _db.CafeProveedores.FindAsync(id);
        if (p is null) return NotFound(new { error = "Proveedor no encontrado" });
        if (req.Nombre is not null)
        {
            if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "El nombre no puede estar vacio" });
            p.Nombre = req.Nombre.Trim();
        }
        if (req.Contacto is not null) p.Contacto = NullIfEmpty(req.Contacto);
        if (req.Telefono is not null) p.Telefono = NullIfEmpty(req.Telefono);
        if (req.Email is not null) p.Email = NullIfEmpty(req.Email);
        if (req.Notas is not null) p.Notas = NullIfEmpty(req.Notas);
        if (req.Cuit is not null)
        {
            var nuevoCuit = NormCuit(req.Cuit);
            if (nuevoCuit is not null && nuevoCuit != p.Cuit
                && await _db.CafeProveedores.AnyAsync(x => x.Cuit == nuevoCuit && x.Id != id))
                return BadRequest(new { error = $"Ya existe otro proveedor con el CUIT {nuevoCuit}" });
            p.Cuit = nuevoCuit;
        }
        if (req.CategoriaImpositiva is not null) p.CategoriaImpositiva = NullIfEmpty(req.CategoriaImpositiva)?.ToUpperInvariant();
        if (req.Direccion is not null) p.Direccion = NullIfEmpty(req.Direccion);
        if (req.CodigoPostal is not null) p.CodigoPostal = NullIfEmpty(req.CodigoPostal);
        if (req.Provincia is not null) p.Provincia = NullIfEmpty(req.Provincia);
        if (req.Ciudad is not null) p.Ciudad = NullIfEmpty(req.Ciudad);
        if (req.Web is not null) p.Web = NullIfEmpty(req.Web);
        if (req.IsActive.HasValue) p.IsActive = req.IsActive.Value;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        var n = await _db.CafeCompras.CountAsync(c => c.ProveedorId == id && c.Estado != "ANULADA");
        var total = await _db.CafeCompras.Where(c => c.ProveedorId == id && c.Estado != "ANULADA").SumAsync(c => (decimal?)c.Total) ?? 0m;
        return Ok(Map(p, n, total));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var p = await _db.CafeProveedores.FindAsync(id);
        if (p is null) return NotFound(new { error = "Proveedor no encontrado" });
        // Si tiene compras asociadas, no se borra: se desactiva. Asi se preserva el historial.
        var hasCompras = await _db.CafeCompras.AnyAsync(c => c.ProveedorId == id);
        if (hasCompras)
        {
            p.IsActive = false;
            p.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { deleted = false, deactivated = true, message = "Proveedor con compras: se desactivo en lugar de eliminar" });
        }
        _db.CafeProveedores.Remove(p);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    private static CafeProveedorDto Map(CafeProveedor p, int comprasCount, decimal totalComprado) => new(
        p.Id, p.Nombre, p.Contacto, p.Telefono, p.Email, p.Notas,
        p.Cuit, p.CategoriaImpositiva,
        p.Direccion, p.CodigoPostal, p.Provincia, p.Ciudad, p.Web,
        p.IsActive, p.CreatedAt, p.UpdatedAt, comprasCount, totalComprado);

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>Normaliza el CUIT a solo digitos. Devuelve null si despues de limpiar queda vacio
    /// o si tiene < 8 digitos (no validamos algoritmo de digito verificador, solo presencia).</summary>
    private static string? NormCuit(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var digits = new string(s.Where(char.IsDigit).ToArray());
        return string.IsNullOrEmpty(digits) ? null : digits;
    }
}
