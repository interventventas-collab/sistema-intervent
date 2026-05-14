using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Cobranzas (Café → Tesorería → Cobranzas).
/// Espejo del flujo Contabilium: elegir cliente, ver sus comprobantes pendientes,
/// definir cuanto cobrar de cada uno (parcial / total / a cuenta), forma de pago combinada.
/// </summary>
[ApiController]
[Route("api/cafe/cobranzas")]
[Authorize]
public class CafeCobranzasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;
    private readonly CafeReciboCobranzaPdfService _pdfService;

    public CafeCobranzasController(AppDbContext db, AuditLogService audit, CafeReciboCobranzaPdfService pdfService)
    {
        _db = db; _audit = audit; _pdfService = pdfService;
    }

    /// <summary>Genera el PDF del recibo de cobranza.</summary>
    [HttpGet("{id:int}/pdf")]
    public async Task<IActionResult> DescargarPdf(int id)
    {
        var c = await _db.CafeCobranzas
            .Include(x => x.Cliente)
            .Include(x => x.Comprobantes).ThenInclude(cc => cc.Venta)
            .Include(x => x.Medios).ThenInclude(m => m.Caja)
            .Include(x => x.Medios).ThenInclude(m => m.Cheque)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        if (c.Cliente is null) return BadRequest(new { error = "Cliente no encontrado" });

        var settings = await _db.CafeSettings.FindAsync(1);
        var comps = c.Comprobantes.Select(x => (
            numero: x.Venta?.Numero ?? "",
            importe: x.Importe,
            aCuenta: x.VentaId is null
        )).ToList();
        var medios = c.Medios.Select(m => (
            cajaNombre: m.Caja?.Nombre ?? "—",
            importe: m.Importe,
            referencia: m.Referencia,
            chequeInfo: m.Cheque is null ? null : $"Cheque {m.Cheque.Banco} N° {m.Cheque.Numero}"
        )).ToList();

        var bytes = _pdfService.GenerarPdfBytes(c, c.Cliente, comps, medios, settings);
        return File(bytes, "application/pdf", $"Recibo-{c.Numero}.pdf");
    }

    public record ComprobantePendienteDto(
        int VentaId, string Numero, DateTime Fecha, decimal Total, decimal Pagado, decimal Saldo);

    public record CobranzaListDto(
        int Id, string Numero, DateTime Fecha, int ClienteId, string ClienteNombre,
        decimal Total, decimal Retenciones, string Estado);

    public record CobranzaDetalleDto(
        int Id, string Numero, DateTime Fecha, int ClienteId, string ClienteNombre,
        decimal Total, decimal Retenciones, string Estado, string? Operador, string? Observaciones,
        List<CobranzaComprobanteDto> Comprobantes, List<CobranzaMedioDto> Medios);

    public record CobranzaComprobanteDto(int Id, int? VentaId, string? VentaNumero, decimal Importe);
    public record CobranzaMedioDto(int Id, int CajaId, string CajaNombre, decimal Importe, string? Referencia, int? ChequeId);

    public record CrearCobranzaRequest(
        int ClienteId,
        decimal Retenciones,
        string? Operador,
        string? Observaciones,
        List<CrearComprobanteItem> Comprobantes,
        List<CrearMedioItem> Medios);

    public record CrearComprobanteItem(int? VentaId, decimal Importe);

    public record CrearMedioItem(
        int CajaId, decimal Importe, string? Referencia,
        // Datos del cheque si es medio cheque (CajaId apunta a una caja tipo CHEQUES_CARTERA)
        CrearChequeItem? Cheque);

    public record CrearChequeItem(
        string Numero, string Banco, string? Emisor, decimal Importe,
        DateTime? FechaCobro, DateTime? FechaVencimiento, string? Observaciones);

    /// <summary>
    /// Devuelve los comprobantes (ventas) del cliente con saldo pendiente.
    /// Saldo = Total venta − suma de Importes en CobranzasComprobantes que apunten a esta venta.
    /// </summary>
    [HttpGet("comprobantes-pendientes/{clienteId:int}")]
    public async Task<IActionResult> ComprobantesPendientes(int clienteId)
    {
        // Ventas del cliente
        var ventas = await _db.CafeVentas
            .Where(v => v.ClienteId == clienteId && v.Estado != "anulado")
            .Select(v => new { v.Id, v.Numero, v.Fecha, v.Total })
            .ToListAsync();

        if (ventas.Count == 0) return Ok(new List<ComprobantePendienteDto>());

        var ventaIds = ventas.Select(v => v.Id).ToList();
        // IMPORTANTE: solo contamos comprobantes de cobranzas VIGENTES. Si la cobranza
        // fue ANULADA, sus imputaciones NO deben sumar como pago — por eso el venta
        // tiene que volver a aparecer como pendiente.
        var pagadoPorVenta = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId != null && ventaIds.Contains(c.VentaId!.Value)
                && c.Cobranza!.Estado == "VIGENTE")
            .GroupBy(c => c.VentaId!.Value)
            .Select(g => new { VentaId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        var dict = pagadoPorVenta.ToDictionary(p => p.VentaId, p => p.Total);

        var result = ventas
            .Select(v => new ComprobantePendienteDto(
                v.Id, v.Numero ?? $"#{v.Id}", v.Fecha, v.Total,
                dict.TryGetValue(v.Id, out var p) ? p : 0m,
                v.Total - (dict.TryGetValue(v.Id, out var p2) ? p2 : 0m)))
            .Where(x => x.Saldo > 0.01m)  // solo pendientes
            .OrderBy(x => x.Fecha)
            .ToList();

        return Ok(result);
    }

    /// <summary>Lista cobranzas con filtros opcionales por cliente y rango de fechas.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? clienteId,
        [FromQuery] DateTime? desde,
        [FromQuery] DateTime? hasta,
        [FromQuery] int take = 200)
    {
        var q = _db.CafeCobranzas.Include(c => c.Cliente).AsQueryable();
        if (clienteId.HasValue) q = q.Where(c => c.ClienteId == clienteId.Value);
        if (desde.HasValue) q = q.Where(c => c.Fecha >= desde.Value);
        if (hasta.HasValue) q = q.Where(c => c.Fecha <= hasta.Value);

        var list = await q.OrderByDescending(c => c.Fecha).Take(take)
            .Select(c => new CobranzaListDto(
                c.Id, c.Numero, c.Fecha, c.ClienteId,
                c.Cliente != null ? c.Cliente.Nombre : "—",
                c.Total, c.Retenciones, c.Estado))
            .ToListAsync();
        return Ok(list);
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id)
    {
        var c = await _db.CafeCobranzas
            .Include(x => x.Cliente)
            .Include(x => x.Comprobantes).ThenInclude(cc => cc.Venta)
            .Include(x => x.Medios).ThenInclude(m => m.Caja)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();

        var dto = new CobranzaDetalleDto(
            c.Id, c.Numero, c.Fecha, c.ClienteId,
            c.Cliente?.Nombre ?? "—",
            c.Total, c.Retenciones, c.Estado, c.Operador, c.Observaciones,
            c.Comprobantes.Select(x => new CobranzaComprobanteDto(
                x.Id, x.VentaId, x.Venta?.Numero, x.Importe)).ToList(),
            c.Medios.Select(x => new CobranzaMedioDto(
                x.Id, x.CajaId, x.Caja?.Nombre ?? "—", x.Importe, x.Referencia, x.ChequeId)).ToList());
        return Ok(dto);
    }

    /// <summary>Crea una cobranza nueva. Valida coherencia: suma de medios + retenciones = suma de comprobantes.</summary>
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearCobranzaRequest req)
    {
        // Validaciones basicas
        var cliente = await _db.CafeClientes.FindAsync(req.ClienteId);
        if (cliente is null) return BadRequest(new { error = "Cliente no encontrado" });
        if (req.Comprobantes == null || req.Comprobantes.Count == 0)
            return BadRequest(new { error = "Hay que cobrar al menos un comprobante (o agregar como 'a cuenta')" });
        if (req.Medios == null || req.Medios.Count == 0)
            return BadRequest(new { error = "Hay que especificar al menos una forma de cobro" });

        var sumComprobantes = req.Comprobantes.Sum(c => c.Importe);
        var sumMedios = req.Medios.Sum(m => m.Importe);
        var retenciones = Math.Max(0m, req.Retenciones);

        // Regla: la suma de los medios + retenciones tiene que igualar el total imputado en comprobantes
        if (Math.Abs(sumComprobantes - (sumMedios + retenciones)) > 0.01m)
            return BadRequest(new { error = $"No cuadra: imputado a comprobantes ${sumComprobantes:N2} vs medios+retenciones ${(sumMedios+retenciones):N2}" });

        // Generar numero correlativo
        var ultimoNum = await _db.CafeCobranzas
            .Select(c => c.Numero)
            .ToListAsync();
        var maxSec = 0;
        foreach (var num in ultimoNum)
        {
            var parts = (num ?? "").Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var n) && n > maxSec) maxSec = n;
        }
        var numero = $"0100-{(maxSec + 1):D8}";

        var cobranza = new CafeCobranza
        {
            Numero = numero,
            Fecha = DateTime.UtcNow,
            ClienteId = req.ClienteId,
            Total = sumMedios,         // lo que efectivamente entro a las cajas
            Retenciones = retenciones,
            Operador = req.Operador,
            Observaciones = req.Observaciones,
            Estado = "VIGENTE"
        };
        _db.CafeCobranzas.Add(cobranza);
        await _db.SaveChangesAsync();  // necesito el Id para los hijos

        foreach (var comp in req.Comprobantes)
        {
            _db.CafeCobranzasComprobantes.Add(new CafeCobranzaComprobante
            {
                CobranzaId = cobranza.Id,
                VentaId = comp.VentaId,  // null = a cuenta
                Importe = comp.Importe
            });
        }

        foreach (var med in req.Medios)
        {
            var caja = await _db.CafeCajas.FindAsync(med.CajaId);
            if (caja is null)
                return BadRequest(new { error = $"Caja {med.CajaId} no existe" });

            int? chequeId = null;
            // Si la caja es CHEQUES_CARTERA y vino info de cheque, lo creamos
            if (caja.Tipo == "CHEQUES_CARTERA" && med.Cheque is not null)
            {
                var ch = new CafeCheque
                {
                    Numero = med.Cheque.Numero,
                    Banco = med.Cheque.Banco,
                    Emisor = med.Cheque.Emisor,
                    Importe = med.Cheque.Importe,
                    FechaCobro = med.Cheque.FechaCobro,
                    FechaVencimiento = med.Cheque.FechaVencimiento,
                    Observaciones = med.Cheque.Observaciones,
                    ClienteOrigenId = req.ClienteId,
                    Estado = "EN_CARTERA",
                    CobranzaOrigenId = cobranza.Id
                };
                _db.CafeCheques.Add(ch);
                await _db.SaveChangesAsync();
                chequeId = ch.Id;
            }

            _db.CafeCobranzasMedios.Add(new CafeCobranzaMedio
            {
                CobranzaId = cobranza.Id,
                CajaId = med.CajaId,
                Importe = med.Importe,
                Referencia = med.Referencia,
                ChequeId = chequeId
            });
        }
        await _db.SaveChangesAsync();

        await _audit.LogAsync("CafeCobranza", cobranza.Id.ToString(), "CREATE",
            $"Cobranza {numero} para cliente {cliente.Nombre}, total ${sumMedios:N2}");

        // Sincronizar flag IsPaid de las ventas imputadas (TRUE si saldo <= 0)
        await SincronizarIsPaidAsync(req.Comprobantes.Where(c => c.VentaId.HasValue).Select(c => c.VentaId!.Value).ToList());

        return Ok(new { id = cobranza.Id, numero });
    }

    /// <summary>
    /// Recalcula el flag IsPaid de cada venta tras un cambio de cobranza.
    /// Una venta esta pagada si la suma de cobranzas aplicadas a ella (de cobranzas VIGENTES) >= Total.
    /// </summary>
    private async Task SincronizarIsPaidAsync(List<int> ventaIds)
    {
        if (ventaIds == null || ventaIds.Count == 0) return;
        var ventas = await _db.CafeVentas.Where(v => ventaIds.Contains(v.Id)).ToListAsync();
        if (ventas.Count == 0) return;
        var pagadoPorVenta = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId != null && ventaIds.Contains(c.VentaId!.Value)
                && c.Cobranza!.Estado == "VIGENTE")
            .GroupBy(c => c.VentaId!.Value)
            .Select(g => new { VentaId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        var dict = pagadoPorVenta.ToDictionary(p => p.VentaId, p => p.Total);
        foreach (var v in ventas)
        {
            var pagado = dict.TryGetValue(v.Id, out var p) ? p : 0m;
            v.IsPaid = pagado >= v.Total - 0.01m;
        }
        await _db.SaveChangesAsync();
    }

    public record EliminarCobranzaRequest(string Password);

    /// <summary>
    /// Elimina FISICAMENTE una cobranza ANULADA. Requiere la clave del usuario actual
    /// como confirmacion porque borra registros historicos. Solo aplica a cobranzas
    /// que ya estan en estado ANULADA — las VIGENTES hay que anularlas primero.
    /// Borra los comprobantes/medios linkeados (FK cascade), no toca los cheques ya
    /// rechazados (esos quedan en la chequera como historial).
    /// </summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Eliminar(int id, [FromBody] EliminarCobranzaRequest req)
    {
        // 1) Verificar clave del usuario actual
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                       ?? User.FindFirst("sub")?.Value;
        if (!int.TryParse(userIdClaim, out var userId))
            return Unauthorized(new { error = "Sesion invalida" });
        var user = await _db.Users.FindAsync(userId);
        if (user is null) return Unauthorized(new { error = "Usuario no encontrado" });
        if (string.IsNullOrEmpty(req?.Password) || !BCrypt.Net.BCrypt.Verify(req.Password, user.PasswordHash))
            return BadRequest(new { error = "Clave incorrecta" });

        // 2) La cobranza debe existir y estar ANULADA
        var c = await _db.CafeCobranzas
            .Include(x => x.Medios)
            .Include(x => x.Comprobantes)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        if (c.Estado != "ANULADA")
            return BadRequest(new { error = "Solo se pueden eliminar cobranzas ANULADAS. Anula la cobranza primero." });

        // 3) Borrar comprobantes y medios linkeados, despues la cobranza
        if (c.Comprobantes.Count > 0) _db.CafeCobranzasComprobantes.RemoveRange(c.Comprobantes);
        if (c.Medios.Count > 0) _db.CafeCobranzasMedios.RemoveRange(c.Medios);
        _db.CafeCobranzas.Remove(c);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeCobranza", id.ToString(), "ELIMINAR", $"Cobranza {c.Numero} eliminada fisicamente (clave OK)", user.Email);
        return Ok(new { ok = true });
    }

    [HttpPost("{id:int}/anular")]
    public async Task<IActionResult> Anular(int id)
    {
        var c = await _db.CafeCobranzas
            .Include(x => x.Medios)
            .Include(x => x.Comprobantes)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        if (c.Estado == "ANULADA") return BadRequest(new { error = "Ya esta anulada" });
        c.Estado = "ANULADA";
        c.UpdatedAt = DateTime.UtcNow;
        // Si algun medio creo un cheque EN_CARTERA, lo marcamos como rechazado (cobranza revertida)
        foreach (var m in c.Medios.Where(m => m.ChequeId.HasValue))
        {
            var ch = await _db.CafeCheques.FindAsync(m.ChequeId!.Value);
            if (ch is not null && ch.Estado == "EN_CARTERA")
            {
                ch.Estado = "RECHAZADO";
                ch.FechaCambioEstado = DateTime.UtcNow;
                ch.Observaciones = (ch.Observaciones ?? "") + " · Cobranza anulada";
            }
        }
        await _db.SaveChangesAsync();
        // Re-sincronizar IsPaid de las ventas afectadas (que ahora vuelven a "no pagadas")
        await SincronizarIsPaidAsync(c.Comprobantes.Where(cc => cc.VentaId.HasValue).Select(cc => cc.VentaId!.Value).ToList());
        await _audit.LogAsync("CafeCobranza", id.ToString(), "ANULAR", $"Cobranza {c.Numero} anulada");
        return Ok(new { ok = true });
    }
}
