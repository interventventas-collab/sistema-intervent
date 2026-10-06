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
    private readonly CafeComodatoPdfService _pdfService;
    public CafeComodatosController(AppDbContext db, AuditLogService audit, CafeComodatoPdfService pdfService)
    {
        _db = db;
        _audit = audit;
        _pdfService = pdfService;
    }

    /// <summary>Genera y devuelve el PDF del comprobante (comodato o financiacion) para
    /// que el cliente lo firme. Numero estable derivado del Id + año de creacion.</summary>
    [HttpGet("{id:int}/comprobante.pdf")]
    public async Task<IActionResult> ComprobantePdf(int id)
    {
        var (bytes, fileName) = await GenerarPdfBytesAsync(id);
        if (bytes is null) return NotFound();
        return File(bytes, "application/pdf", fileName);
    }

    /// <summary>30/09/2026: bytes del comprobante (el mismo del botón), para el robot de respaldo en Drive.
    /// (null, "") si no existe.</summary>
    [NonAction]
    public async Task<(byte[]? bytes, string fileName)> GenerarPdfBytesAsync(int id)
    {
        var c = await _db.CafeComodatos
            .Include(x => x.Cliente)
            .Include(x => x.Pagos)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return (null, "");
        var settings = await _db.CafeSettings.FirstOrDefaultAsync();
        // 2026-10-06: los pagos cargados como cobranza (recibo + caja) también van en el comprobante.
        // Se arman renglones sueltos (no se guardan) con el mismo formato que los pagos viejos.
        var pagosPdf = c.Pagos.Select(p => new CafeComodatoPago { Fecha = p.Fecha, Importe = p.Importe, MedioPago = p.MedioPago, Notas = p.Notas }).ToList();
        foreach (var pc in await PagosPorCobranzaAsync(c))
            pagosPdf.Add(new CafeComodatoPago
            {
                Fecha = pc.Fecha.AddHours(-3).Date,   // la cobranza guarda UTC; el comprobante va en día argentino
                Importe = pc.Importe,
                MedioPago = $"Recibo {pc.ReciboNumero}",
                Notas = pc.MedioPago
            });
        var bytes = _pdfService.GenerarPdfBytes(c, c.Cliente, pagosPdf, settings);
        return (bytes, CafeComodatoPdfService.GetNumeroComprobante(c) + ".pdf");
    }

    public record ComodatoDto(int Id, int ClienteId, string? ClienteNombre, string Modalidad, string Moneda,
        string? Marca, string? Modelo, string? NumeroSerie, DateTime? FechaEntrega,
        string Estado, DateTime? FechaDevolucion, string? Notas, decimal? ValorEstimado,
        decimal? PrecioVenta, int? CuotasTotales, decimal? ValorCuota, int? DiaPagoMensual,
        decimal? SaldoFinanciamiento, decimal PagosAcumulados, int PagosCount,
        DateTime CreatedAt);

    private static ComodatoDto Map(CafeComodato c, Dictionary<int, CafeComodatoSaldoService.Cobrado>? cobrado = null)
    {
        // Calculamos saldo en RUNTIME (Precio - Pagos), no usamos c.SaldoFinanciamiento
        // guardado en DB porque a veces queda viejo si el precio cambio despues de hacer pagos
        // o por bugs viejos de migracion. La unica fuente de verdad son Precio + lista de Pagos.
        // 2026-10-06: pagos = los viejos anotados a mano + las cobranzas vigentes imputadas a la máquina
        // (en la moneda de la máquina). Misma cuenta que CafeComodatoSaldoService.
        var cob = cobrado is not null && cobrado.TryGetValue(c.Id, out var cb) ? cb : null;
        var pagosAcumulados = (c.Pagos?.Sum(p => p.Importe) ?? 0m) + (cob?.Monto ?? 0m);
        decimal? saldoCalculado = c.Modalidad == "FINANCIADA"
            ? Math.Max(0m, (c.PrecioVenta ?? 0m) - pagosAcumulados)
            : null;
        return new ComodatoDto(
            c.Id, c.ClienteId, c.Cliente?.Nombre, c.Modalidad, c.Moneda,
            c.Marca, c.Modelo, c.NumeroSerie, c.FechaEntrega,
            c.Estado, c.FechaDevolucion, c.Notas, c.ValorEstimado,
            c.PrecioVenta, c.CuotasTotales, c.ValorCuota, c.DiaPagoMensual,
            saldoCalculado,
            pagosAcumulados,
            (c.Pagos?.Count ?? 0) + (cob?.Cantidad ?? 0),
            c.CreatedAt);
    }

    /// <summary>Un pago de la máquina. Importe va en la moneda de la máquina.
    /// 2026-10-06: además de los pagos viejos (EsAnterior = true, anotados a mano, sin caja ni recibo)
    /// vienen las cobranzas: CobranzaId + ReciboNumero, cómo entró la plata (MedioPago) y, si la
    /// máquina es en dólares, cuántos pesos fueron (ImportePesos) y con qué dólar (DolarDelDia).
    /// Id = id del pago viejo (0 en las cobranzas: esas se anulan desde Tesorería).</summary>
    public record PagoDto(int Id, int ComodatoId, DateTime Fecha, decimal Importe, string? MedioPago, string? Notas, DateTime CreatedAt,
        bool EsAnterior = true, int? CobranzaId = null, string? ReciboNumero = null,
        decimal? ImportePesos = null, decimal? DolarDelDia = null);

    /// <summary>2026-10-06: las cobranzas vigentes imputadas a esta máquina, con cómo entró la plata
    /// (caja, cheque, redirigido → a quién).</summary>
    private async Task<List<PagoDto>> PagosPorCobranzaAsync(CafeComodato c)
    {
        var filas = await _db.CafeCobranzasComprobantes.AsNoTracking()
            .Where(x => x.ComodatoId == c.Id && x.Cobranza!.Estado == "VIGENTE")
            .Select(x => new
            {
                x.Id, x.CobranzaId, x.Cobranza!.Numero, x.Cobranza.Fecha, x.Cobranza.Observaciones, x.Cobranza.CreatedAt,
                x.Importe, x.ImporteUsd,
                Medios = x.Cobranza.Medios.Select(m => new
                {
                    Caja = m.Caja != null ? m.Caja.Nombre : "",
                    Tipo = m.Caja != null ? m.Caja.Tipo : "",
                    m.RedirigidoEmpleadoId, m.RedirigidoProveedorId, m.RedirigidoDestino,
                    ChequeBanco = m.Cheque != null ? m.Cheque.Banco : null,
                    ChequeNumero = m.Cheque != null ? m.Cheque.Numero : null
                }).ToList()
            })
            .ToListAsync();
        if (filas.Count == 0) return new();

        var empIds = filas.SelectMany(f => f.Medios).Where(m => m.RedirigidoEmpleadoId != null)
            .Select(m => m.RedirigidoEmpleadoId!.Value).Distinct().ToList();
        var provIds = filas.SelectMany(f => f.Medios).Where(m => m.RedirigidoProveedorId != null)
            .Select(m => m.RedirigidoProveedorId!.Value).Distinct().ToList();
        var emps = empIds.Count == 0 ? new Dictionary<int, string>()
            : await _db.NomEmpleados.Where(e => empIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Nombre);
        var provs = provIds.Count == 0 ? new Dictionary<int, string>()
            : await _db.CafeProveedores.Where(p => provIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Nombre);

        string Redirigido(int? empId, int? provId, string? destino) =>
            empId is int eid ? (emps.TryGetValue(eid, out var en) ? en : $"empleado #{eid}") + (string.IsNullOrEmpty(destino) ? "" : $" · {destino}")
            : provId is int pid ? (provs.TryGetValue(pid, out var pn) ? pn : $"proveedor #{pid}")
            : destino == "privada" ? "queda en la privada"
            : "sin decir a quién";

        var usd = c.Moneda == "USD";
        return filas.Select(f =>
        {
            var como = string.Join(" · ", f.Medios.Select(m =>
                    m.Tipo == "V_PRIVADO" ? "Redirigido → " + Redirigido(m.RedirigidoEmpleadoId, m.RedirigidoProveedorId, m.RedirigidoDestino)
                    : m.ChequeNumero is not null ? $"Cheque {m.ChequeBanco} N° {m.ChequeNumero}"
                    : m.Caja)
                .Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());
            if (string.IsNullOrWhiteSpace(como)) como = "Retenciones";
            var importe = usd ? (f.ImporteUsd ?? 0m) : f.Importe;
            decimal? dolar = usd && (f.ImporteUsd ?? 0m) > 0m ? Math.Round(f.Importe / f.ImporteUsd!.Value, 2) : null;
            return new PagoDto(0, c.Id, f.Fecha, importe, como, f.Observaciones, f.CreatedAt,
                false, f.CobranzaId, f.Numero, usd ? f.Importe : null, dolar);
        }).ToList();
    }

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
        var cobrado = await CafeComodatoSaldoService.CobradoPorCobranzasAsync(_db, list.Select(c => c.Id));
        return Ok(list.Select(c => Map(c, cobrado)));
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var c = await _db.CafeComodatos.Include(x => x.Cliente).Include(x => x.Pagos).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        var cobrado = await CafeComodatoSaldoService.CobradoPorCobranzasAsync(_db, new[] { c.Id });
        // 2026-10-06: viejos (anotados a mano) + cobranzas, el más nuevo primero.
        var pagos = c.Pagos.Select(p => new PagoDto(p.Id, p.ComodatoId, p.Fecha, p.Importe, p.MedioPago, p.Notas, p.CreatedAt))
            .Concat(await PagosPorCobranzaAsync(c))
            .OrderByDescending(p => p.Fecha).ThenByDescending(p => p.CreatedAt)
            .ToList();
        return Ok(new
        {
            comodato = Map(c, cobrado),
            pagos
        });
    }

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var todos = await _db.CafeComodatos.Include(c => c.Pagos).ToListAsync();
        // 2026-10-06: lo cobrado por cobranzas también descuenta (en la moneda de cada máquina).
        var cobrado = await CafeComodatoSaldoService.CobradoPorCobranzasAsync(_db, todos.Select(c => c.Id));
        var comodatos = todos.Where(c => c.Modalidad == "COMODATO").ToList();
        var financiadas = todos.Where(c => c.Modalidad == "FINANCIADA").ToList();
        // Calcular saldo en runtime (Precio - Pagos) por moneda — NO sumamos USD con ARS.
        decimal SaldoCalculado(CafeComodato c) =>
            Math.Max(0m, (c.PrecioVenta ?? 0m) - (c.Pagos?.Sum(p => p.Importe) ?? 0m)
                         - (cobrado.TryGetValue(c.Id, out var cb) ? cb.Monto : 0m));
        var activasArs = financiadas.Where(c => c.Estado == "EN_CLIENTE" && c.Moneda == "ARS").ToList();
        var activasUsd = financiadas.Where(c => c.Estado == "EN_CLIENTE" && c.Moneda == "USD").ToList();
        return Ok(new
        {
            comodatosTotales = comodatos.Count,
            comodatosActivos = comodatos.Count(c => c.Estado == "EN_CLIENTE"),
            financiadasTotales = financiadas.Count,
            financiadasActivas = financiadas.Count(c => c.Estado == "EN_CLIENTE"),
            financiadasPagadas = financiadas.Count(c => c.Estado == "PAGADA"),
            // Total ARS y USD por separado (NO se mezclan)
            saldoFinanciamientoArs = activasArs.Sum(SaldoCalculado),
            saldoFinanciamientoUsd = activasUsd.Sum(SaldoCalculado),
            // Campo legacy — suma SOLO ARS para que el frontend viejo siga funcionando con
            // el numero correcto (sin contaminar con USD). Cuando el frontend actualice, sacar.
            saldoFinanciamientoTotal = activasArs.Sum(SaldoCalculado),
            valorEstimadoComodatos = comodatos.Where(c => c.Estado == "EN_CLIENTE").Sum(c => c.ValorEstimado ?? 0)
        });
    }

    public record CreateRequest(int ClienteId, string Modalidad, string? Moneda, string? Marca, string? Modelo, string? NumeroSerie,
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

        var moneda = (req.Moneda ?? "ARS").ToUpperInvariant();
        if (moneda != "ARS" && moneda != "USD") moneda = "ARS";
        var c = new CafeComodato
        {
            ClienteId = req.ClienteId,
            Modalidad = modalidad,
            Moneda = moneda,
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

    public record UpdateRequest(int? ClienteId, string? Moneda, string? Marca, string? Modelo, string? NumeroSerie, DateTime? FechaEntrega,
        string? Estado, DateTime? FechaDevolucion, string? Notas, decimal? ValorEstimado,
        decimal? PrecioVenta, int? CuotasTotales, decimal? ValorCuota, int? DiaPagoMensual, string? Modalidad = null);

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateRequest req)
    {
        var c = await _db.CafeComodatos.Include(x => x.Pagos).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        if (req.ClienteId.HasValue && req.ClienteId.Value > 0 && req.ClienteId.Value != c.ClienteId)
        {
            var existe = await _db.CafeClientes.AnyAsync(x => x.Id == req.ClienteId.Value);
            if (!existe) return BadRequest(new { error = "El cliente seleccionado no existe." });
            c.ClienteId = req.ClienteId.Value;
        }
        if (!string.IsNullOrWhiteSpace(req.Moneda))
        {
            var mon = req.Moneda.Trim().ToUpperInvariant();
            if (mon == "ARS" || mon == "USD") c.Moneda = mon;
        }
        c.Marca = req.Marca?.Trim();
        c.Modelo = req.Modelo?.Trim();
        c.NumeroSerie = req.NumeroSerie?.Trim();
        c.FechaEntrega = req.FechaEntrega;
        if (!string.IsNullOrWhiteSpace(req.Estado)) c.Estado = req.Estado.Trim().ToUpperInvariant();
        c.FechaDevolucion = req.FechaDevolucion;
        c.Notas = req.Notas?.Trim();
        c.ValorEstimado = req.ValorEstimado;

        // Cambio de modalidad (Comodato <-> Financiada).
        // Opción B: Financiada -> Comodato SOLO si no tiene pagos (para no perder plata cargada).
        if (!string.IsNullOrWhiteSpace(req.Modalidad))
        {
            var nuevaMod = req.Modalidad.Trim().ToUpperInvariant();
            if (nuevaMod != "COMODATO" && nuevaMod != "FINANCIADA")
                return BadRequest(new { error = "Modalidad inválida." });
            if (nuevaMod != c.Modalidad)
            {
                if (nuevaMod == "COMODATO")
                {
                    // 2026-10-06: también cuentan los pagos cargados como cobranza.
                    if (c.Pagos.Any() || await _db.CafeCobranzasComprobantes.AnyAsync(x => x.ComodatoId == c.Id && x.Cobranza!.Estado == "VIGENTE"))
                        return BadRequest(new { error = "Esta máquina financiada tiene pagos registrados. Anulá los pagos (o las cobranzas en Tesorería) antes de pasarla a comodato." });
                    c.Modalidad = "COMODATO";
                    c.PrecioVenta = null; c.CuotasTotales = null; c.ValorCuota = null;
                    c.DiaPagoMensual = null; c.SaldoFinanciamiento = null;
                    if (c.Estado == "PAGADA") c.Estado = "EN_CLIENTE";   // comodato no tiene 'pagada'
                }
                else // pasar a FINANCIADA
                {
                    if ((req.PrecioVenta ?? 0m) <= 0m)
                        return BadRequest(new { error = "Cargá el precio total para pasarla a financiada." });
                    c.Modalidad = "FINANCIADA";
                }
            }
        }

        if (c.Modalidad == "FINANCIADA")
        {
            c.PrecioVenta = req.PrecioVenta;
            c.CuotasTotales = req.CuotasTotales;
            c.ValorCuota = req.ValorCuota;
            c.DiaPagoMensual = req.DiaPagoMensual;
        }
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        // 2026-10-06: saldo = precio − (pagos viejos + cobranzas). Antes miraba sólo los pagos viejos.
        // El estado no se toca: acá lo elige el operador (igual que antes).
        if (c.Modalidad == "FINANCIADA") await CafeComodatoSaldoService.RecalcularAsync(_db, c.Id, tocarEstado: false);
        var cobrado = await CafeComodatoSaldoService.CobradoPorCobranzasAsync(_db, new[] { c.Id });
        return Ok(Map(c, cobrado));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var c = await _db.CafeComodatos.Include(x => x.Pagos).FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        // 2026-10-06: si tiene pagos cargados como cobranza, borrarla dejaría recibos apuntando a nada
        // (y la plata en la caja sin saber de qué fue). Primero se anulan esas cobranzas.
        var recibos = await _db.CafeCobranzasComprobantes
            .Where(x => x.ComodatoId == id && x.Cobranza!.Estado == "VIGENTE")
            .Select(x => x.Cobranza!.Numero).Distinct().ToListAsync();
        if (recibos.Count > 0)
            return BadRequest(new { error = $"Esta máquina tiene pagos cargados como cobranza ({string.Join(", ", recibos)}). Anulalas en Tesorería → Cobranzas antes de eliminarla." });
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

        // 2026-10-06: saldo y estado los recalcula el servicio (pagos viejos + cobranzas). Desde hoy la
        // pantalla carga los pagos como cobranza (Tesorería); esta entrada queda por compatibilidad.
        await CafeComodatoSaldoService.RecalcularAsync(_db, c.Id);
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
        // Recalcular saldo y volver a EN_CLIENTE si estaba PAGADA (2026-10-06: con las cobranzas incluidas).
        await CafeComodatoSaldoService.RecalcularAsync(_db, c.Id);
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
