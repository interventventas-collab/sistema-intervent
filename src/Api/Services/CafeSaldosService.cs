using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>Cálculo centralizado de "cuánto debe cada cliente" (cuenta corriente).
/// ÚNICA fuente de verdad: la usan el panel "¿Quién me debe?", el Excel de saldos,
/// el aviso diario de Telegram, el bot de empleados, la ficha del cliente y la
/// tarjeta de cliente del chat de WhatsApp. Si hay que tocar la fórmula, se toca ACÁ.
///
/// Saldo del cliente = (ventas cobrables − notas de crédito) − (todo lo cobrado).
///
/// Detalles que ANTES estaban mal (2026-08-21) y acá quedaron resueltos:
///  1. Los pagos "a cuenta" (cobranza sin factura asignada) NO se descontaban.
///  2. Las notas de crédito NO descontaban: la factura anulada seguía figurando como deuda.
///  3. Si a una factura le imputaron de más, ese saldo a favor se tiraba a la basura
///     en vez de compensarlo contra el resto de la deuda del mismo cliente.
///  4. La ficha del cliente usaba OTRA fórmula (acreditaba la cobranza al cliente que
///     pagaba, aunque el pago cancelara facturas de una sucursal hermana), así que el
///     panel y la ficha mostraban números distintos para el mismo cliente.
///
/// Convención de signos: una NC entra como movimiento NEGATIVO y sus imputaciones también,
/// así la compensación "FA + NC en una misma cobranza" no descuenta dos veces.
/// El monto cobrable de cada venta es ArcaImpTotal (con IVA) si tiene CAE, sino Total.</summary>
public class CafeSaldosService
{
    private readonly AppDbContext _db;
    public CafeSaldosService(AppDbContext db) => _db = db;

    /// <summary>Diferencias de menos de medio peso son redondeo, no deuda.</summary>
    public const decimal Umbral = 0.50m;

    /// <summary>Una venta con lo que se le imputó de cobranzas vigentes.</summary>
    public sealed class VentaCuenta
    {
        public int Id { get; init; }
        public int? ClienteId { get; init; }
        /// <summary>Nombre que quedó guardado en la venta. Lo usan las ventas "ocasionales" (sin cliente del catálogo).</summary>
        public string? ClienteNombreSnapshot { get; init; }
        public string Numero { get; init; } = "";
        public DateTime Fecha { get; init; }
        public string? TipoComprobante { get; init; }
        /// <summary>Lo que el cliente tiene que pagar por esta venta (con IVA si es factura ARCA).</summary>
        public decimal Cobrable { get; init; }
        /// <summary>Suma de imputaciones de cobranzas VIGENTES a esta venta.</summary>
        public decimal Pagado { get; init; }
        public bool EsNotaCredito { get; init; }
        /// <summary>La factura fue anulada con una nota de crédito.</summary>
        public bool AnuladaPorNc { get; init; }
        public bool EsSaldoMigracion { get; init; }

        /// <summary>Lo que este comprobante mueve en la cuenta: las NC restan.</summary>
        public decimal Movimiento => EsNotaCredito ? -Cobrable : Cobrable;
        /// <summary>Saldo firmado. Positivo = deuda, negativo = plata a favor del cliente.</summary>
        public decimal Saldo => Movimiento - Pagado;
        /// <summary>Comprobante que sigue pendiente de cobro de verdad (no una NC ni una
        /// factura ya anulada por NC).</summary>
        public bool Pendiente => !EsNotaCredito && !AnuladaPorNc && Saldo > Umbral;
    }

    /// <summary>Un movimiento de la cuenta corriente (venta al debe, NC/cobranza al haber).</summary>
    public record MovimientoCuenta(DateTime Fecha, string Tipo, string Numero, decimal Debe, decimal Haber, string? Detalle);

    // ────────────────────────────── núcleo ──────────────────────────────

