using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>Cálculo centralizado de "cuánto debe cada cliente" (cuenta corriente).
/// Extraído de CafeClientesController.GetSaldosPendientes para poder reusarlo desde el
/// aviso diario de Telegram (DeudoresDiarioService) SIN duplicar la fórmula.
///
/// Saldo pendiente = SUM(ventas emitidas cobrables) - SUM(cobranzas vigentes asignadas).
/// El monto cobrable de cada venta es ArcaImpTotal (con IVA) si tiene CAE, sino Total (neto).</summary>
public class CafeSaldosService
{
    private readonly AppDbContext _db;
    public CafeSaldosService(AppDbContext db) => _db = db;

    /// <summary>Lista TODOS los clientes con saldo pendiente (deudores), agrupados y ordenados
    /// por la venta más antigua primero. Solo devuelve clientes con saldo > 0.</summary>
    public async Task<List<ClienteSaldoPendienteDto>> GetSaldosPendientesAsync()
    {
        // Traer todas las ventas emitidas (no anuladas) con cliente y total > 0.
        // Se excluyen PRESUPUESTOS (PRO) y Notas de Crédito (NC*): no son deuda.
        var ventas = await _db.CafeVentas
            .Where(v => v.Estado != "anulado"
                     && v.ClienteId != null
                     && v.Total > 0
                     && v.TipoComprobante != "PRO"
                     && (v.TipoComprobante == null || !v.TipoComprobante.StartsWith("NC")))
            .Select(v => new {
                v.Id, ClienteId = v.ClienteId!.Value, v.Total, v.ArcaImpTotal, v.Fecha, v.TipoComprobante,
                EsSaldoMigracion = _db.CafeSaldosMigracion.Any(s => s.VentaId == v.Id)
            })
            .ToListAsync();
        if (ventas.Count == 0) return new List<ClienteSaldoPendienteDto>();

        // Pagos por venta (cobranzas VIGENTES).
        var ventaIds = ventas.Select(v => v.Id).ToList();
        var pagados = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId != null && ventaIds.Contains(c.VentaId!.Value)
                     && c.Cobranza!.Estado == "VIGENTE")
            .GroupBy(c => c.VentaId!.Value)
            .Select(g => new { VentaId = g.Key, Pagado = g.Sum(x => x.Importe) })
            .ToListAsync();
        var pagadosDict = pagados.ToDictionary(p => p.VentaId, p => p.Pagado);

        // Saldo de cada venta. Monto cobrable: ArcaImpTotal (con IVA) si tiene CAE, sino Total.
        var ventasConSaldo = ventas.Select(v =>
        {
            var totalCobrar = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
            return new {
                v.Id, v.ClienteId, Total = totalCobrar, v.Fecha, v.TipoComprobante, v.EsSaldoMigracion,
                Saldo = totalCobrar - (pagadosDict.TryGetValue(v.Id, out var p) ? p : 0m)
            };
        // Saldo > 0.50: ignora diferencias menores a medio peso (redondeo entre Total y ArcaImpTotal+IVA).
        }).Where(v => v.Saldo > 0.50m).ToList();
        if (ventasConSaldo.Count == 0) return new List<ClienteSaldoPendienteDto>();

        // Agrupar por cliente.
        var clienteIds = ventasConSaldo.Select(v => v.ClienteId).Distinct().ToList();
        var clientes = await _db.CafeClientes
            .Where(c => clienteIds.Contains(c.Id))
            .ToListAsync();
        var clientesDict = clientes.ToDictionary(c => c.Id);

        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        return ventasConSaldo
            .GroupBy(v => v.ClienteId)
            .Select(g =>
            {
                clientesDict.TryGetValue(g.Key, out var cli);
                var fechaMasAntigua = g.Min(x => x.Fecha);
                // "Cotizacion" = X + PRO (no fiscal); "Factura" = FA + FB + FC (fiscal, con CAE).
                var saldoCotizacion = g.Where(x => x.TipoComprobante == "X" || x.TipoComprobante == "PRO")
                    .Sum(x => x.Saldo);
                var saldoFactura = g.Where(x => x.TipoComprobante == "FA" || x.TipoComprobante == "FB" || x.TipoComprobante == "FC")
                    .Sum(x => x.Saldo);
                return new ClienteSaldoPendienteDto(
                    g.Key,
                    cli?.Nombre ?? "(sin nombre)",
                    cli?.Tipo,
                    cli?.Telefono,
                    cli?.MapeoLink,
                    cli?.CodigoInterno,
                    g.Count(),
                    g.Sum(x => x.Saldo),
                    fechaMasAntigua,
                    (int)(hoy - fechaMasAntigua.Date).TotalDays,
                    g.Any(x => x.EsSaldoMigracion),
                    saldoCotizacion,
                    saldoFactura,
                    cli?.Cuit
                );
            })
            .OrderBy(c => c.FechaMasAntigua) // más antigua primero (mayor urgencia)
            .ToList();
    }
}

/// <summary>Un cliente deudor con su saldo pendiente consolidado.</summary>
public record ClienteSaldoPendienteDto(
    int ClienteId, string Nombre, string? Tipo, string? Telefono, string? MapeoLink,
    int? CodigoInterno,
    int CantidadVentasPendientes,
    decimal SaldoPendiente,
    DateTime FechaMasAntigua, int DiasMasAntigua,
    bool TieneSaldoMigracion,
    /// <summary>Saldo de comprobantes tipo X y PRO (no fiscales). Default 0 si no hay.</summary>
    decimal SaldoCotizacion = 0m,
    /// <summary>Saldo de comprobantes tipo FA, FB, FC (con CAE de ARCA, fiscales). Default 0 si no hay.</summary>
    decimal SaldoFactura = 0m,
    /// <summary>CUIT del cliente. Sirve para agrupar cuentas del mismo CUIT en el aviso de deudas.</summary>
    string? Cuit = null);
