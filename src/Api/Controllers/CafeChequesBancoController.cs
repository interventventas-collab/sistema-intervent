using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// CRUD + importacion del listado de cheques que descarga el banco (Galicia / BBVA / etc.).
/// El usuario sube los 3 Excel del banco (recibidos / emitidos / endosados), aca los parseamos
/// y guardamos cada cheque en Cafe_ChequesBanco. La clave para no duplicar al re-importar es
/// el "ID del cheque" del banco (campo IdBanco). El parseo del Excel vive en
/// ChequesBancoImportService (compartido con el robot de Galicia que baja el .XLS solo).
/// </summary>
[ApiController]
[Route("api/cafe/cheques-banco")]
[Authorize]
public class CafeChequesBancoController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ChequesBancoImportService _import;
    private readonly ILogger<CafeChequesBancoController> _logger;

    public CafeChequesBancoController(AppDbContext db, ChequesBancoImportService import, ILogger<CafeChequesBancoController> logger)
    {
        _db = db;
        _import = import;
        _logger = logger;
    }

    public record ChequeBancoDto(int Id, string IdBanco, string Tipo, string Numero,
        string? Cmc7, string? Clausula, string? BancoEmisor, DateTime? FechaEmision, DateTime? FechaPago,
        decimal Importe, string Estado, string? Motivo, string? CuentaLibradora, string? CbuDeposito,
        string? LibradorNombre, string? LibradorCuit,
        string? BeneficiarioActualNombre, string? BeneficiarioActualCuit,
        string? ContraparteNombre, string? ContraparteCuit,
        int CantidadEndosos, int CantidadCesiones, int CantidadAvales,
        // Linkeo a cobranza (cuando el e-cheq se asocio)
        int? CafeChequeId = null, int? CobranzaId = null, string? CobranzaNumero = null);

    public record ChequesResumenDto(int Cantidad, decimal Importe);
    public record StatsDto(
        ChequesResumenDto EmitidosPorPagar,    // Tipo=EMITIDO, Estado=Aceptado
        ChequesResumenDto RecibidosDisponibles, // Tipo=RECIBIDO, Estado=Disponible
        ChequesResumenDto EmitidosPagados,      // historico
        ChequesResumenDto RecibidosUsados);     // historico (pagados + endosados)

    [HttpGet("stats")]
    public async Task<IActionResult> Stats()
    {
        var todos = await _db.CafeChequesBanco.ToListAsync();
        ChequesResumenDto Sum(IEnumerable<CafeChequeBanco> xs)
        {
            var l = xs.ToList();
            return new ChequesResumenDto(l.Count, l.Sum(x => x.Importe));
        }
        // Un RECIBIDO se considera "Disponible" si su Estado es Disponible Y todavía no
        // fue asociado a una cobranza (CafeChequeId == null). El filtro del list usa la
        // misma regla — si no, la card mostraba "1 disponible" pero la tabla salía vacía.
        return Ok(new StatsDto(
            EmitidosPorPagar: Sum(todos.Where(x => x.Tipo == "EMITIDO" && string.Equals(x.Estado, "Aceptado", StringComparison.OrdinalIgnoreCase))),
            RecibidosDisponibles: Sum(todos.Where(x => x.Tipo == "RECIBIDO"
                && string.Equals(x.Estado, "Disponible", StringComparison.OrdinalIgnoreCase)
                && x.CafeChequeId == null)),
            EmitidosPagados: Sum(todos.Where(x => x.Tipo == "EMITIDO" && string.Equals(x.Estado, "Pagado", StringComparison.OrdinalIgnoreCase))),
            RecibidosUsados: Sum(todos.Where(x => x.Tipo != "EMITIDO"
                && (!string.Equals(x.Estado, "Disponible", StringComparison.OrdinalIgnoreCase) || x.CafeChequeId != null)))
        ));
    }

    /// <summary>2026-06-18 — Próximos cheques EMITIDOS por pagar (los que firmamos contra proveedores
    /// y aún no se cobraron). Para mostrar en el dropdown hover de la topbar. Excluye los ya pagados
    /// y los rechazados. Ordena por FechaPago ascendente (los más urgentes primero) y limita a TOP N.</summary>
    public record ChequeProximoDto(int Id, string Numero, DateTime? FechaPago, decimal Importe,
        string? ContraparteNombre, string? Motivo, string Estado);

    [HttpGet("proximos-pagar")]
    public async Task<IActionResult> ProximosPagar([FromQuery] int take = 5)
    {
        take = Math.Clamp(take, 1, 50);
        var hoy = DateTime.Today;
        // EMITIDOS + estado Aceptado/Disponible (no Pagados ni Rechazados). FechaPago futura o
        // ya vencida (importante: si quedo un cheque atrasado tambien aparece, como alerta).
        var list = await _db.CafeChequesBanco
            .Where(c => c.Tipo == "EMITIDO"
                && (c.Estado == "Aceptado" || c.Estado == "Disponible")
                && c.FechaPago.HasValue)
            .OrderBy(c => c.FechaPago)
            .ThenBy(c => c.Id)
            .Take(take)
            .Select(c => new ChequeProximoDto(c.Id, c.Numero, c.FechaPago, c.Importe,
                c.ContraparteNombre, c.Motivo, c.Estado))
            .ToListAsync();
        return Ok(list);
    }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? tipo = null, [FromQuery] string? estado = null,
        [FromQuery] string? q = null, [FromQuery] int take = 500)
    {
        var query = _db.CafeChequesBanco.Include(c => c.Cobranza).AsQueryable();
        if (!string.IsNullOrWhiteSpace(tipo)) query = query.Where(c => c.Tipo == tipo.ToUpperInvariant());
        if (!string.IsNullOrWhiteSpace(estado))
        {
            query = query.Where(c => c.Estado == estado);
            // Si pide Disponibles, excluir los que ya fueron asociados a una cobranza
            // (el flag CafeChequeId no es null cuando el e-cheq se uso para cobrar al cliente).
            if (string.Equals(estado, "Disponible", StringComparison.OrdinalIgnoreCase))
                query = query.Where(c => c.CafeChequeId == null);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            query = query.Where(c => c.Numero.Contains(t) ||
                (c.ContraparteNombre != null && c.ContraparteNombre.Contains(t)) ||
                (c.LibradorNombre != null && c.LibradorNombre.Contains(t)));
        }
        var list = await query
            .OrderBy(c => c.FechaPago ?? DateTime.MaxValue)
            .ThenBy(c => c.Id)
            .Take(Math.Clamp(take, 1, 2000))
            .Select(c => new ChequeBancoDto(c.Id, c.IdBanco, c.Tipo, c.Numero,
                c.Cmc7, c.Clausula, c.BancoEmisor, c.FechaEmision, c.FechaPago,
                c.Importe, c.Estado, c.Motivo, c.CuentaLibradora, c.CbuDeposito,
                c.LibradorNombre, c.LibradorCuit,
                c.BeneficiarioActualNombre, c.BeneficiarioActualCuit,
                c.ContraparteNombre, c.ContraparteCuit,
                c.CantidadEndosos, c.CantidadCesiones, c.CantidadAvales,
                c.CafeChequeId, c.CobranzaId, c.Cobranza != null ? c.Cobranza.Numero : null))
            .ToListAsync();
        return Ok(list);
    }

    public record SugerenciaClienteDto(int Id, string Nombre, string? RazonSocial, string? Cuit, int? CodigoInterno);
    public record ImputarComprobanteItem(int? VentaId, decimal Importe);
    public record AsociarECheqRequest(
        int ClienteId,
        decimal Retenciones,
        string? Observaciones,
        List<ImputarComprobanteItem> Comprobantes);

    /// <summary>Busca clientes que matchean al librador del e-cheq por CUIT exacto.
    /// Util para que el modal pre-cargue el cliente correcto sin tipearlo.</summary>
    [HttpGet("{id:int}/cliente-sugerido")]
    public async Task<IActionResult> ClienteSugerido(int id)
    {
        var ec = await _db.CafeChequesBanco.FindAsync(id);
        if (ec is null) return NotFound();
        var cuit = ec.LibradorCuit;
        if (string.IsNullOrWhiteSpace(cuit)) return Ok(new List<SugerenciaClienteDto>());
        var matches = await _db.CafeClientes
            .Where(c => c.IsActive && c.Cuit == cuit)
            .OrderBy(c => c.Nombre)
            .Select(c => new SugerenciaClienteDto(c.Id, c.Nombre, c.RazonSocial, c.Cuit, c.CodigoInterno))
            .ToListAsync();
        return Ok(matches);
    }

    /// <summary>
    /// Toma un e-cheq Disponible (del extracto bancario) y lo asocia a una cobranza:
    ///   1. Crea un CafeCheque "espejo" en cartera (linkeado al e-cheq via ChequeBancoId).
    ///   2. Crea una CafeCobranza para el cliente con el medio "Cheques en cartera" apuntando a ese cheque.
    ///   3. Imputa la cobranza a las facturas del cliente que vengan en req.Comprobantes.
    ///   4. Marca el e-cheq como utilizado (CafeChequeId + CobranzaId) para sacarlo de "Disponibles".
    /// La suma de Comprobantes debe igualar e-cheq.Importe + Retenciones.
    /// </summary>
    [HttpPost("{id:int}/asociar-cobranza")]
    public async Task<IActionResult> AsociarCobranza(int id, [FromBody] AsociarECheqRequest req)
    {
        var ec = await _db.CafeChequesBanco.FindAsync(id);
        if (ec is null) return NotFound(new { error = "E-cheq no encontrado" });
        if (ec.CafeChequeId.HasValue || ec.CobranzaId.HasValue)
            return BadRequest(new { error = "Este e-cheq ya está asociado a una cobranza" });
        if (!string.Equals(ec.Estado, "Disponible", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"El e-cheq no está Disponible (estado actual: {ec.Estado})" });

        var cliente = await _db.CafeClientes.FindAsync(req.ClienteId);
        if (cliente is null) return BadRequest(new { error = "Cliente no encontrado" });
        if (req.Comprobantes == null || req.Comprobantes.Count == 0)
            return BadRequest(new { error = "Imputá el cheque al menos a un comprobante (o como 'a cuenta')" });

        var sumComprobantes = req.Comprobantes.Sum(c => c.Importe);
        var retenciones = Math.Max(0m, req.Retenciones);
        if (Math.Abs(sumComprobantes - (ec.Importe + retenciones)) > 0.01m)
            return BadRequest(new { error = $"No cuadra: imputado ${sumComprobantes:N2} ≠ importe del e-cheq ${ec.Importe:N2} + retenciones ${retenciones:N2}" });

        // Resolver caja de cheques en cartera
        var caja = await _db.CafeCajas.FirstOrDefaultAsync(c => c.Tipo == "CHEQUES_CARTERA" && c.IsActive);
        if (caja is null) return BadRequest(new { error = "No hay una caja de tipo CHEQUES_CARTERA configurada" });

        // Generar numero correlativo de cobranza
        var ultimoNum = await _db.CafeCobranzas.Select(c => c.Numero).ToListAsync();
        var maxSec = 0;
        foreach (var num in ultimoNum)
        {
            var parts = (num ?? "").Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var n) && n > maxSec) maxSec = n;
        }
        var numeroCobranza = $"0100-{(maxSec + 1):D8}";

        // 1. Crear cobranza
        var cobranza = new CafeCobranza
        {
            Numero = numeroCobranza,
            Fecha = DateTime.UtcNow,
            ClienteId = req.ClienteId,
            Total = ec.Importe,
            Retenciones = retenciones,
            Operador = User?.Identity?.Name,
            Observaciones = string.IsNullOrWhiteSpace(req.Observaciones)
                ? $"Cobranza por e-cheq {ec.BancoEmisor} N° {ec.Numero}"
                : req.Observaciones.Trim(),
            Estado = "VIGENTE"
        };
        _db.CafeCobranzas.Add(cobranza);
        await _db.SaveChangesAsync();

        // 2. Crear el CafeCheque espejo en cartera
        var cheque = new CafeCheque
        {
            Numero = ec.Numero,
            Banco = ec.BancoEmisor ?? "(sin nombre)",
            BancoId = ec.BancoId,
            Emisor = ec.LibradorNombre,
            Importe = ec.Importe,
            FechaCobro = ec.FechaPago,
            FechaVencimiento = ec.FechaPago,
            ClienteOrigenId = req.ClienteId,
            Estado = "EN_CARTERA",
            FechaCambioEstado = DateTime.UtcNow,
            CobranzaOrigenId = cobranza.Id,
            ChequeBancoId = ec.Id,
            Observaciones = $"Importado del extracto bancario (ID banco: {ec.IdBanco})",
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeCheques.Add(cheque);
        await _db.SaveChangesAsync();

        // 3. Comprobantes imputados
        foreach (var comp in req.Comprobantes)
        {
            _db.CafeCobranzasComprobantes.Add(new CafeCobranzaComprobante
            {
                CobranzaId = cobranza.Id,
                VentaId = comp.VentaId,
                Importe = comp.Importe
            });
        }

        // 4. Medio de pago: el cheque que recién creamos
        _db.CafeCobranzasMedios.Add(new CafeCobranzaMedio
        {
            CobranzaId = cobranza.Id,
            CajaId = caja.Id,
            Importe = ec.Importe,
            Referencia = $"E-cheq #{ec.Numero} ({ec.BancoEmisor})",
            ChequeId = cheque.Id
        });

        // 5. Marcar el e-cheq como utilizado para que salga de Disponibles
        ec.CafeChequeId = cheque.Id;
        ec.CobranzaId = cobranza.Id;
        ec.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // 6. Sincronizar IsPaid de las ventas imputadas
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

        return Ok(new { cobranzaId = cobranza.Id, numero = numeroCobranza, chequeId = cheque.Id });
    }

    [HttpPost("import")]
    [RequestSizeLimit(50 * 1024 * 1024)]
    public async Task<IActionResult> Import(IFormFileCollection files)
    {
        if (files == null || files.Count == 0)
            return BadRequest(new { error = "Subi al menos un archivo Excel del banco." });
        var resultados = new List<ChequesBancoImportService.ImportResultDto>();
        foreach (var file in files)
        {
            using var stream = file.OpenReadStream();
            resultados.Add(await _import.ImportStreamAsync(stream, file.FileName));
        }
        return Ok(resultados);
    }
}
