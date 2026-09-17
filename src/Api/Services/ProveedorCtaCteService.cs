using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 17/09/2026 — Cuenta corriente de proveedores. UNA sola fórmula, la usan la pantalla de cuenta
/// corriente, los pagos de Tesorería, el cobro redirigido, el extracto del banco y la Contadora.
///
/// Qué se debe:
///   · OFICIAL: las facturas de AFIP del proveedor (ContadoraComprobantes, Naturaleza='COMPRA',
///     por CUIT) con fecha desde que se le prendió la cuenta corriente. Las NC restan.
///   · NO OFICIAL: las cotizaciones cargadas a mano (Cafe_ProveedorDeudas).
///   · El saldo inicial (lo que se debía el día que arrancó) va también en Cafe_ProveedorDeudas,
///     marcado oficial o no oficial.
/// Qué se pagó: los renglones de Cafe_PagosProveedorComprobantes de pagos VIGENTES, contra una
/// factura de AFIP, contra una deuda, o "a cuenta". Los renglones contra Cafe_Compras (el modelo
/// viejo, que además mueve stock) no cuentan. Para las facturas de AFIP también cuentan los pagos
/// viejos que se marcaron en la Contadora (ContadoraComprobantePagos), así las dos pantallas dicen
/// lo mismo.
///
/// La clave de un documento es "AFIP:{IdComprobante}" o "DEU:{Id}".
/// </summary>
public class ProveedorCtaCteService
{
    public const string TipoCotizacion = "COTIZACION";
    public const string TipoSaldoInicial = "SALDO_INICIAL";
    private const string PrefAfip = "AFIP:";
    private const string PrefDeuda = "DEU:";

    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;

    public ProveedorCtaCteService(AppDbContext db, AuditLogService audit) { _db = db; _audit = audit; }

    public static DateTime HoyAr() => DateTime.UtcNow.AddHours(-3).Date;

    /// <summary>Los pagos guardan la fecha de dos maneras: un instante UTC (cargados a mano) o un
    /// día pelado a las 00:00 (extracto del banco, cobro redirigido). Devuelve el día argentino.</summary>
    public static DateTime DiaAr(DateTime f) => f.TimeOfDay == TimeSpan.Zero ? f.Date : f.AddHours(-3).Date;

    public static string ClaveAfip(string idComprobante) => PrefAfip + idComprobante;
    public static string ClaveDeuda(int id) => PrefDeuda + id;

