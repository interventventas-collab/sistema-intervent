using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Pagos a proveedores (Café → Tesorería → Pagos).
/// Espejo de Cobranzas: elegis proveedor, ves sus compras pendientes, definis cuanto pagas de cada una,
/// formas de pago combinadas (incluye endoso de cheques de cartera).
/// </summary>
[ApiController]
[Route("api/cafe/pagos-proveedor")]
[Authorize]
public class CafePagosProveedorController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;
    private readonly ProveedorCtaCteService _ctacte;

    public CafePagosProveedorController(AppDbContext db, AuditLogService audit, ProveedorCtaCteService ctacte)
    { _db = db; _audit = audit; _ctacte = ctacte; }

    /// <summary>17/09/2026: lo que se le puede pagar a un proveedor. Ya no son las Cafe_Compras
    /// (que mueven stock): son sus facturas de AFIP y sus cotizaciones, de la cuenta corriente.</summary>
    public record DocPendienteDto(string Clave, string Tipo, string Numero, bool Oficial, DateTime Fecha,
        decimal Total, decimal Pagado, decimal Saldo, bool TieneArchivo);
    public record PendientesProveedorDto(bool CuentaCorriente, DateTime? Desde, decimal Oficial, decimal NoOficial,
        decimal ACuenta, decimal Total, List<DocPendienteDto> Documentos);
    public record PagoListDto(int Id, string Numero, DateTime Fecha, int ProveedorId, string ProveedorNombre, decimal Total, decimal Retenciones, string Estado);
    public record PagoComprobanteDto(int Id, int? CompraId, string? CompraNumero, decimal Importe);
    public record PagoMedioDto(int Id, int CajaId, string CajaNombre, decimal Importe, string? Referencia, int? ChequeId);
    public record PagoDetalleDto(int Id, string Numero, DateTime Fecha, int ProveedorId, string ProveedorNombre, decimal Total, decimal Retenciones, string Estado, string? Operador, string? Observaciones, List<PagoComprobanteDto> Comprobantes, List<PagoMedioDto> Medios);

    public record CrearPagoRequest(
        int ProveedorId,
        decimal Retenciones,
        string? Operador,
        string? Observaciones,
        List<CrearComprobanteItem> Comprobantes,
        List<CrearMedioItem> Medios);
    /// <summary>Clave = "AFIP:..." o "DEU:..." (ver ProveedorCtaCteService). Sin clave ni compra = a cuenta.</summary>
    public record CrearComprobanteItem(int? CompraId, decimal Importe, string? Clave = null);
    /// <summary>Si el medio se hace con un cheque de cartera (endoso), pasar ChequeExistenteId. Si no, lo dejas null y va por caja normal.</summary>
    public record CrearMedioItem(int CajaId, decimal Importe, string? Referencia, int? ChequeExistenteId);

    [HttpGet("pendientes/{proveedorId:int}")]
    public async Task<IActionResult> Pendientes(int proveedorId)
    {
        var c = await _ctacte.GetCuentaAsync(proveedorId);
        if (c is null) return NotFound();
        return Ok(new PendientesProveedorDto(c.CuentaCorriente, c.Desde, c.Oficial, c.NoOficial, c.ACuenta, c.Total,
            c.Pendientes.Select(d => new DocPendienteDto(d.Clave, d.Tipo, d.Numero, d.Oficial, d.Fecha, d.Total, d.Pagado, d.Saldo, d.TieneArchivo)).ToList()));
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? proveedorId,
        [FromQuery] DateTime? desde,
        [FromQuery] DateTime? hasta,
        [FromQuery] int take = 200)
    {
        var q = _db.CafePagosProveedor.Include(p => p.Proveedor).AsQueryable();
        if (proveedorId.HasValue) q = q.Where(p => p.ProveedorId == proveedorId.Value);
        if (desde.HasValue) q = q.Where(p => p.Fecha >= desde.Value);
        if (hasta.HasValue) q = q.Where(p => p.Fecha <= hasta.Value);
        var list = await q.OrderByDescending(p => p.Fecha).Take(take)
            .Select(p => new PagoListDto(p.Id, p.Numero, p.Fecha, p.ProveedorId,
                p.Proveedor != null ? p.Proveedor.Nombre : "—",
                p.Total, p.Retenciones, p.Estado))
            .ToListAsync();
        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var p = await _db.CafePagosProveedor
            .Include(x => x.Proveedor)
            .Include(x => x.Comprobantes).ThenInclude(c => c.Compra)
            .Include(x => x.Medios).ThenInclude(m => m.Caja)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        return Ok(new PagoDetalleDto(
            p.Id, p.Numero, p.Fecha, p.ProveedorId,
            p.Proveedor?.Nombre ?? "—",
            p.Total, p.Retenciones, p.Estado, p.Operador, p.Observaciones,
            p.Comprobantes.Select(x => new PagoComprobanteDto(x.Id, x.CompraId, x.Compra?.Numero, x.Importe)).ToList(),
            p.Medios.Select(x => new PagoMedioDto(x.Id, x.CajaId, x.Caja?.Nombre ?? "—", x.Importe, x.Referencia, x.ChequeId)).ToList()));
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearPagoRequest req)
    {
        var (error, id, numero) = await _ctacte.CrearPagoAsync(new ProveedorCtaCteService.NuevoPago(
            req.ProveedorId, req.Retenciones, req.Operador, req.Observaciones,
            (req.Comprobantes ?? new()).Select(c => new ProveedorCtaCteService.ItemPago(c.Clave, c.CompraId, c.Importe)).ToList(),
            (req.Medios ?? new()).Select(m => new ProveedorCtaCteService.MedioPago(m.CajaId, m.Importe, m.Referencia, m.ChequeExistenteId)).ToList()));
        if (error is not null) return BadRequest(new { error });
        return Ok(new { id, numero });
    }

    [HttpPost("{id:int}/anular")]
    public async Task<IActionResult> Anular(int id)
    {
        var p = await _db.CafePagosProveedor.Include(x => x.Medios).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado == "ANULADA") return BadRequest(new { error = "Ya esta anulada" });
        p.Estado = "ANULADA";
        p.UpdatedAt = DateTime.UtcNow;
        // Revertir endosos: cheques endosados vuelven a cartera
        foreach (var m in p.Medios.Where(m => m.ChequeId.HasValue))
        {
            var ch = await _db.CafeCheques.FindAsync(m.ChequeId!.Value);
            if (ch is not null && ch.Estado == "ENDOSADO")
            {
                ch.Estado = "EN_CARTERA";
                ch.FechaCambioEstado = DateTime.UtcNow;
                ch.ProveedorEndosoId = null;
                ch.PagoOrigenId = null;
            }
        }
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafePagoProveedor", id.ToString(), "ANULAR", $"Pago {p.Numero} anulado");
        return Ok(new { ok = true });
    }
}
