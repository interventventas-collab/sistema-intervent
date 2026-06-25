using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Cheques de terceros: trackeo individual y transiciones de estado.
/// Estados v1: EN_CARTERA, DEPOSITADO, ACREDITADO, COBRADO_VENTANILLA, ENDOSADO, RECHAZADO.
/// El cheque se crea automaticamente cuando una cobranza usa un medio de tipo CHEQUES_CARTERA.
/// Desde aca solo se hacen las transiciones (depositar, cobrar, rechazar). Endosar viene en Fase 2 con Pagos.
/// </summary>
[ApiController]
[Route("api/cafe/cheques")]
[Authorize]
public class CafeChequesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;

    public CafeChequesController(AppDbContext db, AuditLogService audit) { _db = db; _audit = audit; }

    public record ChequeDto(
        int Id, string Numero, string Banco, string? Emisor,
        int? ClienteOrigenId, string? ClienteOrigenNombre,
        decimal Importe, DateTime? FechaCobro, DateTime? FechaVencimiento,
        string Estado, DateTime? FechaCambioEstado, string? Observaciones, int? CobranzaOrigenId,
        DateTime CreatedAt);

    public record CreateChequeRequest(
        string Numero, int? BancoId, string? Banco, string? Emisor,
        decimal Importe, DateTime? FechaCobro, DateTime? FechaVencimiento,
        int? ClienteOrigenId, string? Observaciones);

    /// <summary>Alta manual de cheque (papel) que entra a cartera. Cliente origen es opcional —
    /// si se carga sin cliente, despues se asigna al usarlo en una cobranza.
    /// BancoId es la forma nueva (apunta al catalogo Cafe_Bancos); Banco string queda
    /// como fallback para compatibilidad.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateChequeRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Numero)) return BadRequest(new { error = "Número obligatorio" });
        if (!req.BancoId.HasValue && string.IsNullOrWhiteSpace(req.Banco))
            return BadRequest(new { error = "Banco obligatorio" });
        if (req.Importe <= 0) return BadRequest(new { error = "Importe debe ser mayor a 0" });

        // Resolver banco del catalogo
        CafeBanco? banco = null;
        string bancoTextoFinal;
        if (req.BancoId.HasValue)
        {
            banco = await _db.CafeBancos.FindAsync(req.BancoId.Value);
            if (banco is null) return BadRequest(new { error = "Banco del catálogo no encontrado" });
            bancoTextoFinal = banco.Alias ?? banco.Nombre;
        }
        else
        {
            bancoTextoFinal = req.Banco!.Trim();
        }

        // Anti-duplicado: mismo banco (preferentemente BancoId, sino texto) + numero + importe en cartera
        var dupQuery = _db.CafeCheques.Where(c =>
            c.Numero == req.Numero.Trim() && c.Importe == req.Importe && c.Estado != "RECHAZADO");
        dupQuery = banco is not null
            ? dupQuery.Where(c => c.BancoId == banco.Id || c.Banco == bancoTextoFinal)
            : dupQuery.Where(c => c.Banco == bancoTextoFinal);
        var dup = await dupQuery.FirstOrDefaultAsync();
        if (dup is not null)
            return Conflict(new { error = $"Ya existe el cheque {dup.Banco} N° {dup.Numero} por ${dup.Importe} (estado: {dup.Estado})", existingId = dup.Id });

        var ch = new CafeCheque
        {
            Numero = req.Numero.Trim(),
            Banco = bancoTextoFinal,
            BancoId = banco?.Id,
            Emisor = string.IsNullOrWhiteSpace(req.Emisor) ? null : req.Emisor.Trim(),
            Importe = req.Importe,
            FechaCobro = req.FechaCobro,
            FechaVencimiento = req.FechaVencimiento,
            ClienteOrigenId = req.ClienteOrigenId,
            Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim(),
            Estado = "EN_CARTERA",
            FechaCambioEstado = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeCheques.Add(ch);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeCheque", ch.Id.ToString(), "CREAR_MANUAL",
            $"Alta manual: {ch.Banco} N° {ch.Numero} por ${ch.Importe}{(ch.ClienteOrigenId.HasValue ? $" (cliente origen {ch.ClienteOrigenId.Value})" : " sin cliente")}");
        return Ok(new ChequeDto(
            ch.Id, ch.Numero, ch.Banco, ch.Emisor,
            ch.ClienteOrigenId, null,
            ch.Importe, ch.FechaCobro, ch.FechaVencimiento,
            ch.Estado, ch.FechaCambioEstado, ch.Observaciones, ch.CobranzaOrigenId,
            ch.CreatedAt));
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? estado = null, [FromQuery] int take = 500)
    {
        var q = _db.CafeCheques.Include(c => c.ClienteOrigen).AsQueryable();
        if (!string.IsNullOrWhiteSpace(estado)) q = q.Where(c => c.Estado == estado);
        var list = await q.OrderByDescending(c => c.CreatedAt).Take(take)
            .Select(c => new ChequeDto(
                c.Id, c.Numero, c.Banco, c.Emisor,
                c.ClienteOrigenId, c.ClienteOrigen != null ? c.ClienteOrigen.Nombre : null,
                c.Importe, c.FechaCobro, c.FechaVencimiento,
                c.Estado, c.FechaCambioEstado, c.Observaciones, c.CobranzaOrigenId,
                c.CreatedAt))
            .ToListAsync();
        return Ok(list);
    }

    public record CambiarEstadoRequest(string? Observaciones, int? CajaDestinoId);

    /// <summary>
    /// Marca el cheque como Depositado (mandado al banco). Suma a la caja destino (tipica: Galicia Empresas).
    /// Si no se especifica CajaDestinoId, busca la primera caja de tipo BANCO.
    /// </summary>
    [HttpPost("{id:int}/depositar")]
    public async Task<IActionResult> Depositar(int id, [FromBody] CambiarEstadoRequest? req)
    {
        var ch = await _db.CafeCheques.FindAsync(id);
        if (ch is null) return NotFound();
        if (ch.Estado != "EN_CARTERA") return BadRequest(new { error = $"El cheque ya esta {ch.Estado}, no se puede depositar" });

        // Buscar caja destino (banco)
        int? cajaId = req?.CajaDestinoId;
        if (!cajaId.HasValue)
        {
            var banco = await _db.CafeCajas.FirstOrDefaultAsync(c => c.Tipo == "BANCO" && c.IsActive);
            cajaId = banco?.Id;
        }
        if (!cajaId.HasValue) return BadRequest(new { error = "No hay caja de tipo BANCO configurada" });

        // Simplificacion v1: depositar = ya acreditado (saltea el estado intermedio DEPOSITADO)
        ch.Estado = "ACREDITADO";
        ch.FechaCambioEstado = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(req?.Observaciones))
            ch.Observaciones = (ch.Observaciones ?? "") + " · " + req!.Observaciones;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeCheque", id.ToString(), "DEPOSITAR_ACREDITAR", $"Cheque {ch.Numero} depositado/acreditado en caja {cajaId.Value}");
        return Ok(new { ok = true });
    }

    /// <summary>Marca el cheque como Cobrado por ventanilla. Suma a Efectivo.</summary>
    [HttpPost("{id:int}/cobrar-ventanilla")]
    public async Task<IActionResult> CobrarVentanilla(int id, [FromBody] CambiarEstadoRequest? req)
    {
        var ch = await _db.CafeCheques.FindAsync(id);
        if (ch is null) return NotFound();
        if (ch.Estado != "EN_CARTERA") return BadRequest(new { error = $"El cheque ya esta {ch.Estado}" });
        ch.Estado = "COBRADO_VENTANILLA";
        ch.FechaCambioEstado = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(req?.Observaciones))
            ch.Observaciones = (ch.Observaciones ?? "") + " · " + req!.Observaciones;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeCheque", id.ToString(), "COBRAR_VENTANILLA", $"Cheque {ch.Numero} cobrado por ventanilla");
        return Ok(new { ok = true });
    }

    /// <summary>Marca el cheque como Rechazado (rebote). La deuda vuelve al cliente origen.</summary>
    [HttpPost("{id:int}/rechazar")]
    public async Task<IActionResult> Rechazar(int id, [FromBody] CambiarEstadoRequest? req)
    {
        var ch = await _db.CafeCheques.FindAsync(id);
        if (ch is null) return NotFound();
        if (ch.Estado == "ENDOSADO" || ch.Estado == "RECHAZADO")
            return BadRequest(new { error = $"No se puede rechazar un cheque {ch.Estado}" });
        ch.Estado = "RECHAZADO";
        ch.FechaCambioEstado = DateTime.UtcNow;
        ch.Observaciones = (ch.Observaciones ?? "") + " · REBOTADO" + (string.IsNullOrWhiteSpace(req?.Observaciones) ? "" : " · " + req!.Observaciones);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeCheque", id.ToString(), "RECHAZAR", $"Cheque {ch.Numero} rechazado/rebotado");
        return Ok(new { ok = true });
    }

    public record ImputarComprobanteItem(int? VentaId, decimal Importe);
    public record AplicarChequeRequest(
        int ClienteId,
        decimal Retenciones,
        string? Observaciones,
        List<ImputarComprobanteItem> Comprobantes);

    /// <summary>
    /// Aplica un cheque EN_CARTERA (cargado manualmente o importado por Excel) a una cobranza nueva.
    /// Espejo de CafeChequesBancoController.AsociarCobranza, pero sin crear un cheque "espejo":
    /// reusa el cheque ya existente y lo linkea al medio de pago de la cobranza.
    /// La suma de Comprobantes debe igualar cheque.Importe + Retenciones.
    /// </summary>
    [HttpPost("{id:int}/aplicar-a-cobranza")]
    public async Task<IActionResult> AplicarACobranza(int id, [FromBody] AplicarChequeRequest req)
    {
        var ch = await _db.CafeCheques.FindAsync(id);
        if (ch is null) return NotFound(new { error = "Cheque no encontrado" });
        if (ch.Estado != "EN_CARTERA")
            return BadRequest(new { error = $"El cheque está {ch.Estado}, solo se pueden aplicar los EN_CARTERA" });
        if (ch.CobranzaOrigenId.HasValue)
            return BadRequest(new { error = $"Este cheque ya está vinculado a la cobranza #{ch.CobranzaOrigenId.Value}" });

        var cliente = await _db.CafeClientes.FindAsync(req.ClienteId);
        if (cliente is null) return BadRequest(new { error = "Cliente no encontrado" });
        if (req.Comprobantes == null || req.Comprobantes.Count == 0)
            return BadRequest(new { error = "Imputá el cheque al menos a un comprobante (o como 'a cuenta')" });

        var sumComprobantes = req.Comprobantes.Sum(c => c.Importe);
        var retenciones = Math.Max(0m, req.Retenciones);
        if (Math.Abs(sumComprobantes - (ch.Importe + retenciones)) > 0.01m)
            return BadRequest(new { error = $"No cuadra: imputado ${sumComprobantes:N2} ≠ importe del cheque ${ch.Importe:N2} + retenciones ${retenciones:N2}" });

        var caja = await _db.CafeCajas.FirstOrDefaultAsync(c => c.Tipo == "CHEQUES_CARTERA" && c.IsActive);
        if (caja is null) return BadRequest(new { error = "No hay una caja de tipo CHEQUES_CARTERA configurada" });

        var ultimoNum = await _db.CafeCobranzas.Select(c => c.Numero).ToListAsync();
        var maxSec = 0;
        foreach (var num in ultimoNum)
        {
            var parts = (num ?? "").Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var n) && n > maxSec) maxSec = n;
        }
        var numeroCobranza = $"0100-{(maxSec + 1):D8}";

        var cobranza = new CafeCobranza
        {
            Numero = numeroCobranza,
            Fecha = DateTime.UtcNow,
            ClienteId = req.ClienteId,
            Total = ch.Importe,
            Retenciones = retenciones,
            Operador = User?.Identity?.Name,
            Observaciones = string.IsNullOrWhiteSpace(req.Observaciones)
                ? $"Cobranza por cheque {ch.Banco} N° {ch.Numero}"
                : req.Observaciones.Trim(),
            Estado = "VIGENTE"
        };
        _db.CafeCobranzas.Add(cobranza);
        await _db.SaveChangesAsync();

        foreach (var comp in req.Comprobantes)
        {
            _db.CafeCobranzasComprobantes.Add(new CafeCobranzaComprobante
            {
                CobranzaId = cobranza.Id,
                VentaId = comp.VentaId,
                Importe = comp.Importe
            });
        }

        _db.CafeCobranzasMedios.Add(new CafeCobranzaMedio
        {
            CobranzaId = cobranza.Id,
            CajaId = caja.Id,
            Importe = ch.Importe,
            Referencia = $"Cheque {ch.Banco} N° {ch.Numero}",
            ChequeId = ch.Id
        });

        ch.CobranzaOrigenId = cobranza.Id;
        if (!ch.ClienteOrigenId.HasValue) ch.ClienteOrigenId = req.ClienteId;
        ch.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        var ventaIds = req.Comprobantes.Where(c => c.VentaId.HasValue).Select(c => c.VentaId!.Value).Distinct().ToList();
        if (ventaIds.Count > 0)
        {
            var ventas = await _db.CafeVentas.Where(v => ventaIds.Contains(v.Id)).ToListAsync();
            var pagado = await _db.CafeCobranzasComprobantes
                .Where(c => c.VentaId != null && ventaIds.Contains(c.VentaId!.Value)
                    && c.Cobranza!.Estado == "VIGENTE")
                .GroupBy(c => c.VentaId!.Value)
                .Select(g => new { Id = g.Key, Total = g.Sum(x => x.Importe) })
                .ToDictionaryAsync(x => x.Id, x => x.Total);
            foreach (var v in ventas)
            {
                var pag = pagado.GetValueOrDefault(v.Id, 0m);
                var totalCobrar = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
                v.IsPaid = pag >= totalCobrar - 0.01m;
            }
            await _db.SaveChangesAsync();
        }

        await _audit.LogAsync("CafeCheque", id.ToString(), "APLICAR_A_COBRANZA",
            $"Cheque {ch.Numero} aplicado a cobranza {numeroCobranza} del cliente {cliente.Nombre} (${ch.Importe:N2})");

        return Ok(new { cobranzaId = cobranza.Id, numero = numeroCobranza });
    }
}
