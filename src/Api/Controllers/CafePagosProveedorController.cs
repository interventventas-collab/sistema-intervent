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

    public CafePagosProveedorController(AppDbContext db, AuditLogService audit) { _db = db; _audit = audit; }

    public record CompraPendienteDto(int CompraId, string Numero, DateTime Fecha, decimal Total, decimal Pagado, decimal Saldo, string? NumeroComprobanteProveedor);
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
    public record CrearComprobanteItem(int? CompraId, decimal Importe);
    /// <summary>Si el medio se hace con un cheque de cartera (endoso), pasar ChequeExistenteId. Si no, lo dejas null y va por caja normal.</summary>
    public record CrearMedioItem(int CajaId, decimal Importe, string? Referencia, int? ChequeExistenteId);

    [HttpGet("comprobantes-pendientes/{proveedorId:int}")]
    public async Task<IActionResult> ComprobantesPendientes(int proveedorId)
    {
        var compras = await _db.CafeCompras
            .Where(c => c.ProveedorId == proveedorId && c.Estado != "ANULADA")
            .Select(c => new { c.Id, c.Numero, c.Fecha, c.Total, c.NumeroComprobante })
            .ToListAsync();
        if (compras.Count == 0) return Ok(new List<CompraPendienteDto>());

        var compraIds = compras.Select(c => c.Id).ToList();
        var pagado = await _db.CafePagosProveedorComprobantes
            .Where(c => c.CompraId != null && compraIds.Contains(c.CompraId!.Value)
                && c.Pago!.Estado == "VIGENTE")
            .GroupBy(c => c.CompraId!.Value)
            .Select(g => new { CompraId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        var dict = pagado.ToDictionary(p => p.CompraId, p => p.Total);

        var result = compras
            .Select(c => new CompraPendienteDto(
                c.Id, c.Numero, c.Fecha, c.Total,
                dict.TryGetValue(c.Id, out var p) ? p : 0m,
                c.Total - (dict.TryGetValue(c.Id, out var p2) ? p2 : 0m),
                c.NumeroComprobante))
            .Where(x => x.Saldo > 0.01m)
            .OrderBy(x => x.Fecha)
            .ToList();
        return Ok(result);
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
        var proveedor = await _db.CafeProveedores.FindAsync(req.ProveedorId);
        if (proveedor is null) return BadRequest(new { error = "Proveedor no encontrado" });
        if (req.Comprobantes == null || req.Comprobantes.Count == 0)
            return BadRequest(new { error = "Cargar al menos un comprobante (o 'a cuenta')" });
        if (req.Medios == null || req.Medios.Count == 0)
            return BadRequest(new { error = "Cargar al menos una forma de pago" });

        var sumComp = req.Comprobantes.Sum(c => c.Importe);
        var sumMed = req.Medios.Sum(m => m.Importe);
        var reten = Math.Max(0m, req.Retenciones);
        if (Math.Abs(sumComp - (sumMed + reten)) > 0.01m)
            return BadRequest(new { error = $"No cuadra: imputado ${sumComp:N2} vs medios+retenciones ${(sumMed+reten):N2}" });

        // Validar cheques endosados
        foreach (var med in req.Medios.Where(m => m.ChequeExistenteId.HasValue))
        {
            var ch = await _db.CafeCheques.FindAsync(med.ChequeExistenteId!.Value);
            if (ch is null) return BadRequest(new { error = $"Cheque {med.ChequeExistenteId} no encontrado" });
            if (ch.Estado != "EN_CARTERA") return BadRequest(new { error = $"Cheque {ch.Numero} no esta en cartera (estado: {ch.Estado})" });
            if (Math.Abs(ch.Importe - med.Importe) > 0.01m) return BadRequest(new { error = $"El importe del medio ({med.Importe:N2}) no coincide con el del cheque {ch.Numero} ({ch.Importe:N2})" });
        }

        // Generar numero correlativo
        var ultimos = await _db.CafePagosProveedor.Select(x => x.Numero).ToListAsync();
        int maxSec = 0;
        foreach (var n in ultimos)
        {
            var parts = (n ?? "").Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var k) && k > maxSec) maxSec = k;
        }
        var numero = $"OP-{(maxSec + 1):D8}";

        var pago = new CafePagoProveedor
        {
            Numero = numero,
            Fecha = DateTime.UtcNow,
            ProveedorId = req.ProveedorId,
            Total = sumMed,
            Retenciones = reten,
            Operador = req.Operador,
            Observaciones = req.Observaciones,
            Estado = "VIGENTE"
        };
        _db.CafePagosProveedor.Add(pago);
        await _db.SaveChangesAsync();

        foreach (var c in req.Comprobantes)
            _db.CafePagosProveedorComprobantes.Add(new CafePagoProveedorComprobante
            {
                PagoId = pago.Id,
                CompraId = c.CompraId,
                Importe = c.Importe
            });

        foreach (var m in req.Medios)
        {
            // Si endosa un cheque existente, marcarlo como ENDOSADO
            if (m.ChequeExistenteId.HasValue)
            {
                var ch = await _db.CafeCheques.FindAsync(m.ChequeExistenteId.Value);
                if (ch is not null)
                {
                    ch.Estado = "ENDOSADO";
                    ch.FechaCambioEstado = DateTime.UtcNow;
                    ch.ProveedorEndosoId = req.ProveedorId;
                    ch.PagoOrigenId = pago.Id;
                }
            }
            _db.CafePagosProveedorMedios.Add(new CafePagoProveedorMedio
            {
                PagoId = pago.Id,
                CajaId = m.CajaId,
                Importe = m.Importe,
                Referencia = m.Referencia,
                ChequeId = m.ChequeExistenteId
            });
        }
        await _db.SaveChangesAsync();

        await _audit.LogAsync("CafePagoProveedor", pago.Id.ToString(), "CREATE",
            $"Pago {numero} a {proveedor.Nombre}, total ${sumMed:N2}");

        return Ok(new { id = pago.Id, numero });
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
