using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 17/11/2026 — Lo que hace solo el sistema con los pagos PROGRAMADOS a proveedores:
/// 1) "Todos los meses": cuando el de este mes queda pagado (o se saltea), crea el del mes siguiente
///    (si cae sábado o domingo, pasa al lunes). Si va con "lo de su última factura", el importe se toma
///    de la última factura de AFIP que nos hizo ese proveedor.
/// 2) Cruce con el extracto del banco: una transferencia al CUIT del proveedor por el MISMO importe,
///    cerca de la fecha programada, registra el pago de verdad (Tesorería, caja del banco) y lo deja PAGADO.
///    Si el importe no coincide, queda como sugerencia para que el dueño decida ("Tomarlo").
/// </summary>
public class PagosProgramadosService
{
    private readonly AppDbContext _db;
    private readonly ProveedorCtaCteService _ctacte;
    private readonly ILogger<PagosProgramadosService> _log;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>Ventana en la que una transferencia del banco puede ser de un pago programado.</summary>
    public const int DiasAntes = 12, DiasDespues = 25;

    public PagosProgramadosService(AppDbContext db, ProveedorCtaCteService ctacte, ILogger<PagosProgramadosService> log)
    { _db = db; _ctacte = ctacte; _log = log; }

    public static DateTime HoyAr => DateTime.UtcNow.AddHours(-3).Date;

    /// <summary>El día del mes pedido (o el último si el mes es más corto); finde → lunes.</summary>
    public static DateTime FechaDelMes(int anio, int mes, int dia)
    {
        var d = new DateTime(anio, mes, Math.Min(Math.Max(dia, 1), DateTime.DaysInMonth(anio, mes)));
        while (d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) d = d.AddDays(1);
        return d;
    }

    /// <summary>Primera fecha de la serie desde hoy (este mes si todavía no pasó, si no el que viene).</summary>
    public static DateTime PrimeraFecha(int dia, DateTime desde)
    {
        var f = FechaDelMes(desde.Year, desde.Month, dia);
        if (f < desde.Date) { var m = desde.AddMonths(1); f = FechaDelMes(m.Year, m.Month, dia); }
        return f;
    }

    public static DateTime SiguienteFecha(DateTime anterior, int dia)
    {
        // "anterior" pudo correrse al lunes y quedar en el mes siguiente (ej. 31 → 1°): se cuenta desde el día pedido.
        var baseMes = anterior.Day < Math.Min(dia, 28) && anterior.Day <= 3 ? anterior.AddMonths(-1) : anterior;
        var m = baseMes.AddMonths(1);
        return FechaDelMes(m.Year, m.Month, dia);
    }

    public record UltimaFactura(string Clave, string Texto, DateTime Fecha, decimal Total);

    /// <summary>Última factura de AFIP (no nota de crédito) que nos hizo ese CUIT, emitida antes de "hasta".</summary>
    public async Task<UltimaFactura?> UltimaFacturaAsync(string? cuit, DateTime? hasta = null)
    {
        var c = ProveedorCtaCteService.NormCuit(cuit);
        if (c.Length == 0) return null;
        var q = _db.ContadoraComprobantes.AsNoTracking()
            .Where(x => x.Naturaleza == "COMPRA" && x.ReceptorDoc == c && !x.EsNotaCredito && x.FechaEmision != null && x.Total > 0);
        if (hasta.HasValue) { var h = hasta.Value.Date.AddDays(1); q = q.Where(x => x.FechaEmision < h); }
        var f = await q.OrderByDescending(x => x.FechaEmision).ThenByDescending(x => x.NumeroComprobante)
            .Select(x => new { x.IdComprobante, x.TipoComprobante, x.PuntoVenta, x.NumeroComprobante, x.FechaEmision, x.Total })
            .FirstOrDefaultAsync();
        if (f is null) return null;
        var tipo = string.IsNullOrWhiteSpace(f.TipoComprobante) ? "Factura" : f.TipoComprobante!;
        return new UltimaFactura(ProveedorCtaCteService.ClaveAfip(f.IdComprobante),
            $"{tipo} {ProveedorCtaCteService.NumeroAfip(f.PuntoVenta, f.NumeroComprobante)}", f.FechaEmision!.Value.Date, f.Total);
    }

