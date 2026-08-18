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

    // ─── 2026-08-18: circuito en dos pasos para el que quiere seguir el cheque mientras
    // el banco lo procesa. El boton 🏦 de siempre (arriba) sigue haciendo todo de una.
    // El estado DEPOSITADO ya existia en el modelo pero NADA lo seteaba: ningun cheque
    // podia llegar a el, asi que la etiqueta "🏦 Depositado" era letra muerta.

    /// <summary>Paso 1: lo mandé al banco pero todavia no se acredito. Queda en DEPOSITADO.</summary>
    [HttpPost("{id:int}/mandar-al-banco")]
    public async Task<IActionResult> MandarAlBanco(int id, [FromBody] CambiarEstadoRequest? req)
    {
        var ch = await _db.CafeCheques.FindAsync(id);
        if (ch is null) return NotFound();
        if (ch.Estado != "EN_CARTERA") return BadRequest(new { error = $"El cheque ya esta {ch.Estado}, no se puede depositar" });
        ch.Estado = "DEPOSITADO";
        ch.FechaCambioEstado = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(req?.Observaciones))
            ch.Observaciones = (ch.Observaciones ?? "") + " · " + req!.Observaciones;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeCheque", id.ToString(), "MANDAR_AL_BANCO", $"Cheque {ch.Numero} depositado, esperando acreditacion");
        return Ok(new { ok = true });
    }

    /// <summary>Paso 2: el banco lo acredito. De DEPOSITADO (o directo de cartera) pasa a ACREDITADO.</summary>
    [HttpPost("{id:int}/acreditar")]
    public async Task<IActionResult> Acreditar(int id, [FromBody] CambiarEstadoRequest? req)
    {
        var ch = await _db.CafeCheques.FindAsync(id);
        if (ch is null) return NotFound();
        if (ch.Estado != "DEPOSITADO" && ch.Estado != "EN_CARTERA")
            return BadRequest(new { error = $"El cheque esta {ch.Estado}, no se puede acreditar" });
        ch.Estado = "ACREDITADO";
        ch.FechaCambioEstado = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(req?.Observaciones))
            ch.Observaciones = (ch.Observaciones ?? "") + " · " + req!.Observaciones;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeCheque", id.ToString(), "ACREDITAR", $"Cheque {ch.Numero} acreditado en el banco");
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

    // ============================================================
    // 2026-08-18: Hacer la cobranza directamente desde un cheque en cartera.
    // Antes, un cheque cargado a mano quedaba "sin asignar" y para descontarle la deuda al cliente
    // habia que ir a Cobranzas y volver a tipear los datos del cheque — camino que CREA UN CHEQUE NUEVO,
    // dejando dos cheques en cartera por el mismo papel. Este endpoint usa el cheque que ya existe.
    // Es el equivalente del "asociar-cobranza" de los e-cheques del banco (CafeChequesBancoController).
    // ============================================================

    public record ImputarItem(int? VentaId, decimal Importe);
    public record CobranzaDesdeChequeRequest(int ClienteId, decimal Retenciones, string? Observaciones, List<ImputarItem> Comprobantes);

    /// <summary>Crea una cobranza usando ESTE cheque como forma de cobro y la imputa a los comprobantes elegidos.
    /// Solo para cheques EN_CARTERA que todavia no nacieron de una cobranza.</summary>
    [HttpPost("{id:int}/cobranza")]
    public async Task<IActionResult> CrearCobranzaDesdeCheque(int id, [FromBody] CobranzaDesdeChequeRequest req)
    {
        var ch = await _db.CafeCheques.FindAsync(id);
        if (ch is null) return NotFound(new { error = "Cheque no encontrado" });
        if (ch.Estado != "EN_CARTERA")
            return BadRequest(new { error = $"El cheque esta {ch.Estado} — solo se puede cobrar uno en cartera" });
        if (ch.CobranzaOrigenId.HasValue)
            return BadRequest(new { error = "Este cheque ya vino de una cobranza, no se puede imputar de nuevo" });

        var cliente = await _db.CafeClientes.FindAsync(req.ClienteId);
        if (cliente is null) return BadRequest(new { error = "Cliente no encontrado" });
        if (req.Comprobantes == null || req.Comprobantes.Count == 0)
            return BadRequest(new { error = "Imputá el cheque al menos a un comprobante (o como 'a cuenta')" });

        var retenciones = Math.Max(0m, req.Retenciones);
        var sumComprobantes = req.Comprobantes.Sum(c => c.Importe);
        if (Math.Abs(sumComprobantes - (ch.Importe + retenciones)) > 0.01m)
            return BadRequest(new { error = $"No cuadra: imputado ${sumComprobantes:N2} ≠ cheque ${ch.Importe:N2} + retenciones ${retenciones:N2}" });

        // Las ventas imputadas tienen que ser del mismo cliente (o de una sucursal con el mismo CUIT)
        var ventaIdsReq = req.Comprobantes.Where(c => c.VentaId.HasValue).Select(c => c.VentaId!.Value).Distinct().ToList();
        if (ventaIdsReq.Count > 0)
        {
            List<int> clientesValidos = new() { req.ClienteId };
            if (!string.IsNullOrWhiteSpace(cliente.Cuit))
                clientesValidos = await _db.CafeClientes.Where(c => c.Cuit == cliente.Cuit).Select(c => c.Id).ToListAsync();
            var ventasReq = await _db.CafeVentas.Where(v => ventaIdsReq.Contains(v.Id))
                .Select(v => new { v.Id, v.ClienteId }).ToListAsync();
            if (ventasReq.Count != ventaIdsReq.Count)
                return BadRequest(new { error = "Alguna venta referenciada no existe" });
            var ajena = ventasReq.FirstOrDefault(v => !v.ClienteId.HasValue || !clientesValidos.Contains(v.ClienteId.Value));
            if (ajena is not null)
                return BadRequest(new { error = $"La venta #{ajena.Id} no pertenece a este cliente" });
        }

        var caja = await _db.CafeCajas.FirstOrDefaultAsync(c => c.Tipo == "CHEQUES_CARTERA" && c.IsActive);
        if (caja is null) return BadRequest(new { error = "No hay una caja de tipo CHEQUES_CARTERA configurada" });

        // Numero correlativo de cobranza (mismo criterio que CafeCobranzasController)
        var numeros = await _db.CafeCobranzas.Select(c => c.Numero).ToListAsync();
        var maxSec = 0;
        foreach (var num in numeros)
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
                VentaId = comp.VentaId,   // null = a cuenta
                Importe = comp.Importe
            });
        }

        // El medio de cobro es el cheque que YA estaba en cartera (no se crea uno nuevo)
        _db.CafeCobranzasMedios.Add(new CafeCobranzaMedio
        {
            CobranzaId = cobranza.Id,
            CajaId = caja.Id,
            Importe = ch.Importe,
            Referencia = $"Cheque N° {ch.Numero} ({ch.Banco})",
            ChequeId = ch.Id
        });

        ch.ClienteOrigenId = req.ClienteId;
        ch.CobranzaOrigenId = cobranza.Id;
        await _db.SaveChangesAsync();

        // Resincronizar el flag "pagada" de las ventas imputadas
        if (ventaIdsReq.Count > 0)
        {
            var ventas = await _db.CafeVentas.Where(v => ventaIdsReq.Contains(v.Id)).ToListAsync();
            var pagado = await _db.CafeCobranzasComprobantes
                .Where(c => c.VentaId != null && ventaIdsReq.Contains(c.VentaId!.Value)
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

        await _audit.LogAsync("CafeCheque", id.ToString(), "COBRANZA_DESDE_CHEQUE",
            $"Cheque {ch.Numero} imputado a {cliente.Nombre} en cobranza {numeroCobranza} por ${ch.Importe:N2}" +
            (retenciones > 0 ? $" + ${retenciones:N2} de retenciones" : ""));

        return Ok(new { cobranzaId = cobranza.Id, numero = numeroCobranza });
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
