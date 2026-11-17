using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 17/11/2026 — Pagos a proveedores PROGRAMADOS (ver <see cref="CafePagoProgramado"/>).
/// Pedido del dueño: "elegir un proveedor y poder cargar el pago pendiente con fecha", como el
/// "Programar pago" del equipo. No mueve plata: el pago real se hace con el Nuevo pago de siempre.
/// </summary>
[ApiController]
[Route("api/cafe/pagos-programados")]
[Authorize]
public class CafePagosProgramadosController : ControllerBase
{
    private static readonly string[] Medios = { "TRANSFERENCIA", "ECHEQ", "EFECTIVO" };
    private readonly AppDbContext _db;

    public CafePagosProgramadosController(AppDbContext db) { _db = db; }

    public record ProgramadoDto(int Id, int ProveedorId, string ProveedorNombre, DateTime Fecha, decimal Importe,
        string Medio, List<string> Claves, string? Nota, int Parte, int Partes, string Estado, int? PagoId, string? Operador);
    public record ParteReq(DateTime Fecha, decimal Importe);
    public record CrearReq(int ProveedorId, string? Medio, List<string>? Claves, string? Nota, List<ParteReq> Partes);
    public record EditarReq(DateTime Fecha, decimal Importe, string? Medio, string? Nota);
    public record PagadoReq(int PagoId);

    /// <summary>Por defecto: todos los PENDIENTES (atrasados incluidos). Con desde/hasta: los de ese rango.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? estado = "PENDIENTE", [FromQuery] int? proveedorId = null)
    {
        var q = _db.CafePagosProgramados.AsNoTracking().Include(p => p.Proveedor).AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado) && estado != "TODOS") q = q.Where(p => p.Estado == estado);
        if (desde.HasValue) q = q.Where(p => p.Fecha >= desde.Value.Date);
        if (hasta.HasValue) q = q.Where(p => p.Fecha <= hasta.Value.Date);
        if (proveedorId.HasValue) q = q.Where(p => p.ProveedorId == proveedorId.Value);
        var lista = await q.OrderBy(p => p.Fecha).ThenBy(p => p.Id).ToListAsync();
        return Ok(lista.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var p = await _db.CafePagosProgramados.AsNoTracking().Include(x => x.Proveedor).FirstOrDefaultAsync(x => x.Id == id);
        return p is null ? NotFound() : Ok(Map(p));
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearReq req)
    {
        if (!await _db.CafeProveedores.AnyAsync(p => p.Id == req.ProveedorId)) return BadRequest(new { error = "Elegí un proveedor" });
        var partes = (req.Partes ?? new()).Where(p => p.Importe > 0).ToList();
        if (partes.Count == 0) return BadRequest(new { error = "Poné el importe" });
        if (partes.Count > 24) return BadRequest(new { error = "Máximo 24 partes" });
        var medio = NormMedio(req.Medio);
        var claves = string.Join("|", (req.Claves ?? new()).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct());
        var nota = Recortar(req.Nota, 500);
        var op = QuienCarga();
        var n = 0;
        foreach (var pt in partes.OrderBy(p => p.Fecha))
        {
            n++;
            _db.CafePagosProgramados.Add(new CafePagoProgramado
            {
                ProveedorId = req.ProveedorId, Fecha = pt.Fecha.Date, Importe = Math.Round(pt.Importe, 2), Medio = medio,
                Claves = string.IsNullOrEmpty(claves) ? null : claves, Nota = nota,
                Parte = n, Partes = partes.Count, Operador = op,
            });
        }
        await _db.SaveChangesAsync();
        return Ok(new { cantidad = n });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] EditarReq req)
    {
        var p = await _db.CafePagosProgramados.FindAsync(id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = "Ya no está pendiente" });
        if (req.Importe <= 0) return BadRequest(new { error = "Poné el importe" });
        p.Fecha = req.Fecha.Date;
        p.Importe = Math.Round(req.Importe, 2);
        p.Medio = NormMedio(req.Medio);
        p.Nota = Recortar(req.Nota, 500);
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Sacarlo de la lista (no se pagó así). Queda ANULADO, no se borra.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Anular(int id)
    {
        var p = await _db.CafePagosProgramados.FindAsync(id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = "Ya no está pendiente" });
        p.Estado = "ANULADO";
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Se guardó el pago de verdad (Nuevo pago abierto desde "Pagar"): queda PAGADO.</summary>
    [HttpPost("{id:int}/pagado")]
    public async Task<IActionResult> Pagado(int id, [FromBody] PagadoReq req)
    {
        var p = await _db.CafePagosProgramados.FindAsync(id);
        if (p is null) return NotFound();
        if (!await _db.CafePagosProveedor.AnyAsync(x => x.Id == req.PagoId && x.ProveedorId == p.ProveedorId))
            return BadRequest(new { error = "Ese pago no es de este proveedor" });
        p.Estado = "PAGADO";
        p.PagoId = req.PagoId;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private static ProgramadoDto Map(CafePagoProgramado p) => new(p.Id, p.ProveedorId, p.Proveedor?.Nombre ?? "?", p.Fecha.Date,
        p.Importe, p.Medio, (p.Claves ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList(), p.Nota,
        p.Parte, p.Partes, p.Estado, p.PagoId, p.Operador);

    private static string NormMedio(string? m)
    {
        var u = (m ?? "").Trim().ToUpperInvariant();
        return Medios.Contains(u) ? u : "TRANSFERENCIA";
    }

    private static string? Recortar(string? s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        return s.Length > max ? s[..max] : s;
    }

    private string? QuienCarga()
    {
        var op = Request?.Headers["X-Operator-Name"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(op) ? User?.Identity?.Name : op.Trim();
    }
}
