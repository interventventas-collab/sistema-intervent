using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Modulo Pagos Movil. Pantalla simplificada que usan Osmar / Germán / Gabriel desde el celular
/// para PRECARGAR pagos (a empleados o a proveedores) cuando estan fuera de la oficina.
/// El pago queda en estado PENDIENTE en PagosMovil_Pendientes y NO impacta saldos hasta que
/// alguien lo confirma desde la PC. Al confirmar:
///   - tipo='empleado' → crea Nom_Pago atado a una Nom_Liquidacion (si hay, sino crea una vacia del mes).
///   - tipo='proveedor' → crea Cafe_PagoProveedor + Comprobantes + Medios.
/// </summary>
[ApiController]
[Route("api/pagos-movil")]
[Authorize]
public class PagosMovilController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;

    public PagosMovilController(AppDbContext db, AuditLogService audit) { _db = db; _audit = audit; }

    // ────────────────────────────────────────────────────────────────────────────
    // DTOs
    // ────────────────────────────────────────────────────────────────────────────

    public record EmpleadoActivoDto(int Id, string Nombre, string? Puesto);
    public record ProveedorConDeudaDto(int Id, string Nombre, decimal Deuda, int CantidadFacturas);
    public record CompraPendienteDto(int Id, string Numero, DateTime Fecha, decimal Total, decimal Pagado, decimal Saldo, string? NumeroComprobante);

    public record PrecargarEmpleadoRequest(
        int EmpleadoId,
        string Concepto,            // sueldo / adelanto / aguinaldo / bono / vacaciones / otro: xxx
        decimal Monto,
        string MedioPago,           // efectivo / transferencia / mp / cheque
        string? Notas);

    public record PrecargarFacturaRequest(
        int ProveedorId,
        List<PrecargarFacturaItem> Comprobantes,
        string MedioPago,
        string? Notas);
    public record PrecargarFacturaItem(int CompraId, decimal Importe);

    public record PendienteListDto(
        int Id, string Tipo,
        int? EmpleadoId, string? EmpleadoNombre,
        int? ProveedorId, string? ProveedorNombre,
        string Concepto, decimal Monto, string MedioPago, string? Notas,
        DateTime CreatedAt, string CreadoPor,
        int CantidadComprobantes);

    public record PendienteDetalleDto(
        int Id, string Tipo,
        int? EmpleadoId, string? EmpleadoNombre,
        int? ProveedorId, string? ProveedorNombre,
        string Concepto, decimal Monto, string MedioPago, string? Notas,
        DateTime CreatedAt, string CreadoPor,
        List<ComprobanteDetalleDto> Comprobantes);
    public record ComprobanteDetalleDto(int CompraId, string? CompraNumero, decimal Importe);

    public record ConfirmarRequest(int? CajaId, DateTime? FechaPago);
    public record RechazarRequest(string? Motivo);
    public record EditarRequest(
        string? Concepto, decimal? Monto, string? MedioPago, string? Notas,
        List<PrecargarFacturaItem>? Comprobantes);

    // ────────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────────

    private int? GetUserId()
    {
        var c = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
             ?? User.FindFirst("sub")?.Value;
        return int.TryParse(c, out var id) ? id : null;
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Listados para los selects de la pantalla movil
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>Empleados activos para el select de Pago Empleados.</summary>
    [HttpGet("empleados-activos")]
    public async Task<IActionResult> EmpleadosActivos()
    {
        var list = await _db.NomEmpleados
            .Where(e => e.IsActive)
            .OrderBy(e => e.Nombre)
            .Select(e => new EmpleadoActivoDto(e.Id, e.Nombre, e.Puesto))
            .ToListAsync();
        return Ok(list);
    }

    /// <summary>Proveedores con deuda pendiente (saldo > 0).</summary>
    [HttpGet("proveedores-con-deuda")]
    public async Task<IActionResult> ProveedoresConDeuda()
    {
        // Para cada compra VIGENTE, calcular saldo (Total - sum(pagos VIGENTE))
        var compras = await _db.CafeCompras
            .Where(c => c.Estado != "ANULADA")
            .Select(c => new { c.Id, c.ProveedorId, c.Total, ProveedorNombre = c.Proveedor!.Nombre })
            .ToListAsync();
        if (compras.Count == 0) return Ok(new List<ProveedorConDeudaDto>());

        var ids = compras.Select(c => c.Id).ToList();
        var pagado = await _db.CafePagosProveedorComprobantes
            .Where(c => c.CompraId != null && ids.Contains(c.CompraId!.Value) && c.Pago!.Estado == "VIGENTE")
            .GroupBy(c => c.CompraId!.Value)
            .Select(g => new { CompraId = g.Key, Total = g.Sum(x => x.Importe) })
            .ToListAsync();
        var dictPag = pagado.ToDictionary(p => p.CompraId, p => p.Total);

        var result = compras
            .Select(c => new
            {
                c.ProveedorId,
                c.ProveedorNombre,
                Saldo = c.Total - (dictPag.TryGetValue(c.Id, out var p) ? p : 0m)
            })
            .Where(x => x.Saldo > 0.01m)
            .GroupBy(x => new { x.ProveedorId, x.ProveedorNombre })
            .Select(g => new ProveedorConDeudaDto(
                g.Key.ProveedorId, g.Key.ProveedorNombre,
                g.Sum(x => x.Saldo), g.Count()))
            .OrderByDescending(x => x.Deuda)
            .ToList();
        return Ok(result);
    }

    /// <summary>Compras pendientes (con saldo) de un proveedor.</summary>
    [HttpGet("proveedor/{proveedorId:int}/compras-pendientes")]
    public async Task<IActionResult> ComprasPendientes(int proveedorId)
    {
        var compras = await _db.CafeCompras
            .Where(c => c.ProveedorId == proveedorId && c.Estado != "ANULADA")
            .Select(c => new { c.Id, c.Numero, c.Fecha, c.Total, c.NumeroComprobante })
            .ToListAsync();
        if (compras.Count == 0) return Ok(new List<CompraPendienteDto>());

        var ids = compras.Select(c => c.Id).ToList();
        var pagado = await _db.CafePagosProveedorComprobantes
            .Where(c => c.CompraId != null && ids.Contains(c.CompraId!.Value) && c.Pago!.Estado == "VIGENTE")
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

    // ────────────────────────────────────────────────────────────────────────────
    // Precarga desde el movil
    // ────────────────────────────────────────────────────────────────────────────

    [HttpPost("empleado")]
    public async Task<IActionResult> PrecargarEmpleado([FromBody] PrecargarEmpleadoRequest req)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var emp = await _db.NomEmpleados.FindAsync(req.EmpleadoId);
        if (emp is null) return BadRequest(new { error = "Empleado no encontrado" });
        if (req.Monto <= 0) return BadRequest(new { error = "El monto debe ser mayor a 0" });
        if (string.IsNullOrWhiteSpace(req.Concepto)) return BadRequest(new { error = "Falta concepto" });
        if (string.IsNullOrWhiteSpace(req.MedioPago)) return BadRequest(new { error = "Falta medio de pago" });

        var p = new PagosMovilPendiente
        {
            Tipo = "empleado",
            EmpleadoId = req.EmpleadoId,
            Concepto = req.Concepto.Trim(),
            Monto = req.Monto,
            MedioPago = req.MedioPago.Trim(),
            Notas = req.Notas,
            Estado = "PENDIENTE",
            CreadoPorUsuarioId = userId.Value,
            CreatedAt = DateTime.UtcNow
        };
        _db.PagosMovilPendientes.Add(p);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("PagosMovilPendiente", p.Id.ToString(), "PRECARGAR",
            $"Empleado {emp.Nombre} · {req.Concepto} · ${req.Monto:N2} · {req.MedioPago}");
        return Ok(new { id = p.Id });
    }

    [HttpPost("factura")]
    public async Task<IActionResult> PrecargarFactura([FromBody] PrecargarFacturaRequest req)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var prov = await _db.CafeProveedores.FindAsync(req.ProveedorId);
        if (prov is null) return BadRequest(new { error = "Proveedor no encontrado" });
        if (req.Comprobantes is null || req.Comprobantes.Count == 0)
            return BadRequest(new { error = "Tildá al menos una factura a pagar" });
        if (string.IsNullOrWhiteSpace(req.MedioPago)) return BadRequest(new { error = "Falta medio de pago" });

        // Validar que los importes no superen el saldo de cada compra
        var compraIds = req.Comprobantes.Select(c => c.CompraId).Distinct().ToList();
        var compras = await _db.CafeCompras
            .Where(c => compraIds.Contains(c.Id) && c.ProveedorId == req.ProveedorId)
            .ToListAsync();
        if (compras.Count != compraIds.Count)
            return BadRequest(new { error = "Alguna factura no pertenece al proveedor" });

        var total = req.Comprobantes.Sum(c => c.Importe);
        if (total <= 0) return BadRequest(new { error = "El total debe ser mayor a 0" });

        var p = new PagosMovilPendiente
        {
            Tipo = "proveedor",
            ProveedorId = req.ProveedorId,
            Concepto = "Pago facturas",
            Monto = total,
            MedioPago = req.MedioPago.Trim(),
            Notas = req.Notas,
            Estado = "PENDIENTE",
            CreadoPorUsuarioId = userId.Value,
            CreatedAt = DateTime.UtcNow,
            Comprobantes = req.Comprobantes.Select(c => new PagosMovilPendienteComprobante
            {
                CompraId = c.CompraId,
                Importe = c.Importe
            }).ToList()
        };
        _db.PagosMovilPendientes.Add(p);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("PagosMovilPendiente", p.Id.ToString(), "PRECARGAR",
            $"Proveedor {prov.Nombre} · {req.Comprobantes.Count} fact. · ${total:N2} · {req.MedioPago}");
        return Ok(new { id = p.Id });
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Bandeja de pendientes
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>Lista todos los pagos PENDIENTES (ordenados por mas reciente).</summary>
    [HttpGet("pendientes")]
    public async Task<IActionResult> ListarPendientes()
    {
        var list = await _db.PagosMovilPendientes
            .Where(p => p.Estado == "PENDIENTE")
            .OrderByDescending(p => p.CreatedAt)
            .Select(p => new PendienteListDto(
                p.Id, p.Tipo,
                p.EmpleadoId, p.Empleado != null ? p.Empleado.Nombre : null,
                p.ProveedorId, p.Proveedor != null ? p.Proveedor.Nombre : null,
                p.Concepto, p.Monto, p.MedioPago, p.Notas,
                p.CreatedAt, p.CreadoPor != null ? p.CreadoPor.Email : "—",
                p.Comprobantes.Count))
            .ToListAsync();
        return Ok(list);
    }

    /// <summary>Contador de pendientes (para el badge del topbar / boton).</summary>
    [HttpGet("pendientes/count")]
    public async Task<IActionResult> CountPendientes()
    {
        var n = await _db.PagosMovilPendientes.CountAsync(p => p.Estado == "PENDIENTE");
        return Ok(new { count = n });
    }

    /// <summary>Detalle completo de un pendiente (incluye comprobantes si es de proveedor).</summary>
    [HttpGet("pendientes/{id:int}")]
    public async Task<IActionResult> GetPendiente(int id)
    {
        var p = await _db.PagosMovilPendientes
            .Include(x => x.Empleado)
            .Include(x => x.Proveedor)
            .Include(x => x.CreadoPor)
            .Include(x => x.Comprobantes).ThenInclude(c => c.Compra)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        return Ok(new PendienteDetalleDto(
            p.Id, p.Tipo,
            p.EmpleadoId, p.Empleado?.Nombre,
            p.ProveedorId, p.Proveedor?.Nombre,
            p.Concepto, p.Monto, p.MedioPago, p.Notas,
            p.CreatedAt, p.CreadoPor?.Email ?? "—",
            p.Comprobantes.Select(c => new ComprobanteDetalleDto(c.CompraId, c.Compra?.Numero, c.Importe)).ToList()));
    }

    /// <summary>Edita un pendiente antes de confirmarlo (cambiar monto, concepto, medio, comprobantes).</summary>
    [HttpPut("pendientes/{id:int}")]
    public async Task<IActionResult> EditarPendiente(int id, [FromBody] EditarRequest req)
    {
        var p = await _db.PagosMovilPendientes
            .Include(x => x.Comprobantes)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = "Solo se editan los PENDIENTE" });

        if (!string.IsNullOrWhiteSpace(req.Concepto)) p.Concepto = req.Concepto.Trim();
        if (req.Monto.HasValue && req.Monto.Value > 0) p.Monto = req.Monto.Value;
        if (!string.IsNullOrWhiteSpace(req.MedioPago)) p.MedioPago = req.MedioPago.Trim();
        if (req.Notas != null) p.Notas = req.Notas;

        if (req.Comprobantes != null && p.Tipo == "proveedor")
        {
            _db.PagosMovilPendientesComprobantes.RemoveRange(p.Comprobantes);
            p.Comprobantes = req.Comprobantes.Select(c => new PagosMovilPendienteComprobante
            {
                PendienteId = p.Id,
                CompraId = c.CompraId,
                Importe = c.Importe
            }).ToList();
            p.Monto = req.Comprobantes.Sum(c => c.Importe);
        }
        await _db.SaveChangesAsync();
        await _audit.LogAsync("PagosMovilPendiente", id.ToString(), "EDITAR", $"Pendiente editado");
        return Ok(new { ok = true });
    }

    /// <summary>Confirma un pendiente. Crea el pago REAL en la tabla correspondiente.</summary>
    [HttpPost("pendientes/{id:int}/confirmar")]
    public async Task<IActionResult> Confirmar(int id, [FromBody] ConfirmarRequest req)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var p = await _db.PagosMovilPendientes
            .Include(x => x.Comprobantes)
            .Include(x => x.Empleado)
            .Include(x => x.Proveedor)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Estado actual: {p.Estado}" });

        var fecha = req.FechaPago ?? DateTime.UtcNow;

        if (p.Tipo == "empleado")
        {
            if (p.EmpleadoId is null) return BadRequest(new { error = "Pendiente sin empleado" });

            // Buscar o crear la liquidacion abierta del mes actual del empleado.
            var anio = fecha.Year; var mes = fecha.Month;
            var liq = await _db.NomLiquidaciones
                .FirstOrDefaultAsync(l => l.EmpleadoId == p.EmpleadoId && l.Anio == anio && l.Mes == mes);
            if (liq is null)
            {
                liq = new NomLiquidacion
                {
                    EmpleadoId = p.EmpleadoId.Value,
                    Anio = anio, Mes = mes,
                    Estado = "pendiente",
                    CreatedAt = DateTime.UtcNow
                };
                _db.NomLiquidaciones.Add(liq);
                await _db.SaveChangesAsync();
            }

            var nomPago = new NomPago
            {
                LiquidacionId = liq.Id,
                FechaPago = fecha,
                Metodo = p.MedioPago,
                Monto = p.Monto,
                Concepto = MapConceptoToNomPago(p.Concepto),
                Detalle = p.Concepto,         // guardamos el concepto original (puede ser "otro: xxx")
                Notas = p.Notas,
                CreatedAt = DateTime.UtcNow
            };
            _db.NomPagos.Add(nomPago);
            await _db.SaveChangesAsync();

            p.NomPagoId = nomPago.Id;
            p.LiquidacionId = liq.Id;
            p.Estado = "CONFIRMADO";
            p.ConfirmadoPorUsuarioId = userId;
            p.ConfirmadoAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await _audit.LogAsync("PagosMovilPendiente", id.ToString(), "CONFIRMAR",
                $"Confirmado pago a {p.Empleado?.Nombre} → NomPago #{nomPago.Id}");
            return Ok(new { ok = true, nomPagoId = nomPago.Id });
        }
        else if (p.Tipo == "proveedor")
        {
            if (p.ProveedorId is null) return BadRequest(new { error = "Pendiente sin proveedor" });
            if (req.CajaId is null) return BadRequest(new { error = "Hace falta CajaId para confirmar pagos a proveedor" });
            if (p.Comprobantes.Count == 0) return BadRequest(new { error = "Pendiente sin comprobantes" });

            // Generar numero correlativo OP-XXXXXXXX (mismo patron que CafePagosProveedorController)
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
                Fecha = fecha,
                ProveedorId = p.ProveedorId.Value,
                Total = p.Monto,
                Retenciones = 0m,
                Operador = p.CreadoPor?.Email,
                Observaciones = p.Notas,
                Estado = "VIGENTE",
                CreatedAt = DateTime.UtcNow
            };
            _db.CafePagosProveedor.Add(pago);
            await _db.SaveChangesAsync();

            foreach (var c in p.Comprobantes)
                _db.CafePagosProveedorComprobantes.Add(new CafePagoProveedorComprobante
                {
                    PagoId = pago.Id, CompraId = c.CompraId, Importe = c.Importe
                });

            _db.CafePagosProveedorMedios.Add(new CafePagoProveedorMedio
            {
                PagoId = pago.Id,
                CajaId = req.CajaId.Value,
                Importe = p.Monto,
                Referencia = p.MedioPago
            });
            await _db.SaveChangesAsync();

            p.CafePagoProveedorId = pago.Id;
            p.Estado = "CONFIRMADO";
            p.ConfirmadoPorUsuarioId = userId;
            p.ConfirmadoAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            await _audit.LogAsync("PagosMovilPendiente", id.ToString(), "CONFIRMAR",
                $"Confirmado pago a {p.Proveedor?.Nombre} → CafePagoProveedor {numero}");
            return Ok(new { ok = true, cafePagoProveedorId = pago.Id, numero });
        }
        return BadRequest(new { error = $"Tipo desconocido: {p.Tipo}" });
    }

    /// <summary>Rechaza un pendiente (no crea pago real).</summary>
    [HttpPost("pendientes/{id:int}/rechazar")]
    public async Task<IActionResult> Rechazar(int id, [FromBody] RechazarRequest req)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();
        var p = await _db.PagosMovilPendientes.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        if (p.Estado != "PENDIENTE") return BadRequest(new { error = $"Estado actual: {p.Estado}" });

        p.Estado = "RECHAZADO";
        p.ConfirmadoPorUsuarioId = userId;
        p.ConfirmadoAt = DateTime.UtcNow;
        p.MotivoRechazo = req?.Motivo;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("PagosMovilPendiente", id.ToString(), "RECHAZAR", req?.Motivo ?? "(sin motivo)");
        return Ok(new { ok = true });
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Mapeo de conceptos (los del movil → los que acepta NomPago.Concepto)
    // ────────────────────────────────────────────────────────────────────────────

    private static string MapConceptoToNomPago(string conceptoMovil)
    {
        var c = (conceptoMovil ?? "").Trim().ToLowerInvariant();
        if (c.StartsWith("otro")) return "otro";
        return c switch
        {
            "sueldo" => "sueldo",
            "adelanto" => "adelanto",
            "aguinaldo" => "aguinaldo",
            "bono" => "bono",
            "vacaciones" => "otro",
            "premio" => "bono",
            "reintegro" => "otro",
            "comision" or "comision_cafe" => "comision_cafe",
            "horas_extra" or "horas extra" => "horas_extra",
            _ => "otro"
        };
    }
}