    /// <summary>Pone el importe (y la factura a cancelar, si lleva cuenta corriente) de la última factura.</summary>
    private async Task AplicarUltimaFacturaAsync(CafePagoProgramado p, CafeProveedor prov)
    {
        var uf = await UltimaFacturaAsync(prov.Cuit, p.Fecha);
        if (uf is null) return;
        p.Importe = Math.Round(uf.Total, 2);
        p.Claves = prov.CuentaCorriente ? uf.Clave : null;
    }

    /// <summary>Crea el pago del mes siguiente de cada serie que ya no tiene uno pendiente, y refresca
    /// el importe "lo de su última factura" de los pendientes que nadie tocó a mano.</summary>
    public async Task<int> MantenerAsync()
    {
        var series = await _db.CafePagosProgramados.Include(p => p.Proveedor)
            .Where(p => p.SerieId != null)
            .ToListAsync();
        var creados = 0;
        foreach (var g in series.GroupBy(p => p.SerieId!.Value))
        {
            var ultimo = g.OrderByDescending(p => p.Fecha).ThenByDescending(p => p.Id).First();
            if (ultimo.Estado == "PENDIENTE")
            {
                if (ultimo.ImporteUltimaFactura && ultimo.UpdatedAt == null && ultimo.TransferidoAt == null && ultimo.Proveedor != null)
                    await AplicarUltimaFacturaAsync(ultimo, ultimo.Proveedor);
                continue;
            }
            if (ultimo.DiaMes is null) continue;   // se dejó de repetir
            var nuevo = new CafePagoProgramado
            {
                ProveedorId = ultimo.ProveedorId, Fecha = SiguienteFecha(ultimo.Fecha, ultimo.DiaMes.Value),
                Importe = ultimo.Importe, Medio = ultimo.Medio, Nota = ultimo.Nota, DiaMes = ultimo.DiaMes,
                SerieId = ultimo.SerieId, ImporteUltimaFactura = ultimo.ImporteUltimaFactura,
                Claves = null,   // las facturas del mes pasado ya no corren; con "última factura" se ponen abajo
                Operador = "sistema (todos los meses)",
            };
            if (nuevo.ImporteUltimaFactura && ultimo.Proveedor != null) await AplicarUltimaFacturaAsync(nuevo, ultimo.Proveedor);
            _db.CafePagosProgramados.Add(nuevo);
            creados++;
        }
        if (_db.ChangeTracker.HasChanges()) await _db.SaveChangesAsync();
        return creados;
    }

    public record MovBanco(int Id, DateTime Fecha, decimal Importe, string? Leyenda);

    /// <summary>Transferencias del banco que todavía no son pago de nadie.</summary>
    private async Task<HashSet<int>> MovsUsadosAsync()
    {
        var usados = (await _db.CafePagosProveedor.Where(p => p.Estado == "VIGENTE" && p.ExtractoMovId != null)
            .Select(p => p.ExtractoMovId!.Value).ToListAsync()).ToHashSet();
        usados.UnionWith(await _db.ContadoraComprobantePagos.Where(p => !p.Anulado && p.ExtractoMovId != null)
            .Select(p => p.ExtractoMovId!.Value).ToListAsync());
        return usados;
    }

