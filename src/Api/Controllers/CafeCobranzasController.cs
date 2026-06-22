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
        int VentaId, string Numero, DateTime Fecha, decimal Total, decimal Pagado, decimal Saldo,
        // Cuando se agrupan comprobantes de varias sucursales con mismo CUIT, indicamos
        // de que cliente proviene cada uno para que el operador no se confunda.
        int? ClienteId = null, string? ClienteNombre = null,
        // 2026-06-16: tipo del comprobante (FA/FB/FC/NCA/NCB/NCC/X) — para el front render NCs en otro color y signo.
        string? TipoComprobante = null,
        // 2026-06-16: numero oficial ARCA (PtoVta + CbteNro). Si esta seteado, el front lo muestra abajo del numero interno.
        int? ArcaPtoVta = null, int? ArcaCbteNro = null);

    public record SucursalMismoCuitDto(int Id, string Nombre, string? Cuit);

    // 2026-06-06: ClienteId nullable + ClienteNombre puede venir del snapshot de la venta
    // cuando la cobranza es de una venta "ocasional" (sin cliente del catalogo).
    public record CobranzaListDto(
        int Id, string Numero, DateTime Fecha, int? ClienteId, string ClienteNombre,
        decimal Total, decimal Retenciones, string Estado,
        // 2026-06-19: numeros de venta imputados, para mostrar chips en el listado.
        List<string>? Comprobantes = null,
        // 2026-06-22: datos enriquecidos del cliente para el listado tipo "ficha".
        int? ClienteCodigo = null,
        string? ClienteFantasia = null,
        string? ClienteEntrega = null,
        // 2026-06-22: forma de pago resumida. Si hay 1 sola caja, el tipo de esa caja.
        // Si hay varias cajas distintas → "MIXTO". Si no hay medios → null.
        string? FormaPago = null,
        // Detalle textual para el tooltip cuando es MIXTO (ej: "Efectivo $50.000 · Transferencia $238.000").
        string? FormaPagoDetalle = null);

    public record CobranzaDetalleDto(
        int Id, string Numero, DateTime Fecha, int? ClienteId, string ClienteNombre,
        decimal Total, decimal Retenciones, string Estado, string? Operador, string? Observaciones,
        List<CobranzaComprobanteDto> Comprobantes, List<CobranzaMedioDto> Medios);

    public record CobranzaComprobanteDto(int Id, int? VentaId, string? VentaNumero, decimal Importe);
    public record CobranzaMedioDto(int Id, int CajaId, string CajaNombre, decimal Importe, string? Referencia, int? ChequeId);

    public record CrearCobranzaRequest(
        // 2026-06-06: ClienteId nullable para permitir cobrar "ventas ocasionales" (sin
        // cliente del catálogo). En ese caso debe venir al menos un comprobante con VentaId
        // y la venta apuntada tampoco debe tener ClienteId.
        int? ClienteId,
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

    // 2026-06-22: traduccion del Tipo de caja a label corto para el chip.
    private static string TipoToLabel(string tipo) => tipo?.ToUpperInvariant() switch
    {
        "EFECTIVO" => "Efectivo",
        "TRANSFERENCIA" => "Transfer.",
        "MERCADO_PAGO" or "MERCADOPAGO" or "MP" => "MercadoPago",
        "CHEQUES_CARTERA" or "CHEQUES" or "CHEQUE" => "Cheque",
        "TARJETA" or "TARJETA_CREDITO" or "DEBITO" or "CREDITO" => "Tarjeta",
        "BANCO" => "Banco",
        _ => string.IsNullOrWhiteSpace(tipo) ? "Otro" : char.ToUpper(tipo[0]) + tipo[1..].ToLower()
    };

    /// <summary>
    /// Devuelve los comprobantes (ventas) del cliente con saldo pendiente.
    /// Saldo = Total venta − suma de Importes en CobranzasComprobantes que apunten a esta venta.
    /// Si incluirMismoCuit=true, ademas trae los comprobantes de OTROS clientes que comparten
    /// el mismo CUIT (caso tipico: cliente con varias sucursales que paga global).
    /// </summary>
    [HttpGet("comprobantes-pendientes/{clienteId:int}")]
    public async Task<IActionResult> ComprobantesPendientes(int clienteId, [FromQuery] bool incluirMismoCuit = false)
    {
        // Determinar el set de clientes cuyos comprobantes vamos a traer.
        var clienteIds = new List<int> { clienteId };
        Dictionary<int, string> clienteNombres = new();
        if (incluirMismoCuit)
        {
            var clienteBase = await _db.CafeClientes
                .Where(c => c.Id == clienteId)
                .Select(c => new { c.Id, c.Nombre, c.Cuit })
                .FirstOrDefaultAsync();
            if (clienteBase is not null && !string.IsNullOrWhiteSpace(clienteBase.Cuit) && clienteBase.Cuit.Length >= 8)
            {
                var otrosIds = await _db.CafeClientes
                    .Where(c => c.Cuit == clienteBase.Cuit && c.Id != clienteId && c.IsActive)
                    .Select(c => new { c.Id, c.Nombre })
                    .ToListAsync();
                clienteIds.AddRange(otrosIds.Select(o => o.Id));
                foreach (var o in otrosIds) clienteNombres[o.Id] = o.Nombre;
            }
            // Para que aparezca el nombre del cliente base tambien al sortear/filtrar
            if (clienteBase is not null) clienteNombres[clienteBase.Id] = clienteBase.Nombre;
        }

        // Ventas del cliente (o los multiples con mismo CUIT). Traemos los campos para calcular
        // MontoCobrable (Total + ArcaImpTotal). Tambien traemos TipoComprobante para detectar NCs
        // (Notas de Credito) que van con signo negativo — el operador las imputa junto con la FA para compensar.
        var ventas = await _db.CafeVentas
            .Where(v => v.ClienteId != null && clienteIds.Contains(v.ClienteId!.Value) && v.Estado != "anulado")
            .Select(v => new { v.Id, v.Numero, v.Fecha, v.Total, v.ArcaImpTotal, v.ClienteId, v.TipoComprobante, v.ArcaPtoVta, v.ArcaCbteNro })
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
            .Select(v =>
            {
                // Monto real cobrable: ArcaImpTotal si la venta tiene CAE de ARCA, sino Total.
                var monto = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m)
                    ? v.ArcaImpTotal.Value : v.Total;
                // 2026-06-16: Las Notas de Credito van con signo NEGATIVO. Asi cuando el operador
                // tildea FA + NC en el mismo recibo, el total imputado da 0 y se compensan.
                var esNC = v.TipoComprobante is not null && v.TipoComprobante.StartsWith("NC", StringComparison.OrdinalIgnoreCase);
                var totalCobrar = esNC ? -monto : monto;
                var pagado = dict.TryGetValue(v.Id, out var p) ? p : 0m;
                var clienteNom = incluirMismoCuit && v.ClienteId.HasValue && clienteNombres.TryGetValue(v.ClienteId.Value, out var nm) ? nm : null;
                return new ComprobantePendienteDto(
                    v.Id, v.Numero ?? $"#{v.Id}", v.Fecha, totalCobrar, pagado, totalCobrar - pagado,
                    v.ClienteId, clienteNom, v.TipoComprobante, v.ArcaPtoVta, v.ArcaCbteNro);
            })
            // 2026-06-16: |Saldo| > 0.01 — antes filtraba solo positivos y se perdian las NC con saldo negativo (a compensar).
            .Where(x => Math.Abs(x.Saldo) > 0.01m)
            .OrderBy(x => x.Fecha)
            .ToList();

        return Ok(result);
    }

    /// <summary>Devuelve las OTRAS sucursales/clientes que comparten el mismo CUIT que el
    /// cliente dado. Vacio si el cliente no tiene CUIT o si nadie mas comparte ese CUIT.
    /// Sirve para que el modal de Cobranza muestre el checkbox "Incluir tambien las otras X sucursales".</summary>
    [HttpGet("sucursales-mismo-cuit/{clienteId:int}")]
    public async Task<IActionResult> SucursalesMismoCuit(int clienteId)
    {
        var cliente = await _db.CafeClientes
            .Where(c => c.Id == clienteId)
            .Select(c => new { c.Cuit })
            .FirstOrDefaultAsync();
        if (cliente is null || string.IsNullOrWhiteSpace(cliente.Cuit) || cliente.Cuit.Length < 8)
            return Ok(new List<SucursalMismoCuitDto>());
        var otras = await _db.CafeClientes
            .Where(c => c.Cuit == cliente.Cuit && c.Id != clienteId && c.IsActive)
            .OrderBy(c => c.Nombre)
            .Select(c => new SucursalMismoCuitDto(c.Id, c.Nombre, c.Cuit))
            .ToListAsync();
        return Ok(otras);
    }

    /// <summary>Lista cobranzas con filtros opcionales por cliente y rango de fechas.
    /// 2026-06-06: agregado parámetro `search` que filtra por nombre de cliente, snapshot
    /// de la venta o número de recibo. Cuando viene search, el take se eleva para evitar
    /// que cobranzas viejas queden fuera del corte de 200.</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int? clienteId,
        [FromQuery] DateTime? desde,
        [FromQuery] DateTime? hasta,
        [FromQuery] string? search = null,
        [FromQuery] int take = 200)
    {
        var q = _db.CafeCobranzas
            .Include(c => c.Cliente)
            .Include(c => c.Comprobantes).ThenInclude(cc => cc.Venta)
            .Include(c => c.Medios).ThenInclude(m => m.Caja)
            .AsQueryable();
        if (clienteId.HasValue) q = q.Where(c => c.ClienteId == clienteId.Value);
        if (desde.HasValue) q = q.Where(c => c.Fecha >= desde.Value);
        if (hasta.HasValue) q = q.Where(c => c.Fecha <= hasta.Value);
        // 2026-06-06: búsqueda libre por texto — busca en nombre cliente del catálogo,
        // snapshot del nombre en la venta del primer comprobante, o número de recibo.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(c =>
                (c.Cliente != null && c.Cliente.Nombre.Contains(s))
                || c.Numero.Contains(s)
                || c.Comprobantes.Any(cc => cc.Venta != null && cc.Venta.ClienteNombreSnapshot != null && cc.Venta.ClienteNombreSnapshot.Contains(s)));
            // Cuando hay búsqueda, abrimos el corte para que cobranzas viejas no se pierdan.
            if (take < 2000) take = 2000;
        }

        var rows = await q.OrderByDescending(c => c.Fecha).Take(take)
            .Select(c => new
            {
                c.Id, c.Numero, c.Fecha, c.ClienteId,
                ClienteNombreReal = c.Cliente != null ? c.Cliente.Nombre : null,
                ClienteCodigo = c.Cliente != null ? c.Cliente.CodigoInterno : null,
                ClienteFantasia = c.Cliente != null ? c.Cliente.RazonSocial : null,
                ClienteEntrega = c.Cliente != null ? c.Cliente.DomicilioEntrega : null,
                // 2026-06-06: si no hay cliente, tomamos el snapshot de la venta del primer comprobante.
                VentaSnapshot = c.Comprobantes
                    .Where(cc => cc.Venta != null)
                    .Select(cc => cc.Venta!.ClienteNombreSnapshot)
                    .FirstOrDefault(),
                // 2026-06-19: numeros de venta imputados (para chips en el listado).
                NumerosImputados = c.Comprobantes
                    .Where(cc => cc.Venta != null)
                    .Select(cc => cc.Venta!.Numero)
                    .ToList(),
                // 2026-06-22: medios para calcular forma de pago resumida.
                Medios = c.Medios.Select(m => new {
                    Tipo = m.Caja != null ? m.Caja.Tipo : "OTRO",
                    CajaNombre = m.Caja != null ? m.Caja.Nombre : "—",
                    m.Importe
                }).ToList(),
                c.Total, c.Retenciones, c.Estado
            })
            .ToListAsync();

        var list = rows.Select(r =>
        {
            // Calcular forma de pago resumida: 1 tipo → ese tipo, varios → MIXTO.
            string? formaPago = null;
            string? formaDetalle = null;
            if (r.Medios.Count > 0)
            {
                var tiposDistintos = r.Medios.Select(m => m.Tipo).Distinct().ToList();
                if (tiposDistintos.Count == 1)
                {
                    formaPago = TipoToLabel(tiposDistintos[0]);
                }
                else
                {
                    formaPago = "MIXTO";
                    formaDetalle = string.Join(" · ", r.Medios.Select(m =>
                        $"{TipoToLabel(m.Tipo)} ${m.Importe:N0}"));
                }
            }
            return new CobranzaListDto(
                r.Id, r.Numero, r.Fecha, r.ClienteId,
                r.ClienteNombreReal ?? (!string.IsNullOrWhiteSpace(r.VentaSnapshot) ? r.VentaSnapshot + " (ocasional)" : "—"),
                r.Total, r.Retenciones, r.Estado,
                r.NumerosImputados.Distinct().OrderBy(n => n).ToList(),
                r.ClienteCodigo,
                string.IsNullOrWhiteSpace(r.ClienteFantasia) ? null : r.ClienteFantasia,
                string.IsNullOrWhiteSpace(r.ClienteEntrega) ? null : r.ClienteEntrega,
                formaPago,
                formaDetalle);
        }).ToList();
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

    /// <summary>Crea una cobranza nueva. Valida coherencia: suma de medios + retenciones = suma de comprobantes.
    /// 2026-06-06: ClienteId puede ser null si la cobranza es de una venta ocasional (sin cliente del catalogo).</summary>
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearCobranzaRequest req)
    {
        // Validaciones basicas
        CafeCliente? cliente = null;
        if (req.ClienteId.HasValue && req.ClienteId.Value > 0)
        {
            cliente = await _db.CafeClientes.FindAsync(req.ClienteId.Value);
            if (cliente is null) return BadRequest(new { error = "Cliente no encontrado" });
        }
        else
        {
            // Sin cliente: tiene que haber al menos un comprobante con VentaId, y esa venta
            // tampoco debe tener cliente (es decir, una venta "ocasional"). No permitimos
            // cobranzas "a cuenta" sin cliente porque no habria a quien acreditar el saldo.
            var ventaIdsRef = (req.Comprobantes ?? new()).Where(c => c.VentaId.HasValue).Select(c => c.VentaId!.Value).Distinct().ToList();
            if (ventaIdsRef.Count == 0)
                return BadRequest(new { error = "Una cobranza sin cliente debe estar asociada a una venta puntual." });
            var ventasRef = await _db.CafeVentas.Where(v => ventaIdsRef.Contains(v.Id))
                .Select(v => new { v.Id, v.ClienteId, v.ClienteNombreSnapshot })
                .ToListAsync();
            var alguna = ventasRef.FirstOrDefault(v => v.ClienteId.HasValue);
            if (alguna is not null)
                return BadRequest(new { error = $"La venta #{alguna.Id} ya tiene cliente asignado — usá ese cliente para cobrar." });
        }
        if (req.Comprobantes == null || req.Comprobantes.Count == 0)
            return BadRequest(new { error = "Hay que cobrar al menos un comprobante (o agregar como 'a cuenta')" });

        var sumComprobantes = req.Comprobantes.Sum(c => c.Importe);
        var sumMedios = (req.Medios ?? new()).Sum(m => m.Importe);
        var retenciones = Math.Max(0m, req.Retenciones);

        // 2026-06-16: si la suma de comprobantes da 0 (caso tipico: FA + NC que se compensan),
        // permitimos guardar sin forma de cobro porque no entra plata a caja, solo se imputan los comprobantes entre si.
        if (Math.Abs(sumComprobantes) > 0.01m && (req.Medios == null || req.Medios.Count == 0))
            return BadRequest(new { error = "Hay que especificar al menos una forma de cobro" });

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
            // 2026-06-06: ClienteId puede quedar null para cobranzas ocasionales (venta sin cliente)
            ClienteId = (req.ClienteId.HasValue && req.ClienteId.Value > 0) ? req.ClienteId.Value : null,
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

        // 2026-06-06: cliente puede ser null (venta ocasional). Usamos el snapshot de la venta para el audit log.
        var clienteAudit = cliente?.Nombre;
        if (clienteAudit is null)
        {
            var ventaIdRef = req.Comprobantes.FirstOrDefault(c => c.VentaId.HasValue)?.VentaId;
            if (ventaIdRef.HasValue)
            {
                var snap = await _db.CafeVentas.Where(v => v.Id == ventaIdRef.Value)
                    .Select(v => v.ClienteNombreSnapshot).FirstOrDefaultAsync();
                clienteAudit = string.IsNullOrWhiteSpace(snap) ? "(ocasional sin nombre)" : $"{snap} (ocasional)";
            }
            else clienteAudit = "(ocasional)";
        }
        await _audit.LogAsync("CafeCobranza", cobranza.Id.ToString(), "CREATE",
            $"Cobranza {numero} para cliente {clienteAudit}, total ${sumMedios:N2}");

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
            // Usamos MontoCobrable (Total con IVA si es factura ARCA) para que IsPaid sea correcto.
            var totalCobrar = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
            v.IsPaid = pagado >= totalCobrar - 0.01m;
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

    public record EditarImputacionesRequest(List<CrearComprobanteItem> Comprobantes);

    /// <summary>
    /// Re-imputa una cobranza VIGENTE: borra todas las filas de Cafe_CobranzasComprobantes
    /// y las re-crea con los nuevos importes. NO toca medios de cobro, cheques, cliente ni total.
    /// La suma de los nuevos importes debe igualar c.Total + c.Retenciones (lo mismo que valida el Crear).
    /// Sincroniza IsPaid de las ventas afectadas (las viejas y las nuevas).
    /// Caso de uso: corregir una imputacion mal hecha (ej. monto que quedo "a cuenta" cuando debio
    /// ir a una factura, o viceversa) sin tener que anular y rehacer todo.
    /// </summary>
    [HttpPut("{id:int}/imputaciones")]
    public async Task<IActionResult> EditarImputaciones(int id, [FromBody] EditarImputacionesRequest req)
    {
        var c = await _db.CafeCobranzas
            .Include(x => x.Comprobantes)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound();
        if (c.Estado != "VIGENTE")
            return BadRequest(new { error = "Solo se pueden editar cobranzas VIGENTES" });
        if (req.Comprobantes == null || req.Comprobantes.Count == 0)
            return BadRequest(new { error = "Tiene que haber al menos una imputacion" });

        var sumNuevo = req.Comprobantes.Sum(x => x.Importe);
        var esperado = c.Total + c.Retenciones;
        if (Math.Abs(sumNuevo - esperado) > 0.01m)
            return BadRequest(new { error = $"No cuadra: imputado ${sumNuevo:N2} vs total+retenciones ${esperado:N2}" });

        // Validar ventas referenciadas: existan y pertenezcan al cliente de la cobranza (o a sucursales con mismo CUIT).
        var ventaIdsNuevas = req.Comprobantes.Where(x => x.VentaId.HasValue).Select(x => x.VentaId!.Value).Distinct().ToList();
        if (ventaIdsNuevas.Count > 0)
        {
            var ventas = await _db.CafeVentas.Where(v => ventaIdsNuevas.Contains(v.Id))
                .Select(v => new { v.Id, v.ClienteId }).ToListAsync();
            if (ventas.Count != ventaIdsNuevas.Count)
                return BadRequest(new { error = "Alguna venta referenciada no existe" });
            if (c.ClienteId.HasValue)
            {
                var cuitBase = await _db.CafeClientes.Where(cl => cl.Id == c.ClienteId.Value)
                    .Select(cl => cl.Cuit).FirstOrDefaultAsync();
                List<int> clientesValidos = new() { c.ClienteId.Value };
                if (!string.IsNullOrWhiteSpace(cuitBase))
                    clientesValidos = await _db.CafeClientes
                        .Where(cl => cl.Cuit == cuitBase).Select(cl => cl.Id).ToListAsync();
                var noPertenece = ventas.FirstOrDefault(v => !v.ClienteId.HasValue || !clientesValidos.Contains(v.ClienteId.Value));
                if (noPertenece is not null)
                    return BadRequest(new { error = $"La venta #{noPertenece.Id} no pertenece a este cliente" });
            }
        }

        // Ventas afectadas: las que tenia antes + las nuevas (para resincronizar IsPaid en ambas)
        var ventaIdsViejas = c.Comprobantes.Where(cc => cc.VentaId.HasValue).Select(cc => cc.VentaId!.Value).Distinct().ToList();
        var todasLasVentas = ventaIdsViejas.Concat(ventaIdsNuevas).Distinct().ToList();

        _db.CafeCobranzasComprobantes.RemoveRange(c.Comprobantes);
        foreach (var comp in req.Comprobantes)
        {
            _db.CafeCobranzasComprobantes.Add(new CafeCobranzaComprobante
            {
                CobranzaId = c.Id,
                VentaId = comp.VentaId,
                Importe = comp.Importe
            });
        }
        c.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await SincronizarIsPaidAsync(todasLasVentas);

        await _audit.LogAsync("CafeCobranza", id.ToString(), "EDIT_IMPUTACIONES",
            $"Cobranza {c.Numero}: re-imputada en {req.Comprobantes.Count} items, suma ${sumNuevo:N2}");
        return Ok(new { ok = true });
    }
}