    /// <summary>Plata como la lee él ($1.234,56): el contenedor corre en cultura invariante.</summary>
    public static string Plata(decimal v) => "$" + v.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("es-AR"));

    public static string NormCuit(string? s)
        => string.IsNullOrWhiteSpace(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

    // ─────────────────────────────── Modelos ───────────────────────────────

    public class Doc
    {
        public string Clave { get; set; } = "";
        public string Tipo { get; set; } = "";
        public string Numero { get; set; } = "";
        public bool Oficial { get; set; }
        public bool EsNotaCredito { get; set; }
        public bool EsSaldoInicial { get; set; }
        public DateTime Fecha { get; set; }
        /// <summary>Con signo: una NC o un saldo inicial a favor van en negativo.</summary>
        public decimal Total { get; set; }
        public decimal Pagado { get; set; }
        public decimal Saldo => Total - Pagado;
        public int? DeudaId { get; set; }
        public string? AfipIdComprobante { get; set; }
        public bool TieneArchivo { get; set; }
        public string? Observaciones { get; set; }
        public string Etiqueta => string.IsNullOrWhiteSpace(Numero) ? Tipo : $"{Tipo} {Numero}";
    }

    public class Imputacion
    {
        public int PagoId { get; set; }
        public string PagoNumero { get; set; } = "";
        public DateTime Fecha { get; set; }
        public string? Observaciones { get; set; }
        public decimal Importe { get; set; }
        /// <summary>null = a cuenta.</summary>
        public string? Clave { get; set; }
        public int? ExtractoMovId { get; set; }
        /// <summary>Con qué se pagó, en criollo: "Efectivo", "Galicia Empresas", "cheque endosado", "cobro redirigido".</summary>
        public string? Medio { get; set; }
        /// <summary>Salió de un cobro redirigido: se deshace anulando la cobranza, no el pago.</summary>
        public bool EsRedirigido { get; set; }
    }

    public class Cuenta
    {
        public int ProveedorId { get; set; }
        public string Nombre { get; set; } = "";
        public string? Cuit { get; set; }
        public bool CuentaCorriente { get; set; }
        public DateTime? Desde { get; set; }
        public List<Doc> Docs { get; set; } = new();
        public List<Imputacion> Imputaciones { get; set; } = new();
        /// <summary>Pagos marcados en la Contadora antes de que existiera esta pantalla (sin caja).</summary>
        public decimal PagosContadora { get; set; }

        public decimal Oficial => Docs.Where(d => d.Oficial).Sum(d => d.Saldo);
        public decimal NoOficial => Docs.Where(d => !d.Oficial).Sum(d => d.Saldo);
        public decimal ACuenta => Imputaciones.Where(i => i.Clave is null).Sum(i => i.Importe);
        /// <summary>Positivo = le debemos. Negativo = a nuestro favor.</summary>
        public decimal Total => Oficial + NoOficial - ACuenta;
        public List<Doc> Pendientes => Docs.Where(d => !d.EsNotaCredito && d.Saldo > 0.005m)
            .OrderBy(d => d.Fecha).ThenBy(d => d.Numero).ToList();
    }

    // ─────────────────────────────── Cálculo ───────────────────────────────

    public async Task<Cuenta?> GetCuentaAsync(int proveedorId)
        => (await CalcularAsync(new List<int> { proveedorId })).GetValueOrDefault(proveedorId);

    /// <summary>ids null = todos los proveedores con cuenta corriente prendida.</summary>
    public async Task<Dictionary<int, Cuenta>> CalcularAsync(List<int>? ids = null)
    {
        var provQ = _db.CafeProveedores.AsNoTracking();
        provQ = ids is null ? provQ.Where(p => p.CuentaCorriente) : provQ.Where(p => ids.Contains(p.Id));
        var provs = await provQ.Select(p => new { p.Id, p.Nombre, p.Cuit, p.CuentaCorriente, p.CuentaCorrienteDesde }).ToListAsync();

        var res = provs.ToDictionary(p => p.Id, p => new Cuenta
        {
            ProveedorId = p.Id, Nombre = p.Nombre, Cuit = p.Cuit,
            CuentaCorriente = p.CuentaCorriente, Desde = p.CuentaCorrienteDesde?.Date
        });
        var activas = res.Values.Where(c => c.CuentaCorriente && c.Desde.HasValue).ToList();
        if (activas.Count == 0) return res;
        var idsAct = activas.Select(c => c.ProveedorId).ToList();

        // ── Facturas de AFIP (por CUIT, desde el día que arrancó cada uno) ──
        var porCuit = activas.Where(c => NormCuit(c.Cuit).Length > 0)
            .GroupBy(c => NormCuit(c.Cuit)).ToDictionary(g => g.Key, g => g.First());
        var cuits = porCuit.Keys.ToList();
        var docsAfip = new Dictionary<string, Doc>();
        if (cuits.Count > 0)
        {
            var minDesde = activas.Min(c => c.Desde!.Value);
            var afip = await _db.ContadoraComprobantes.AsNoTracking()
                .Where(c => c.Naturaleza == "COMPRA" && c.ReceptorDoc != null && cuits.Contains(c.ReceptorDoc)
                    && c.FechaEmision != null && c.FechaEmision >= minDesde)
                .Select(c => new { c.IdComprobante, c.ReceptorDoc, c.TipoComprobante, c.PuntoVenta, c.NumeroComprobante,
                    c.FechaEmision, c.EsNotaCredito, c.Total, c.PdfPath })
                .ToListAsync();
            foreach (var f in afip)
            {
                var cta = porCuit[f.ReceptorDoc!];
                if (f.FechaEmision!.Value.Date < cta.Desde!.Value) continue;
                var d = new Doc
                {
                    Clave = ClaveAfip(f.IdComprobante),
                    AfipIdComprobante = f.IdComprobante,
                    Tipo = string.IsNullOrWhiteSpace(f.TipoComprobante) ? "Factura" : f.TipoComprobante!,
                    Numero = NumeroAfip(f.PuntoVenta, f.NumeroComprobante),
                    Oficial = true,
                    EsNotaCredito = f.EsNotaCredito,
                    Fecha = f.FechaEmision.Value.Date,
                    Total = f.EsNotaCredito ? -f.Total : f.Total,
                    TieneArchivo = f.PdfPath != null
                };
                cta.Docs.Add(d);
                docsAfip[f.IdComprobante] = d;
            }
            // Pagos viejos marcados en la Contadora (sin caja). Solo sobre facturas que cuentan.
            var afipIds = docsAfip.Keys.ToList();
            if (afipIds.Count > 0)
            {
                var viejos = await _db.ContadoraComprobantePagos.AsNoTracking()
                    .Where(p => !p.Anulado && afipIds.Contains(p.IdComprobante))
                    .GroupBy(p => p.IdComprobante)
                    .Select(g => new { Id = g.Key, Total = g.Sum(x => x.Importe) })
                    .ToListAsync();
                foreach (var v in viejos)
                    if (docsAfip.TryGetValue(v.Id, out var d) && !d.EsNotaCredito) d.Pagado += v.Total;
            }
        }

        // ── Deudas cargadas a mano (cotizaciones y saldo inicial) ──
        var docsDeuda = new Dictionary<int, Doc>();
        var deudas = await _db.CafeProveedorDeudas.AsNoTracking()
            .Where(d => idsAct.Contains(d.ProveedorId) && d.Estado == "VIGENTE")
            .ToListAsync();
        foreach (var x in deudas)
        {
            var esSaldo = x.Tipo == TipoSaldoInicial;
            var d = new Doc
            {
                Clave = ClaveDeuda(x.Id),
                DeudaId = x.Id,
                Tipo = esSaldo ? (x.Oficial ? "Saldo inicial oficial" : "Saldo inicial no oficial") : "Cotización",
                Numero = esSaldo ? "" : (string.IsNullOrWhiteSpace(x.Numero) ? "s/n" : x.Numero!),
                Oficial = esSaldo && x.Oficial,
                EsSaldoInicial = esSaldo,
                Fecha = x.Fecha.Date,
                Total = x.Importe,
                TieneArchivo = x.ArchivoPath != null,
                Observaciones = x.Observaciones
            };
            res[x.ProveedorId].Docs.Add(d);
            docsDeuda[x.Id] = d;
        }

        // ── Pagos ──
        var imps = await _db.CafePagosProveedorComprobantes.AsNoTracking()
            .Where(c => idsAct.Contains(c.Pago!.ProveedorId) && c.Pago.Estado == "VIGENTE")
            .Select(c => new
            {
                c.PagoId, c.Pago!.ProveedorId, c.Pago.Numero, c.Pago.Fecha, c.Pago.CreatedAt, c.Pago.Observaciones,
                c.Pago.ExtractoMovId, c.CompraId, c.AfipIdComprobante, c.DeudaId, c.Importe
            })
            .ToListAsync();
        foreach (var i in imps)
        {
            var cta = res[i.ProveedorId];
            string? clave;
            if (i.AfipIdComprobante != null)
            {
                // Una factura anterior al arranque (o de otro CUIT) no cuenta: tampoco su pago.
                if (!docsAfip.TryGetValue(i.AfipIdComprobante, out var d) || !cta.Docs.Contains(d)) continue;
                d.Pagado += i.Importe;
                clave = d.Clave;
            }
            else if (i.DeudaId != null)
            {
                if (!docsDeuda.TryGetValue(i.DeudaId.Value, out var d) || !cta.Docs.Contains(d)) continue;
                d.Pagado += i.Importe;
                clave = d.Clave;
            }
            else if (i.CompraId != null) continue;   // modelo viejo (Cafe_Compras): no entra
            else
            {
                // A cuenta: solo los cargados desde que arrancó la cuenta corriente.
                if (i.CreatedAt.AddHours(-3).Date < cta.Desde!.Value) continue;
                clave = null;
            }
            cta.Imputaciones.Add(new Imputacion
            {
                PagoId = i.PagoId, PagoNumero = i.Numero, Fecha = DiaAr(i.Fecha), Observaciones = i.Observaciones,
                Importe = i.Importe, Clave = clave, ExtractoMovId = i.ExtractoMovId
            });
        }

        // Con qué se pagó cada pago (para mostrarlo en criollo, como en la cuenta del repartidor).
        var pagoIds = activas.SelectMany(c => c.Imputaciones).Select(i => i.PagoId).Distinct().ToList();
        if (pagoIds.Count > 0)
        {
            var medios = await _db.CafePagosProveedorMedios.AsNoTracking()
                .Where(m => pagoIds.Contains(m.PagoId))
                .Select(m => new { m.PagoId, Caja = m.Caja!.Nombre, m.ChequeId })
                .ToListAsync();
            var redirigidos = (await _db.CafeCobranzasMedios.AsNoTracking()
                    .Where(m => m.RedirigidoPagoId != null && pagoIds.Contains(m.RedirigidoPagoId.Value)
                        && m.RedirigidoDestino == "proveedor")
                    .Select(m => m.RedirigidoPagoId!.Value).ToListAsync())
                .ToHashSet();
            foreach (var i in activas.SelectMany(c => c.Imputaciones))
            {
                var ms = medios.Where(m => m.PagoId == i.PagoId)
                    .Select(m => m.ChequeId != null ? "cheque endosado" : m.Caja).Distinct().ToList();
                i.EsRedirigido = redirigidos.Contains(i.PagoId);
                i.Medio = ms.Count > 0 ? string.Join(" + ", ms) : i.EsRedirigido ? "cobro redirigido" : null;
            }
        }

        foreach (var c in activas) c.PagosContadora = docsAfip.Values.Where(d => c.Docs.Contains(d)).Sum(d => d.Pagado)
            - c.Imputaciones.Where(i => i.Clave != null && i.Clave.StartsWith(PrefAfip)).Sum(i => i.Importe);
        return res;
    }

    public static string NumeroAfip(int? pv, long? num)
        => $"{(pv.HasValue ? pv.Value.ToString("D5") : "")}-{(num.HasValue ? num.Value.ToString("D8") : "")}".Trim('-');

    // ─────────────────────────── Estado de cuenta ───────────────────────────

    public record Movimiento(DateTime Fecha, string Que, decimal Suma, decimal Resta, decimal Saldo, string? Detalle,
        string? Clave, int? PagoId, bool Oficial, string? Medio, bool PagoRedirigido);

    /// <summary>Renglones con el saldo que va quedando, LO ÚLTIMO ARRIBA (como la cuenta del
    /// repartidor). Un pago es un renglón aunque haya pagado varias cosas: el detalle dice cuáles.</summary>
    public static List<Movimiento> Movimientos(Cuenta c)
    {
        var filas = new List<(DateTime f, int orden, string que, decimal suma, decimal resta, string? det, string? clave, int? pagoId, bool oficial, string? medio, bool redir)>();
        foreach (var d in c.Docs)
        {
            if (d.Total >= 0)
                filas.Add((d.Fecha, 0, d.Etiqueta, d.Total, 0m, d.Observaciones, d.Clave, null, d.Oficial, null, false));
            else
                filas.Add((d.Fecha, 0, d.Etiqueta, 0m, -d.Total, d.EsNotaCredito ? "Nota de crédito: baja la deuda" : "A nuestro favor", d.Clave, null, d.Oficial, null, false));
        }
        var etiquetas = c.Docs.ToDictionary(d => d.Clave, d => d.Etiqueta);
        foreach (var g in c.Imputaciones.GroupBy(i => i.PagoId))
        {
            var partes = g.Select(i => i.Clave is null ? "a cuenta" : etiquetas.GetValueOrDefault(i.Clave, "?")).Distinct().ToList();
            var det = $"{g.First().PagoNumero} · pagó " + string.Join(" · ", partes);
            var obs = g.First().Observaciones;
            if (!string.IsNullOrWhiteSpace(obs)) det += " — " + obs;
            var oficial = g.All(i => i.Clave != null && etiquetas.ContainsKey(i.Clave) && c.Docs.First(d => d.Clave == i.Clave).Oficial);
            filas.Add((g.First().Fecha, 1, "Pago", 0m, g.Sum(i => i.Importe), det, null, g.Key, oficial, g.First().Medio, g.First().EsRedirigido));
        }
        // Pagos que se marcaron en la Contadora antes de esta pantalla: no tienen número ni caja.
        if (c.PagosContadora > 0.005m)
            filas.Add((c.Desde ?? DateTime.MinValue, 1, "Pagos marcados en la Contadora", 0m, c.PagosContadora,
                "Marcados antes de que existiera esta pantalla (no descontaron de ninguna caja)", null, null, true, null, false));

        decimal acum = 0m;
        var res = new List<Movimiento>();
        foreach (var x in filas.OrderBy(x => x.f).ThenBy(x => x.orden))
        {
            acum += x.suma - x.resta;
            res.Add(new Movimiento(x.f, x.que, x.suma, x.resta, acum, x.det, x.clave, x.pagoId, x.oficial, x.medio, x.redir));
        }
        res.Reverse();   // lo último arriba
        return res;
    }

    // ─────────────────────────────── Pagos ───────────────────────────────

    public record ItemPago(string? Clave, int? CompraId, decimal Importe);
    public record MedioPago(int CajaId, decimal Importe, string? Referencia, int? ChequeExistenteId);
    public record NuevoPago(int ProveedorId, decimal Retenciones, string? Operador, string? Observaciones,
        List<ItemPago> Items, List<MedioPago> Medios, DateTime? Fecha = null, int? ExtractoMovId = null);

    /// <summary>Crea un pago a proveedor. Es el mismo camino para la pantalla de Tesorería y para el
    /// extracto del banco. Devuelve error (en palabras del usuario) o el pago creado.</summary>
    public async Task<(string? error, int id, string numero)> CrearPagoAsync(NuevoPago req)
    {
        var proveedor = await _db.CafeProveedores.FindAsync(req.ProveedorId);
        if (proveedor is null) return ("Proveedor no encontrado", 0, "");
        if (req.Items is null || req.Items.Count == 0) return ("Cargar al menos un comprobante (o 'a cuenta')", 0, "");
        if (req.Medios is null || req.Medios.Count == 0) return ("Cargar al menos una forma de pago", 0, "");
        if (req.Items.Any(i => i.Importe <= 0)) return ("Hay un importe en cero o negativo", 0, "");

        var sumComp = req.Items.Sum(c => c.Importe);
        var sumMed = req.Medios.Sum(m => m.Importe);
        var reten = Math.Max(0m, req.Retenciones);
        if (Math.Abs(sumComp - (sumMed + reten)) > 0.01m)
            return ($"No cuadra: imputado {Plata(sumComp)} vs medios+retenciones {Plata(sumMed + reten)}", 0, "");

        var err = await ValidarClavesAsync(req.ProveedorId, req.Items.Select(i => (i.Clave, i.Importe)).ToList());
        if (err is not null) return (err, 0, "");

        foreach (var med in req.Medios.Where(m => m.ChequeExistenteId.HasValue))
        {
            var ch = await _db.CafeCheques.FindAsync(med.ChequeExistenteId!.Value);
            if (ch is null) return ($"Cheque {med.ChequeExistenteId} no encontrado", 0, "");
            if (ch.Estado != "EN_CARTERA") return ($"Cheque {ch.Numero} no esta en cartera (estado: {ch.Estado})", 0, "");
            if (Math.Abs(ch.Importe - med.Importe) > 0.01m) return ($"El importe del medio ({Plata(med.Importe)}) no coincide con el del cheque {ch.Numero} ({Plata(ch.Importe)})", 0, "");
        }

        var numero = await SiguienteNumeroAsync();
        var pago = new CafePagoProveedor
        {
            Numero = numero,
            Fecha = req.Fecha ?? DateTime.UtcNow,
            ProveedorId = req.ProveedorId,
            Total = sumMed,
            Retenciones = reten,
            Operador = req.Operador,
            Observaciones = req.Observaciones,
            Estado = "VIGENTE",
            ExtractoMovId = req.ExtractoMovId
        };
        _db.CafePagosProveedor.Add(pago);
        await _db.SaveChangesAsync();

        foreach (var c in req.Items)
        {
            var row = new CafePagoProveedorComprobante { PagoId = pago.Id, CompraId = c.Clave is null ? c.CompraId : null, Importe = c.Importe };
            AplicarClave(row, c.Clave);
            _db.CafePagosProveedorComprobantes.Add(row);
        }
        foreach (var m in req.Medios)
        {
            if (m.ChequeExistenteId.HasValue)
            {
                var ch = await _db.CafeCheques.FindAsync(m.ChequeExistenteId.Value);
                if (ch is not null)
                {
                    ch.Estado = "ENDOSADO";
                    ch.FechaCambioEstado = DateTime.UtcNow;
                    ch.ProveedorEndosoId = req.ProveedorId;
                    ch.PagoOrigenId = pago.Id;
                }
            }
            _db.CafePagosProveedorMedios.Add(new CafePagoProveedorMedio
            {
                PagoId = pago.Id, CajaId = m.CajaId, Importe = m.Importe, Referencia = m.Referencia, ChequeId = m.ChequeExistenteId
            });
        }
        await _db.SaveChangesAsync();

        await _audit.LogAsync("CafePagoProveedor", pago.Id.ToString(), "CREATE",
            $"Pago {numero} a {proveedor.Nombre}, total {Plata(sumMed)}");
        return (null, pago.Id, numero);
    }

    /// <summary>Número correlativo OP-00000001, igual que siempre.</summary>
    public async Task<string> SiguienteNumeroAsync()
    {
        var numeros = await _db.CafePagosProveedor.Select(x => x.Numero).ToListAsync();
        int maxSec = 0;
        foreach (var n in numeros)
        {
            var parts = (n ?? "").Split('-');
            if (parts.Length >= 2 && int.TryParse(parts[^1], out var k) && k > maxSec) maxSec = k;
        }
        return $"OP-{(maxSec + 1):D8}";
    }

    /// <summary>Pone en el renglón del pago contra qué documento va. null = a cuenta.</summary>
    public static void AplicarClave(CafePagoProveedorComprobante row, string? clave)
    {
        if (string.IsNullOrWhiteSpace(clave)) return;
        if (clave.StartsWith(PrefAfip)) row.AfipIdComprobante = clave.Substring(PrefAfip.Length);
        else if (clave.StartsWith(PrefDeuda) && int.TryParse(clave.Substring(PrefDeuda.Length), out var id)) row.DeudaId = id;
    }

    /// <summary>Cada clave tiene que ser un documento pendiente de ESE proveedor y no pagarse de más.</summary>
    public async Task<string?> ValidarClavesAsync(int proveedorId, List<(string? clave, decimal importe)> items)
    {
        var conClave = items.Where(i => !string.IsNullOrWhiteSpace(i.clave)).ToList();
        if (conClave.Count == 0) return null;
        var cta = await GetCuentaAsync(proveedorId);
        if (cta is null) return "Proveedor no encontrado";
        if (!cta.CuentaCorriente) return $"{cta.Nombre} no lleva cuenta corriente: el pago solo puede ir a cuenta.";
        var pend = cta.Pendientes.ToDictionary(d => d.Clave);
        foreach (var g in conClave.GroupBy(i => i.clave!))
        {
            if (!pend.TryGetValue(g.Key, out var d))
                return "Uno de los comprobantes elegidos ya está pagado o no es de este proveedor. Volvé a abrir el pago.";
            var imp = g.Sum(x => x.importe);
            if (imp > d.Saldo + 0.01m)
                return $"{d.Etiqueta}: le quedan {Plata(d.Saldo)} y se quieren pagar {Plata(imp)}.";
        }
        return null;
    }

    // ───────────────────────── Alta / saldo inicial / cotizaciones ─────────────────────────

    /// <summary>Prende la cuenta corriente desde un día. Si no se había prendido nunca, arranca hoy.</summary>
    public async Task<string?> ActivarAsync(int proveedorId, DateTime? desde)
    {
        var p = await _db.CafeProveedores.FindAsync(proveedorId);
        if (p is null) return "Proveedor no encontrado";
        p.CuentaCorriente = true;
        if (desde.HasValue) p.CuentaCorrienteDesde = desde.Value.Date;
        else p.CuentaCorrienteDesde ??= HoyAr();
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        // Si ya tenía saldo inicial, que quede con la fecha nueva de arranque.
        var saldos = await _db.CafeProveedorDeudas
            .Where(d => d.ProveedorId == proveedorId && d.Tipo == TipoSaldoInicial && d.Estado == "VIGENTE").ToListAsync();
        foreach (var s in saldos) s.Fecha = p.CuentaCorrienteDesde!.Value;
        if (saldos.Count > 0) await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeProveedor", p.Id.ToString(), "CTACTE_ON",
            $"Cuenta corriente de {p.Nombre} desde {p.CuentaCorrienteDesde:dd/MM/yyyy}");
        return null;
    }

    public async Task<string?> DesactivarAsync(int proveedorId)
    {
        var p = await _db.CafeProveedores.FindAsync(proveedorId);
        if (p is null) return "Proveedor no encontrado";
        p.CuentaCorriente = false;   // la fecha y lo cargado quedan: si se vuelve a prender, sigue igual
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeProveedor", p.Id.ToString(), "CTACTE_OFF", $"Cuenta corriente de {p.Nombre} apagada");
        return null;
    }

    /// <summary>Deja el saldo inicial (oficial o no oficial) en ese importe. 0 lo saca.</summary>
    public async Task<string?> SetSaldoInicialAsync(int proveedorId, bool oficial, decimal importe, string? operador)
    {
        var p = await _db.CafeProveedores.FindAsync(proveedorId);
        if (p is null) return "Proveedor no encontrado";
        if (!p.CuentaCorriente || p.CuentaCorrienteDesde is null) return "Primero hay que prenderle la cuenta corriente.";
        importe = Math.Round(importe, 2);

        var actual = await _db.CafeProveedorDeudas.FirstOrDefaultAsync(d => d.ProveedorId == proveedorId
            && d.Tipo == TipoSaldoInicial && d.Oficial == oficial && d.Estado == "VIGENTE");
        var pagado = actual is null ? 0m : await PagadoDeudaAsync(actual.Id);
        if (actual is not null && pagado > 0 && importe < pagado)
            return $"Ese saldo inicial ya tiene {Plata(pagado)} pagados: no puede quedar en menos que eso.";

        if (actual is null)
        {
            if (importe == 0) return null;
            _db.CafeProveedorDeudas.Add(new CafeProveedorDeuda
            {
                ProveedorId = proveedorId, Tipo = TipoSaldoInicial, Oficial = oficial,
                Fecha = p.CuentaCorrienteDesde.Value, Importe = importe, Operador = operador, Estado = "VIGENTE"
            });
        }
        else if (importe == 0)
        {
            actual.Estado = "ANULADA";
            actual.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            actual.Importe = importe;
            actual.Fecha = p.CuentaCorrienteDesde.Value;
            actual.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeProveedor", p.Id.ToString(), "CTACTE_SALDO_INICIAL",
            $"{p.Nombre}: saldo inicial {(oficial ? "oficial" : "no oficial")} {Plata(importe)}");
        return null;
    }

    public async Task<(string? error, int id)> CrearCotizacionAsync(int proveedorId, DateTime fecha, string? numero,
        decimal importe, string? observaciones, string? operador)
    {
        var p = await _db.CafeProveedores.FindAsync(proveedorId);
        if (p is null) return ("Proveedor no encontrado", 0);
        if (!p.CuentaCorriente) return ($"{p.Nombre} no lleva cuenta corriente.", 0);
        if (importe <= 0) return ("El importe tiene que ser mayor a cero.", 0);
        var d = new CafeProveedorDeuda
        {
            ProveedorId = proveedorId, Tipo = TipoCotizacion, Oficial = false, Fecha = fecha.Date,
            Numero = string.IsNullOrWhiteSpace(numero) ? null : numero.Trim(),
            Importe = Math.Round(importe, 2),
            Observaciones = string.IsNullOrWhiteSpace(observaciones) ? null : observaciones.Trim(),
            Operador = operador, Estado = "VIGENTE"
        };
        _db.CafeProveedorDeudas.Add(d);
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeProveedorDeuda", d.Id.ToString(), "CREATE",
            $"Cotización {d.Numero ?? "s/n"} de {p.Nombre} por {Plata(d.Importe)}");
        return (null, d.Id);
    }

    /// <summary>Corrige una cotización (fecha, número, importe, nota). No puede quedar en menos de lo ya pagado.</summary>
    public async Task<string?> EditarCotizacionAsync(int deudaId, DateTime fecha, string? numero, decimal importe, string? observaciones)
    {
        var d = await _db.CafeProveedorDeudas.FindAsync(deudaId);
        if (d is null || d.Estado != "VIGENTE") return "No encontré esa cotización.";
        if (d.Tipo != TipoCotizacion) return "El saldo inicial se cambia con \"cambiar\", arriba en la cuenta.";
        importe = Math.Round(importe, 2);
        if (importe <= 0) return "El importe tiene que ser mayor a cero.";
        var pagado = await PagadoDeudaAsync(d.Id);
        if (importe < pagado) return $"Ya tiene {Plata(pagado)} pagados: el importe no puede quedar en menos que eso.";
        var antes = $"{d.Fecha:dd/MM/yyyy} {d.Numero} {Plata(d.Importe)}";
        d.Fecha = fecha.Date;
        d.Numero = string.IsNullOrWhiteSpace(numero) ? null : numero.Trim();
        d.Importe = importe;
        d.Observaciones = string.IsNullOrWhiteSpace(observaciones) ? null : observaciones.Trim();
        d.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeProveedorDeuda", d.Id.ToString(), "EDITAR",
            $"Cotización editada: antes {antes} → ahora {d.Fecha:dd/MM/yyyy} {d.Numero} {Plata(d.Importe)}");
        return null;
    }

    public async Task<string?> AnularDeudaAsync(int deudaId)
    {
        var d = await _db.CafeProveedorDeudas.FindAsync(deudaId);
        if (d is null) return "No encontré esa cotización.";
        if (d.Estado != "VIGENTE") return null;
        if (await PagadoDeudaAsync(d.Id) > 0)
            return "Tiene pagos cargados: primero anulá el pago en Tesorería → Pagos a proveedores.";
        d.Estado = "ANULADA";
        d.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        await _audit.LogAsync("CafeProveedorDeuda", d.Id.ToString(), "ANULAR", $"{d.Tipo} {d.Numero} {Plata(d.Importe)} anulada");
        return null;
    }

    private async Task<decimal> PagadoDeudaAsync(int deudaId)
        => await _db.CafePagosProveedorComprobantes
            .Where(c => c.DeudaId == deudaId && c.Pago!.Estado == "VIGENTE")
            .SumAsync(c => (decimal?)c.Importe) ?? 0m;

    // ─────────────────────────── Para la Contadora ───────────────────────────

    /// <summary>De una lista de facturas de compra de AFIP, cuáles cuentan en una cuenta corriente
    /// (proveedor con tilde y fecha desde el arranque), de qué proveedor son y cuánto se pagó de cada una.</summary>
    public async Task<Dictionary<string, (int ProveedorId, decimal Pagado)>> EstadoFacturasAfipAsync(
        IEnumerable<(string IdComprobante, string? Cuit, DateTime? Fecha)> facturas)
    {
        var lista = facturas.Where(f => !string.IsNullOrEmpty(f.Cuit) && f.Fecha.HasValue).ToList();
        if (lista.Count == 0) return new();
        var cuits = lista.Select(f => f.Cuit!).Distinct().ToList();
        var provs = (await _db.CafeProveedores.AsNoTracking()
                .Where(p => p.CuentaCorriente && p.CuentaCorrienteDesde != null && p.Cuit != null && cuits.Contains(p.Cuit))
                .Select(p => new { p.Id, p.Cuit, p.CuentaCorrienteDesde })
                .ToListAsync())
            .GroupBy(p => p.Cuit!).ToDictionary(g => g.Key, g => g.First());
        var cuentan = lista.Where(f => provs.TryGetValue(f.Cuit!, out var p) && f.Fecha!.Value.Date >= p.CuentaCorrienteDesde!.Value.Date).ToList();
        if (cuentan.Count == 0) return new();

        var ids = cuentan.Select(f => f.IdComprobante).Distinct().ToList();
        var nuevos = await _db.CafePagosProveedorComprobantes.AsNoTracking()
            .Where(c => c.AfipIdComprobante != null && ids.Contains(c.AfipIdComprobante) && c.Pago!.Estado == "VIGENTE")
            .GroupBy(c => c.AfipIdComprobante!).Select(g => new { Id = g.Key, Total = g.Sum(x => x.Importe) }).ToListAsync();
        var viejos = await _db.ContadoraComprobantePagos.AsNoTracking()
            .Where(p => !p.Anulado && ids.Contains(p.IdComprobante))
            .GroupBy(p => p.IdComprobante).Select(g => new { Id = g.Key, Total = g.Sum(x => x.Importe) }).ToListAsync();
        var pagado = new Dictionary<string, decimal>();
        foreach (var x in nuevos.Concat(viejos)) pagado[x.Id] = pagado.GetValueOrDefault(x.Id) + x.Total;

        var res = new Dictionary<string, (int, decimal)>();
        foreach (var f in cuentan) res[f.IdComprobante] = (provs[f.Cuit!].Id, pagado.GetValueOrDefault(f.IdComprobante));
        return res;
    }
}