    /// <summary>Para cada pago programado por transferencia: los egresos del banco a ese CUIT, sin usar, cerca de la fecha.</summary>
    public async Task<Dictionary<int, List<MovBanco>>> CandidatosAsync(List<CafePagoProgramado> programados)
    {
        var res = new Dictionary<int, List<MovBanco>>();
        var conCuit = programados.Where(p => p.Estado == "PENDIENTE" && p.Medio == "TRANSFERENCIA"
            && ProveedorCtaCteService.NormCuit(p.Proveedor?.Cuit).Length > 0).ToList();
        if (conCuit.Count == 0) return res;
        var cuits = conCuit.Select(p => ProveedorCtaCteService.NormCuit(p.Proveedor!.Cuit)).Distinct().ToList();
        var min = conCuit.Min(p => p.Fecha).AddDays(-DiasAntes);
        var usados = await MovsUsadosAsync();
        var movs = (await _db.CafeExtractoMovimientos.AsNoTracking()
                .Where(m => m.Debitos > 0 && m.LeyendaAdicional2 != null && cuits.Contains(m.LeyendaAdicional2) && m.Fecha >= min)
                .Select(m => new { m.Id, m.Fecha, m.Debitos, m.LeyendaAdicional1, m.LeyendaAdicional2 })
                .ToListAsync())
            .Where(m => !usados.Contains(m.Id)).ToList();
        foreach (var p in conCuit)
        {
            var c = ProveedorCtaCteService.NormCuit(p.Proveedor!.Cuit);
            res[p.Id] = movs.Where(m => m.LeyendaAdicional2 == c
                    && m.Fecha.Date >= p.Fecha.AddDays(-DiasAntes) && m.Fecha.Date <= p.Fecha.AddDays(DiasDespues))
                .OrderBy(m => Math.Abs(m.Debitos - p.Importe) < 0.01m ? 0 : 1)
                .ThenBy(m => Math.Abs((m.Fecha.Date - p.Fecha).TotalDays))
                .Select(m => new MovBanco(m.Id, m.Fecha.Date, m.Debitos, m.LeyendaAdicional1)).ToList();
        }
        return res;
    }