    /// <summary>Trae las ventas que juegan en cuenta corriente (no anuladas, sin presupuestos)
    /// con lo cobrado de cada una. clienteId null = todas.</summary>
    public async Task<List<VentaCuenta>> GetVentasCuentaAsync(int? clienteId = null, bool soloSinCliente = false)
    {
        var q = _db.CafeVentas.Where(v => v.Estado != "anulado" && v.TipoComprobante != "PRO");
        if (soloSinCliente) q = q.Where(v => v.ClienteId == null);
        else if (clienteId.HasValue) q = q.Where(v => v.ClienteId == clienteId.Value);
        else q = q.Where(v => v.ClienteId != null);

        var ventas = await q
            .Select(v => new
            {
                v.Id, v.ClienteId, v.ClienteNombreSnapshot, v.Numero, v.Fecha, v.TipoComprobante, v.Total, v.ArcaImpTotal,
                v.NotaCreditoVentaId,
                EsSaldoMigracion = _db.CafeSaldosMigracion.Any(s => s.VentaId == v.Id)
            })
            .ToListAsync();
        if (ventas.Count == 0) return new List<VentaCuenta>();

        // Imputaciones de cobranzas VIGENTES, agrupadas por venta. Se filtra por las condiciones
        // de la venta (no por una lista de ids) para no armar un IN gigante.
        var pagados = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId != null && c.Cobranza!.Estado == "VIGENTE"
                     && c.Venta!.Estado != "anulado" && c.Venta.TipoComprobante != "PRO")
            .GroupBy(c => c.VentaId!.Value)
            .Select(g => new { VentaId = g.Key, Pagado = g.Sum(x => x.Importe) })
            .ToListAsync();
        var pagadosDict = pagados.ToDictionary(p => p.VentaId, p => p.Pagado);

