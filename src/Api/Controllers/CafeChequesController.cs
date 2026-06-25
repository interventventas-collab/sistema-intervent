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
}