    /// <summary>Cierra solos los programados cuya transferencia apareció en el banco por el importe exacto.</summary>
    public async Task<int> CruzarConBancoAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var hasta = HoyAr.AddDays(DiasAntes);
            var pend = await _db.CafePagosProgramados.Include(p => p.Proveedor)
                .Where(p => p.Estado == "PENDIENTE" && p.Medio == "TRANSFERENCIA" && p.Fecha <= hasta)
                .OrderBy(p => p.Fecha).ThenBy(p => p.Id).ToListAsync();
            if (pend.Count == 0) return 0;
            var cand = await CandidatosAsync(pend);
            var tomados = new HashSet<int>();
            var n = 0;
            foreach (var p in pend)
            {
                if (!cand.TryGetValue(p.Id, out var lista)) continue;
                var mov = lista.FirstOrDefault(m => !tomados.Contains(m.Id) && Math.Abs(m.Importe - p.Importe) < 0.01m);
                if (mov is null) continue;
                var (error, _) = await PagarConMovAsync(p, mov.Id, "sistema (vino en el banco)");
                if (error is null) { tomados.Add(mov.Id); n++; }
                else _log.LogWarning("[Pagos programados] no se pudo cerrar {Id} con el mov {Mov}: {Err}", p.Id, mov.Id, error);
            }
            if (n > 0) await MantenerAsync();
            return n;
        }
        finally { _lock.Release(); }
    }

    /// <summary>Registra el pago de verdad con esa transferencia del banco (por el importe del banco) y deja
    /// el programado PAGADO. Si el banco dice menos, lo que falta queda como un programado nuevo para hoy.</summary>
    public async Task<(string? error, int pagoId)> PagarConMovAsync(CafePagoProgramado p, int movId, string? operador)
    {
        var prov = p.Proveedor ?? await _db.CafeProveedores.FindAsync(p.ProveedorId);
        if (prov is null) return ("No encontré el proveedor", 0);
        var mov = await _db.CafeExtractoMovimientos.AsNoTracking().FirstOrDefaultAsync(m => m.Id == movId);
        if (mov is null || mov.Debitos <= 0) return ("No encontré esa transferencia en el banco", 0);
        if (ProveedorCtaCteService.NormCuit(mov.LeyendaAdicional2) != ProveedorCtaCteService.NormCuit(prov.Cuit))
            return ("Esa transferencia es a otro CUIT", 0);
        if ((await MovsUsadosAsync()).Contains(movId)) return ("Esa transferencia ya está cargada como pago", 0);

        var importe = Math.Round(mov.Debitos, 2);
        // Facturas que cancela (solo si lleva cuenta corriente y siguen pendientes); el resto, a cuenta.
        var items = new List<ProveedorCtaCteService.ItemPago>();
        var claves = (p.Claves ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (prov.CuentaCorriente && claves.Length > 0)
        {
            var cta = await _ctacte.GetCuentaAsync(prov.Id);
            var pendDocs = (cta?.Pendientes ?? new()).ToDictionary(d => d.Clave);
            var resta = importe;
            foreach (var cl in claves)
            {
                if (resta <= 0) break;
                if (!pendDocs.TryGetValue(cl, out var d)) continue;
                var imp = Math.Min(Math.Round(d.Saldo, 2), resta);
                if (imp <= 0) continue;
                items.Add(new ProveedorCtaCteService.ItemPago(cl, null, imp));
                resta -= imp;
            }
            if (resta > 0.005m) items.Add(new ProveedorCtaCteService.ItemPago(null, null, resta));
        }
        else items.Add(new ProveedorCtaCteService.ItemPago(null, null, importe));

        var caja = await _db.CafeCajas.AsNoTracking()
            .Where(c => c.IsActive && c.Tipo == "BANCO")
            .OrderByDescending(c => c.Nombre.Contains("Galicia")).ThenBy(c => c.Id)
            .FirstOrDefaultAsync();
        if (caja is null) return ("No encontré la caja del banco en Tesorería", 0);
        var referencia = string.Join(" ", new[] { mov.Descripcion, mov.NumeroComprobante }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        if (referencia.Length > 200) referencia = referencia[..200];

        var (error, id, _) = await _ctacte.CrearPagoAsync(new ProveedorCtaCteService.NuevoPago(
            prov.Id, 0m, operador,
            $"Pago programado del {p.Fecha:dd/MM} — vino en el extracto del banco",
            items,
            new List<ProveedorCtaCteService.MedioPago> { new(caja.Id, importe, referencia.Length == 0 ? "Transferencia banco" : referencia, null) },
            mov.Fecha.Date.AddHours(15), mov.Id));
        if (error is not null) return (error, 0);

        var tracked = await _db.CafePagosProgramados.FindAsync(p.Id);
        if (tracked is null) return (null, id);
        var falta = Math.Round(tracked.Importe - importe, 2);
        tracked.Estado = "PAGADO";
        tracked.PagoId = id;
        tracked.Importe = importe;
        tracked.UpdatedAt = DateTime.UtcNow;
        if (falta > 0.005m)
            _db.CafePagosProgramados.Add(new CafePagoProgramado
            {
                ProveedorId = tracked.ProveedorId, Fecha = HoyAr, Importe = falta, Medio = tracked.Medio,
                Claves = tracked.Claves, Nota = $"Lo que faltó del pago del {tracked.Fecha:dd/MM}",
                Operador = operador,
            });
        await _db.SaveChangesAsync();
        return (null, id);
    }
}

/// <summary>17/11/2026 — Cada 30 minutos: arma los pagos del mes siguiente y cruza con el banco.</summary>
public class PagosProgramadosBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PagosProgramadosBackgroundService> _log;
    public PagosProgramadosBackgroundService(IServiceScopeFactory f, ILogger<PagosProgramadosBackgroundService> log) { _scopeFactory = f; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(4), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<PagosProgramadosService>();
                await svc.MantenerAsync();
                var n = await svc.CruzarConBancoAsync();
                if (n > 0) _log.LogInformation("[Pagos programados] {N} cerrados con el extracto del banco", n);
            }
            catch (Exception ex) { _log.LogWarning(ex, "[Pagos programados] error en el ciclo (no critico)"); }
            try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