        return ventas.Select(v => new VentaCuenta
        {
            Id = v.Id,
            ClienteId = v.ClienteId,
            ClienteNombreSnapshot = v.ClienteNombreSnapshot,
            Numero = v.Numero ?? $"#{v.Id}",
            Fecha = v.Fecha,
            TipoComprobante = v.TipoComprobante,
            Cobrable = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total,
            Pagado = pagadosDict.TryGetValue(v.Id, out var p) ? p : 0m,
            EsNotaCredito = EsNc(v.TipoComprobante),
            AnuladaPorNc = v.NotaCreditoVentaId.HasValue,
            EsSaldoMigracion = v.EsSaldoMigracion
        }).ToList();
    }

    /// <summary>Pagos "a cuenta" (cobranzas sin factura asignada) por cliente. Son plata cobrada:
    /// descuentan deuda aunque todavía no se sepa a qué comprobante van.</summary>
    public async Task<Dictionary<int, decimal>> GetPagosACuentaAsync()
    {
        var rows = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId == null && c.Cobranza!.Estado == "VIGENTE" && c.Cobranza.ClienteId != null)
            .GroupBy(c => c.Cobranza!.ClienteId!.Value)
            .Select(g => new { ClienteId = g.Key, Importe = g.Sum(x => x.Importe) })
            .ToListAsync();
        return rows.ToDictionary(r => r.ClienteId, r => r.Importe);
    }

    private static bool EsNc(string? tipo) =>
        tipo is not null && tipo.StartsWith("NC", StringComparison.OrdinalIgnoreCase);

    // ────────────────────── panel "¿Quién me debe?" ──────────────────────

    /// <summary>Lista TODOS los clientes con saldo pendiente (deudores), ordenados por la venta
    /// más antigua primero. Solo devuelve clientes que realmente deben plata (saldo neto > 0),
    /// ya descontados los pagos a cuenta, las notas de crédito y los saldos a favor.</summary>
    public async Task<List<ClienteSaldoPendienteDto>> GetSaldosPendientesAsync()
    {
        var ventas = await GetVentasCuentaAsync();
        if (ventas.Count == 0) return new List<ClienteSaldoPendienteDto>();
        var aCuenta = await GetPagosACuentaAsync();

        // Clientes con ventas + clientes que solo tienen pagos a cuenta (por si alguno
        // quedó con crédito suelto: no van a aparecer, pero el cálculo los contempla).
        var grupos = ventas.Where(v => v.ClienteId.HasValue)
            .GroupBy(v => v.ClienteId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        var resumen = new List<(int ClienteId, decimal Neto, decimal Bruto, decimal Credito,
            decimal Cot, decimal Fac, DateTime Fecha, int Cantidad, bool Migracion)>();

        foreach (var (clienteId, lista) in grupos)
        {
            var pendientes = lista.Where(v => v.Pendiente).ToList();
            // Bruto = lo que figura pendiente comprobante por comprobante.
            var bruto = pendientes.Sum(v => v.Saldo);
            // Neto = todo lo que mueve la cuenta (incluye NC y saldos a favor) menos los pagos a cuenta.
            var neto = lista.Sum(v => v.Saldo) - (aCuenta.TryGetValue(clienteId, out var ac) ? ac : 0m);
            if (neto <= Umbral) continue; // no debe nada (o tiene plata a favor)

            var fecha = pendientes.Count > 0 ? pendientes.Min(v => v.Fecha) : lista.Min(v => v.Fecha);
            resumen.Add((
                clienteId,
                neto,
                bruto,
                Math.Max(0m, bruto - neto), // lo que se le descuenta: a cuenta + NC + saldos a favor
                pendientes.Where(v => v.TipoComprobante == "X" || v.TipoComprobante == "PRO").Sum(v => v.Saldo),
                pendientes.Where(v => v.TipoComprobante is "FA" or "FB" or "FC").Sum(v => v.Saldo),
                fecha,
                pendientes.Count,
                pendientes.Any(v => v.EsSaldoMigracion)
            ));
        }
        if (resumen.Count == 0) return new List<ClienteSaldoPendienteDto>();

        var clienteIds = resumen.Select(r => r.ClienteId).ToList();
        var clientes = await _db.CafeClientes.Where(c => clienteIds.Contains(c.Id)).ToListAsync();
        var clientesDict = clientes.ToDictionary(c => c.Id);

        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        return resumen
            .Select(r =>
            {
                clientesDict.TryGetValue(r.ClienteId, out var cli);
                return new ClienteSaldoPendienteDto(
                    r.ClienteId,
                    cli?.Nombre ?? "(sin nombre)",
                    cli?.Tipo,
                    cli?.Telefono,
                    cli?.MapeoLink,
                    cli?.CodigoInterno,
                    r.Cantidad,
                    r.Neto,
                    r.Fecha,
                    (int)(hoy - r.Fecha.Date).TotalDays,
                    r.Migracion,
                    r.Cot,
                    r.Fac,
                    cli?.Cuit,
                    r.Credito
                );
            })
            .OrderBy(c => c.FechaMasAntigua) // más antigua primero (mayor urgencia)
            .ToList();
    }

    // ─────────────────── cuenta corriente de UN cliente ───────────────────

    /// <summary>Movimientos de la cuenta corriente de un cliente, en orden cronológico.
    /// Debe = ventas. Haber = notas de crédito + lo cobrado.
    ///
    /// Lo cobrado se arma con las IMPUTACIONES (a qué comprobante se aplicó la plata), no con
    /// el total de las cobranzas del cliente. Es la diferencia clave con la versión vieja: cuando
    /// una empresa paga de una sola vez y reparte entre sus sucursales, cada sucursal ve el pago
    /// que le corresponde en lugar de que se lo lleve todo la que hizo la transferencia.</summary>
    public async Task<List<MovimientoCuenta>> GetMovimientosClienteAsync(int clienteId)
    {
        var ventas = await _db.CafeVentas
            .Where(v => v.ClienteId == clienteId && v.Estado != "anulado" && v.TipoComprobante != "PRO")
            .Select(v => new { v.Id, v.Fecha, v.Numero, v.Total, v.ArcaImpTotal, v.TipoComprobante })
            .ToListAsync();

        // Imputaciones que tocan a este cliente: las aplicadas a SUS ventas (venga la cobranza
        // de donde venga) + las que quedaron "a cuenta" en SU cuenta.
        var imputaciones = await _db.CafeCobranzasComprobantes
            .Where(cc => cc.Cobranza!.Estado == "VIGENTE"
                && ((cc.VentaId != null && cc.Venta!.ClienteId == clienteId
                     && cc.Venta.Estado != "anulado" && cc.Venta.TipoComprobante != "PRO")
                    || (cc.VentaId == null && cc.Cobranza.ClienteId == clienteId)))
            .Select(cc => new
            {
                cc.CobranzaId,
                Numero = cc.Cobranza!.Numero,
                cc.Cobranza.Fecha,
                CobranzaClienteId = cc.Cobranza.ClienteId,
                CobranzaClienteNombre = cc.Cobranza.Cliente != null ? cc.Cobranza.Cliente.Nombre : null,
                cc.Importe,
                EsACuenta = cc.VentaId == null
            })
            .ToListAsync();

        var movs = new List<MovimientoCuenta>();
        foreach (var v in ventas)
        {
            var monto = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
            // Las Notas de Credito (NCA/NCB/NCC) son DEVOLUCION al cliente — van al HABER, no al DEBE.
            if (EsNc(v.TipoComprobante))
                movs.Add(new MovimientoCuenta(v.Fecha, "Nota Crédito", v.Numero ?? $"#{v.Id}", 0m, monto, null));
            else
                movs.Add(new MovimientoCuenta(v.Fecha, "Venta", v.Numero ?? $"#{v.Id}", monto, 0m, null));
        }
        foreach (var g in imputaciones.GroupBy(x => x.CobranzaId))
        {
            var first = g.First();
            var notas = new List<string>();
            if (g.Any(x => x.EsACuenta)) notas.Add("a cuenta");
            if (first.CobranzaClienteId.HasValue && first.CobranzaClienteId.Value != clienteId)
                notas.Add($"pagado desde la cuenta de {first.CobranzaClienteNombre ?? "otro cliente"}");
            movs.Add(new MovimientoCuenta(first.Fecha, "Cobranza", first.Numero, 0m, g.Sum(x => x.Importe),
                notas.Count > 0 ? "(" + string.Join(" · ", notas) + ")" : null));
        }
        return movs.OrderBy(m => m.Fecha).ToList();
    }

    /// <summary>Lo que debe UN cliente hoy. Mismo número que muestra el panel "¿Quién me debe?".</summary>
    public async Task<decimal> GetSaldoClienteAsync(int clienteId)
        => (await GetMovimientosClienteAsync(clienteId)).Sum(m => m.Debe - m.Haber);
}

