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

    /// <summary>19/11/2026 — Frecuentes / Todos / Sin movimiento. Por proveedor: facturas de ARCA y pagos de los
    /// últimos 90 días y del año, la categoría que se le puso en Facturas recibidas (📦 Mercadería o 🧾 gastos)
    /// y el ⭐. Frecuente = ⭐, cuenta corriente, o 2+ facturas o pagos en 3 meses.</summary>
    public record UsoDto(int Id, bool Favorito, bool Frecuente, bool SinMovimiento, int Facturas90, int Pagos90,
        DateTime? Ultima, string? Categoria, string? Tipo);

    [HttpGet("uso")]
    public async Task<List<UsoDto>> Uso()
    {
        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        var d90 = hoy.AddDays(-90); var d365 = hoy.AddDays(-365);
        var provs = await _db.CafeProveedores.AsNoTracking()
            .Select(p => new { p.Id, p.Cuit, p.Favorito, p.CuentaCorriente }).ToListAsync();
        var facts = await _db.ContadoraComprobantes.AsNoTracking()
            .Where(c => c.Naturaleza == "COMPRA" && c.ReceptorDoc != null && c.FechaEmision >= d365 && !c.EsNotaCredito)
            .GroupBy(c => c.ReceptorDoc!)
            .Select(g => new { Cuit = g.Key, N90 = g.Count(x => x.FechaEmision >= d90), N = g.Count(), Ultima = g.Max(x => x.FechaEmision) })
            .ToDictionaryAsync(x => x.Cuit);
        var pagos = await _db.CafePagosProveedor.AsNoTracking()
            .Where(p => p.Estado == "VIGENTE" && p.Fecha >= d365)
            .GroupBy(p => p.ProveedorId)
            .Select(g => new { Id = g.Key, N90 = g.Count(x => x.Fecha >= d90), N = g.Count(), Ultima = g.Max(x => x.Fecha) })
            .ToDictionaryAsync(x => x.Id);
        var compras = (await _db.CafeCompras.AsNoTracking()
            .Where(c => c.ProveedorId != null && c.Estado != "ANULADA" && c.Fecha >= d365)
            .Select(c => c.ProveedorId!.Value).Distinct().ToListAsync()).ToHashSet();
        var cats = await _db.ComprasProveedorConfigs.AsNoTracking().Where(c => c.Categoria != null)
            .ToDictionaryAsync(c => c.Cuit, c => c.Categoria);

        return provs.Select(p =>
        {
            var f = p.Cuit != null && facts.TryGetValue(p.Cuit, out var ff) ? ff : null;
            var pg = pagos.TryGetValue(p.Id, out var pp) ? pp : null;
            var f90 = f?.N90 ?? 0; var p90 = pg?.N90 ?? 0;
            var frec = p.Favorito || p.CuentaCorriente || f90 >= 2 || p90 >= 2;
            var sinMov = !frec && (f?.N ?? 0) == 0 && (pg?.N ?? 0) == 0 && !compras.Contains(p.Id);
            DateTime? ult = new[] { f?.Ultima, pg?.Ultima }.Where(x => x.HasValue).Max();
            var cat = p.Cuit != null && cats.TryGetValue(p.Cuit, out var cc) ? cc : null;
            var tipo = cat is null ? null : cat.StartsWith("Mercader") ? "MERCADERIA" : "GASTOS";
            return new UsoDto(p.Id, p.Favorito, frec, sinMov, f90, p90, ult, cat, tipo);
        }).ToList();
    }

    public record FavoritoReq(bool Favorito);
    [HttpPut("{id:int}/favorito")]
    public async Task<IActionResult> Favorito(int id, [FromBody] FavoritoReq req)
    {
        var p = await _db.CafeProveedores.FindAsync(id);
        if (p is null) return NotFound(new { error = "Proveedor no encontrado" });
        p.Favorito = req.Favorito; p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
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
        // 09/09/2026: el tilde que lo habilita a recibir cobros redirigidos.
        if (req.AceptaRedirigido.HasValue) p.AceptaRedirigido = req.AceptaRedirigido.Value;
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
        p.IsActive, p.CreatedAt, p.UpdatedAt, comprasCount, totalComprado, p.AceptaRedirigido,
        p.CuentaCorriente, p.CuentaCorrienteDesde);

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
