using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Maquinas de cafe colocadas en clientes:
///   - COMODATO: tuya, no te pagan
///   - FINANCIADA: la compraron en cuotas
/// Aparte de /api/cafe/ventas — no se mezcla con la cuenta corriente de cafe.
/// </summary>
[ApiController]
[Route("api/cafe/comodatos")]
[Authorize]
public class CafeComodatosController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;
    public CafeComodatosController(AppDbContext db, AuditLogService audit) { _db = db; _audit = audit; }

    public record ComodatoDto(int Id, int ClienteId, string? ClienteNombre, string Modalidad,
        string? Marca, string? Modelo, string? NumeroSerie, DateTime? FechaEntrega,
        string Estado, DateTime? FechaDevolucion, string? Notas, decimal? ValorEstimado,
        decimal? PrecioVenta, int? CuotasTotales, decimal? ValorCuota, int? DiaPagoMensual,
        decimal? SaldoFinanciamiento, decimal PagosAcumulados, int PagosCount,
        DateTime CreatedAt);

    private static ComodatoDto Map(CafeComodato c) => new(
        c.Id, c.ClienteId, c.Cliente?.Nombre, c.Modalidad,
        c.Marca, c.Modelo, c.NumeroSerie, c.FechaEntrega,
        c.Estado, c.FechaDevolucion, c.Notas, c.ValorEstimado,
        c.PrecioVenta, c.CuotasTotales, c.ValorCuota, c.DiaPagoMensual,
        c.SaldoFinanciamiento,
        c.Pagos?.Sum(p => p.Importe) ?? 0m,
        c.Pagos?.Count ?? 0,
        c.CreatedAt);

    public record PagoDto(int Id, int ComodatoId, DateTime Fecha, decimal Importe, string? MedioPago, string? Notas, DateTime CreatedAt);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? modalidad = null, [FromQuery] string? estado = null,
        [FromQuery] int? clienteId = null, [FromQuery] string? q = null)
    {
        var qry = _db.CafeComodatos.Include(c => c.Cliente).Include(c => c.Pagos).AsQueryable();
        if (!string.IsNullOrWhiteSpace(modalidad)) qry = qry.Where(c => c.Modalidad == modalidad);
        if (!string.IsNullOrWhiteSpace(estado)) qry = qry.Where(c => c.Estado == estado);
        if (clienteId.HasValue) qry = qry.Where(c => c.ClienteId == clienteId.Value);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            qry = qry.Where(c =>
                (c.Marca != null && c.Marca.Contains(t)) ||
                (c.Modelo != null && c.Modelo.Contains(t)) ||
                (c.NumeroSerie != null && c.NumeroSerie.Contains(t)) ||
                (c.Cliente != null && c.Cliente.Nombre.Contains(t)));
        }
        var list = await qry.OrderByDescending(c => c.CreatedAt).ToListAsync();
        return Ok(list.Select(Map));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var c = await _db.CafeComodatos.Include(x => x.Cliente).Include(x => x.Pagos).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        return Ok(new
        {
            comodato = Map(c),
            pagos = c.Pagos.OrderByDescending(p => p.Fecha).ThenByDescending(p => p.Id)
                .Select(p => new PagoDto(p.Id, p.ComodatoId, p.Fecha, p.Importe, p.MedioPago, p.Notas, p.CreatedAt))
        });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var todos = await _db.CafeComodatos.Include(c => c.Pagos).ToListAsync();
        var comodatos = todos.Where(c => c.Modalidad == "COMODATO").ToList();
        var financiadas = todos.Where(c => c.Modalidad == "FINANCIADA").ToList();
        return Ok(new
        {
            comodatosTotales = comodatos.Count,
            comodatosActivos = comodatos.Count(c => c.Estado == "EN_CLIENTE"),
            financiadasTotales = financiadas.Count,
            financiadasActivas = financiadas.Count(c => c.Estado == "EN_CLIENTE"),
            financiadasPagadas = financiadas.Count(c => c.Estado == "PAGADA"),
            saldoFinanciamientoTotal = financiadas.Where(c => c.Estado == "EN_CLIENTE").Sum(c => c.SaldoFinanciamiento ?? 0),
            valorEstimadoComodatos = comodatos.Where(c => c.Estado == "EN_CLIENTE").Sum(c => c.ValorEstimado ?? 0)
        });
    }

    public record CreateRequest(int ClienteId, string Modalidad, string? Marca, string? Modelo, string? NumeroSerie,
        DateTime? FechaEntrega, string? Notas, decimal? ValorEstimado,
        decimal? PrecioVenta, int? CuotasTotales, decimal? ValorCuota, int? DiaPagoMensual);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req)
    {
        if (req.ClienteId <= 0) return BadRequest(new { error = "Cliente requerido" });
        var modalidad = (req.Modalidad ?? "COMODATO").ToUpperInvariant();
        if (modalidad != "COMODATO" && modalidad != "FINANCIADA")
            return BadRequest(new { error = "Modalidad debe ser COMODATO o FINANCIADA" });
        var cli = await _db.CafeClientes.FindAsync(req.ClienteId);
        if (cli is null) return BadRequest(new { error = "Cliente no encontrado" });

        var c = new CafeComodato
        {
            ClienteId = req.ClienteId,
            Modalidad = modalidad,
            Marca = req.Marca?.Trim(),
            Modelo = req.Modelo?.Trim(),
            NumeroSerie = req.NumeroSerie?.Trim(),
            FechaEntrega = req.FechaEntrega,
            Notas = req.Notas?.Trim(),
            ValorEstimado = req.ValorEstimado,
            Estado = "EN_CLIENTE",
            CreatedAt = DateTime.UtcNow
        };
        if (modalidad == "FINANCIADA")
        {
            c.PrecioVenta = req.PrecioVenta;
            c.CuotasTotales = req.CuotasTotales;
            c.ValorCuota = req.ValorCuota;
            c.DiaPagoMensual = req.DiaPagoMensual;
            c.SaldoFinanciamiento = req.PrecioVenta ?? 0m;
        }
        _db.CafeComodatos.Add(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeComodato", c.Id.ToString(), "CREATE", $"{modalidad} #{c.Id} para cliente {cli.Nombre}");
        c = await _db.CafeComodatos.Include(x => x.Cliente).Include(x => x.Pagos).FirstAsync(x => x.Id == c.Id);
        return Ok(Map(c));
    }

    public record UpdateRequest(string? Marca, string? Modelo, string? NumeroSerie, DateTime? FechaEntrega,
        string? Estado, DateTime? FechaDevolucion, string? Notas, decimal? ValorEstimado,
        decimal? PrecioVenta, int? CuotasTotales, decimal? ValorCuota, int? DiaPagoMensual);

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateRequest req)
    {
        var c = await _db.CafeComodatos.Include(x => x.Pagos).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        c.Marca = req.Marca?.Trim();
        c.Modelo = req.Modelo?.Trim();
        c.NumeroSerie = req.NumeroSerie?.Trim();
        c.FechaEntrega = req.FechaEntrega;
        if (!string.IsNullOrWhiteSpace(req.Estado)) c.Estado = req.Estado.Trim().ToUpperInvariant();
        c.FechaDevolucion = req.FechaDevolucion;
        c.Notas = req.Notas?.Trim();
        c.ValorEstimado = req.ValorEstimado;
        if (c.Modalidad == "FINANCIADA")
        {
            c.PrecioVenta = req.PrecioVenta;
            c.CuotasTotales = req.CuotasTotales;
            c.ValorCuota = req.ValorCuota;
            c.DiaPagoMensual = req.DiaPagoMensual;
            // Recalcular saldo en base a precio y pagos acumulados
            var pagado = c.Pagos.Sum(p => p.Importe);
            c.SaldoFinanciamiento = (req.PrecioVenta ?? 0m) - pagado;
        }
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(c));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.CafeComodatos.Include(x => x.Pagos).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        _db.CafeComodatos.Remove(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeComodato", id.ToString(), "DELETE", $"Comodato {id} eliminado");
        return Ok(new { ok = true });
    }

    public record RegistrarPagoRequest(DateTime Fecha, decimal Importe, string? MedioPago, string? Notas);

    [HttpPost("{id:int}/pagos")]
    public async Task<IActionResult> RegistrarPago(int id, [FromBody] RegistrarPagoRequest req)
    {
        var c = await _db.CafeComodatos.Include(x => x.Pagos).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        if (c.Modalidad != "FINANCIADA") return BadRequest(new { error = "Solo se registran pagos en máquinas FINANCIADAS" });
        if (req.Importe <= 0) return BadRequest(new { error = "Importe debe ser positivo" });

        var p = new CafeComodatoPago
        {
            ComodatoId = c.Id,
            Fecha = req.Fecha.Date,
            Importe = req.Importe,
            MedioPago = req.MedioPago?.Trim(),
            Notas = req.Notas?.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeComodatoPagos.Add(p);
        await _db.SaveChangesAsync();

        // Recalcular saldo
        var pagadoTotal = c.Pagos.Sum(x => x.Importe) + p.Importe;
        c.SaldoFinanciamiento = (c.PrecioVenta ?? 0m) - pagadoTotal;
        // Si el saldo quedo en 0 o menos, marcamos como PAGADA
        if (c.SaldoFinanciamiento <= 0.01m && c.Estado == "EN_CLIENTE")
        {
            c.Estado = "PAGADA";
        }
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeComodato", id.ToString(), "PAGO", $"Pago ${req.Importe:N2} registrado. Saldo: ${c.SaldoFinanciamiento:N2}");

        return Ok(new
        {
            pago = new PagoDto(p.Id, p.ComodatoId, p.Fecha, p.Importe, p.MedioPago, p.Notas, p.CreatedAt),
            saldo = c.SaldoFinanciamiento,
            estado = c.Estado
        });
    }

    [HttpDelete("{id:int}/pagos/{pagoId:int}")]
    public async Task<IActionResult> AnularPago(int id, int pagoId)
    {
        var c = await _db.CafeComodatos.Include(x => x.Pagos).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        var p = c.Pagos.FirstOrDefault(x => x.Id == pagoId);
        if (p is null) return NotFound();
        _db.CafeComodatoPagos.Remove(p);
        await _db.SaveChangesAsync();
        // Recalcular saldo y volver a EN_CLIENTE si estaba PAGADA
        var pagadoTotal = c.Pagos.Where(x => x.Id != pagoId).Sum(x => x.Importe);
        c.SaldoFinanciamiento = (c.PrecioVenta ?? 0m) - pagadoTotal;
        if (c.Estado == "PAGADA" && c.SaldoFinanciamiento > 0.01m) c.Estado = "EN_CLIENTE";
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeComodato", id.ToString(), "ANULAR_PAGO", $"Pago {pagoId} eliminado. Saldo: ${c.SaldoFinanciamiento:N2}");
        return Ok(new { ok = true, saldo = c.SaldoFinanciamiento, estado = c.Estado });
    }

    /// <summary>Cuantos comodatos tiene un cliente — para mostrar el iconito ☕ en el listado.</summary>
    [HttpGet("cliente/{clienteId:int}/count")]
    public async Task<IActionResult> CountByCliente(int clienteId)
    {
        var count = await _db.CafeComodatos.CountAsync(c => c.ClienteId == clienteId && c.Estado == "EN_CLIENTE");
        return Ok(new { count });
    }
}