/// <summary>Un cliente deudor con su saldo pendiente consolidado.</summary>
public record ClienteSaldoPendienteDto(
    int ClienteId, string Nombre, string? Tipo, string? Telefono, string? MapeoLink,
    int? CodigoInterno,
    int CantidadVentasPendientes,
    /// <summary>Lo que el cliente debe DE VERDAD: comprobantes pendientes menos pagos a cuenta,
    /// notas de crédito y saldos a favor.</summary>
    decimal SaldoPendiente,
    DateTime FechaMasAntigua, int DiasMasAntigua,
    bool TieneSaldoMigracion,
    /// <summary>Saldo de comprobantes tipo X y PRO (no fiscales). Default 0 si no hay.</summary>
    decimal SaldoCotizacion = 0m,
    /// <summary>Saldo de comprobantes tipo FA, FB, FC (con CAE de ARCA, fiscales). Default 0 si no hay.</summary>
    decimal SaldoFactura = 0m,
    /// <summary>CUIT del cliente. Sirve para agrupar cuentas del mismo CUIT en el aviso de deudas.</summary>
    string? Cuit = null,
    /// <summary>Plata del cliente que NO está aplicada a los comprobantes pendientes: pagos a cuenta,
    /// notas de crédito sin usar y facturas pagadas de más. Cotización + Factura − esto = SaldoPendiente.</summary>
    decimal CreditoAFavor = 0m);
