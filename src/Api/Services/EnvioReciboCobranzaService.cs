using System.Globalization;
using System.Text;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>2026-08-24: le manda al cliente el comprobante de su pago (el recibo de la cobranza,
/// en PDF) + un resumen de cómo le queda la cuenta, por mail y/o por WhatsApp.
///
/// El motor de envío (SMTP y Meta) es el mismo que usa el comprobante de una venta
/// (EnvioComprobanteService): acá solo se arma el PDF del recibo y el texto.
///
/// Multi-sucursal: si la cobranza pagó facturas de varias razones/sucursales del mismo grupo
/// (caso Dulce Lugar, que paga las 3 juntas), el resumen las lista por separado y da el total,
/// que es lo que el cliente espera ver.</summary>
public class EnvioReciboCobranzaService
{
    private readonly AppDbContext _db;
    private readonly CafeReciboCobranzaPdfService _pdf;
    private readonly EnvioComprobanteService _envio;
    private readonly CafeSaldosService _saldos;
    private static readonly CultureInfo Ar = new("es-AR");

    public EnvioReciboCobranzaService(AppDbContext db, CafeReciboCobranzaPdfService pdf,
        EnvioComprobanteService envio, CafeSaldosService saldos)
    {
        _db = db; _pdf = pdf; _envio = envio; _saldos = saldos;
    }

    public record Resultado(bool EmailOk, string? EmailError, bool WhatsappOk, string? WhatsappError);

    private static string Plata(decimal m) => "$" + m.ToString("N2", Ar);

    /// <summary>Manda el recibo por los canales pedidos. destinoEmail/destinoWhatsapp son opcionales:
    /// si no vienen, se usan el mail y el teléfono del cliente.</summary>
    public async Task<Resultado> EnviarAsync(int cobranzaId, bool porEmail, bool porWhatsapp,
        string? destinoEmail = null, string? destinoWhatsapp = null, string? lineaPhoneId = null)
    {
        var c = await _db.CafeCobranzas
            .Include(x => x.Cliente)
            .Include(x => x.Comprobantes).ThenInclude(cc => cc.Venta)
            .Include(x => x.Medios).ThenInclude(m => m.Caja)
            .Include(x => x.Medios).ThenInclude(m => m.Cheque)
            .FirstOrDefaultAsync(x => x.Id == cobranzaId);
        if (c is null) return new Resultado(false, "No se encontró la cobranza.", false, "No se encontró la cobranza.");
        if (c.Cliente is null) return new Resultado(false, "La cobranza no tiene cliente.", false, "La cobranza no tiene cliente.");
        if (c.Estado != "VIGENTE") return new Resultado(false, "La cobranza está anulada.", false, "La cobranza está anulada.");

        var cfg = await _db.CafeSettings.FindAsync(1);
        var pdfBytes = GenerarPdf(c, cfg);
        var filename = $"Recibo-{c.Numero}.pdf";
        var resumen = await ArmarResumenAsync(c);

        bool emailOk = false, waOk = false;
        string? emailError = null, waError = null;

        if (porEmail)
        {
            var to = string.IsNullOrWhiteSpace(destinoEmail) ? c.Cliente.Email : destinoEmail;
            if (string.IsNullOrWhiteSpace(to)) emailError = "El cliente no tiene correo cargado.";
            else
            {
                var asunto = $"Recibo {c.Numero} - {cfg?.NegocioNombre ?? "Frikaf"}";
                var cuerpo = $"Hola {c.Cliente.Nombre},\n\n" +
                             $"Recibimos tu pago por {Plata(c.Total + c.Retenciones)}. Te adjuntamos el recibo {c.Numero}.\n\n" +
                             resumen + "\n\n" +
                             "Cualquier consulta, escribinos.\n\n" +
                             $"Saludos,\n{cfg?.NegocioNombre ?? "Frikaf"}";
                (emailOk, emailError) = await _envio.EnviarEmailConAdjuntoAsync(to!, asunto, cuerpo, pdfBytes, filename);
            }
        }

        if (porWhatsapp)
        {
            var numero = string.IsNullOrWhiteSpace(destinoWhatsapp) ? c.Cliente.Telefono : destinoWhatsapp;
            if (string.IsNullOrWhiteSpace(numero)) waError = "El cliente no tiene teléfono cargado.";
            else
            {
                var caption = $"¡Gracias {c.Cliente.Nombre}! Recibimos tu pago por {Plata(c.Total + c.Retenciones)}.\n" +
                              $"Te dejamos el recibo {c.Numero}.\n\n" + resumen;
                (waOk, waError) = await _envio.EnviarWhatsappConPdfAsync(numero!, lineaPhoneId, pdfBytes, filename, caption);
            }
        }

        return new Resultado(emailOk, emailError, waOk, waError);
    }

    /// <summary>"Cómo te queda la cuenta" después de este pago. Si la cobranza tocó facturas de
    /// varias sucursales del grupo, las desglosa y da el total.</summary>
    public async Task<string> ArmarResumenAsync(CafeCobranza c)
    {
        // Clientes involucrados: el de la cobranza + los de las facturas que se pagaron
        // (pueden ser sucursales hermanas del mismo CUIT).
        var ids = new List<int>();
        if (c.ClienteId.HasValue) ids.Add(c.ClienteId.Value);
        foreach (var cc in c.Comprobantes)
            if (cc.Venta?.ClienteId is int vid && !ids.Contains(vid)) ids.Add(vid);
        if (ids.Count == 0) return "";
        return await ArmarResumenDeCuentasAsync(ids, "Después de este pago te queda un saldo de",
            "✅ Con este pago quedás al día. ¡Gracias!");
    }

    /// <summary>2026-08-24: le manda al cliente el detalle de lo que debe, sin que haya un pago de
    /// por medio. Lo usa el botón "Mandar saldo" del panel "¿Quién me debe?". El destino puede ser
    /// el mail de la ficha o uno escrito a mano en el momento.</summary>
    public async Task<(bool ok, string? error)> EnviarResumenSaldoAsync(int clienteId, string? destinoEmail = null)
    {
        var cli = await _db.CafeClientes.FirstOrDefaultAsync(x => x.Id == clienteId);
        if (cli is null) return (false, "No se encontró el cliente.");
        var to = string.IsNullOrWhiteSpace(destinoEmail) ? cli.Email : destinoEmail!.Trim();
        if (string.IsNullOrWhiteSpace(to)) return (false, "No hay dirección de correo: cargala en la ficha o escribila a mano.");

        var (asunto, cuerpo) = await ArmarMailSaldoAsync(clienteId);
        return await _envio.EnviarEmailConAdjuntoAsync(to!, asunto, cuerpo, esHtml: true);
    }

    /// <summary>2026-08-24: UN solo mail con el total de varias cuentas del mismo grupo (las
    /// sucursales de una misma razón social). Pedido del usuario: mandarle tres mails con tres
    /// saldos parciales lo obliga al cliente a sumar y cada mail dice un total distinto.</summary>
    public async Task<(string asunto, string cuerpo)> ArmarMailSaldoGrupoAsync(List<int> clienteIds)
    {
        var clis = await _db.CafeClientes.Where(x => clienteIds.Contains(x.Id))
            .Select(x => new { x.Id, x.Nombre, x.RazonSocial, x.Cuit }).ToListAsync();
        var cfg = await _db.CafeSettings.FindAsync(1);
        var hoy = DateTime.UtcNow.AddHours(-3);

        // Si comparten razón social/CUIT, el saludo va a nombre del grupo; si no, se listan.
        var razon = clis.Select(c => c.RazonSocial).FirstOrDefault(r => !string.IsNullOrWhiteSpace(r));
        var saludo = !string.IsNullOrWhiteSpace(razon) ? razon!
                   : string.Join(" / ", clis.Select(c => c.Nombre));

        var asunto = $"Resumen de tu cuenta al {hoy:dd/MM/yyyy} - {cfg?.NegocioNombre ?? "Frikaf"}";
        var cuerpo = await ArmarCuerpoEstadoCuentaAsync(clienteIds, saludo, cfg);
        return (asunto, cuerpo);
    }

    /// <summary>Manda ese único mail con el total del grupo.</summary>
    public async Task<(bool ok, string? error)> EnviarResumenSaldoGrupoAsync(List<int> clienteIds, string? destinoEmail)
    {
        if (clienteIds is null || clienteIds.Count == 0) return (false, "No hay clientes elegidos.");
        var to = destinoEmail?.Trim();
        if (string.IsNullOrWhiteSpace(to))
            to = await _db.CafeClientes.Where(x => clienteIds.Contains(x.Id) && x.Email != null && x.Email != "")
                .Select(x => x.Email).FirstOrDefaultAsync();
        if (string.IsNullOrWhiteSpace(to))
            return (false, "Ninguno de los clientes elegidos tiene correo: escribí uno a mano.");
        var (asunto, cuerpo) = await ArmarMailSaldoGrupoAsync(clienteIds);
        return await _envio.EnviarEmailConAdjuntoAsync(to!, asunto, cuerpo, esHtml: true);
    }

    /// <summary>El mail de resumen de saldo tal cual le va a llegar al cliente. Lo usa el envío
    /// y también la vista previa del panel (para poder leerlo ANTES de mandarlo).</summary>
    public async Task<(string asunto, string cuerpo)> ArmarMailSaldoAsync(int clienteId)
    {
        var cli = await _db.CafeClientes.FirstOrDefaultAsync(x => x.Id == clienteId);
        var cfg = await _db.CafeSettings.FindAsync(1);
        var hoy = DateTime.UtcNow.AddHours(-3);
        var asunto = $"Resumen de tu cuenta al {hoy:dd/MM/yyyy} - {cfg?.NegocioNombre ?? "Frikaf"}";
        var cuerpo = await ArmarCuerpoEstadoCuentaAsync(new List<int> { clienteId }, cli?.Nombre, cfg);
        return (asunto, cuerpo);
    }

    /// <summary>Arma el detalle de deuda de una o varias cuentas (sucursales del mismo grupo).</summary>
    /// <summary>Resumen corto (una linea + comprobantes pendientes). Se usa para WhatsApp, donde
    /// una tabla no entra.</summary>
    private async Task<string> ArmarResumenDeCuentasAsync(List<int> ids, string encabezado, string textoAlDia)
    {
        if (ids.Count == 0) return "";
        // 2026-08-24: la cuenta del cliente es UNA sola aunque adentro tenga varias sucursales:
        // un total y las facturas por fecha, mezcladas (antes iba partido por sucursal).
        decimal total = 0m;
        var pendientes = new List<CafeSaldosService.VentaCuenta>();
        foreach (var id in ids)
        {
            total += await _saldos.GetSaldoClienteAsync(id);
            pendientes.AddRange((await _saldos.GetVentasCuentaAsync(id)).Where(v => v.Pendiente));
        }
        if (total <= CafeSaldosService.Umbral) return textoAlDia;

        var sb = new StringBuilder();
        sb.Append($"{encabezado} {Plata(total)}:\n");
        var ordenadas = pendientes.OrderBy(v => v.Fecha).ThenBy(v => v.Numero).ToList();
        var muestra = ordenadas.Take(20).ToList();
        foreach (var v in muestra)
            sb.Append($"   {v.Numero} ({v.Fecha:dd/MM/yyyy}): {Plata(v.Saldo)}\n");
        if (ordenadas.Count > muestra.Count)
            sb.Append($"   … y {ordenadas.Count - muestra.Count} comprobante(s) más\n");
        return sb.ToString().TrimEnd();
    }

    /// <summary>El mail completo (saludo + estado de cuenta + cierre), en HTML.</summary>
    private async Task<string> ArmarCuerpoEstadoCuentaAsync(List<int> ids, string? saludo, CafeSetting? cfg)
    {
        var (tabla, saldoTotal) = await ArmarEstadoCuentaHtmlAsync(ids);
        var negocio = cfg?.NegocioNombre ?? "Frikaf";
        var hoy = DateTime.UtcNow.AddHours(-3);
        var sb = new StringBuilder();
        sb.Append("<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#111;\">");
        sb.Append($"<p>Hola {saludo},</p>");
        if (saldoTotal <= CafeSaldosService.Umbral)
        {
            sb.Append("<p>Te pasamos el resumen de tu cuenta. <b>Está al día, no hay saldo pendiente.</b> ¡Gracias!</p>");
        }
        else
        {
            // 2026-08-24: el total va ARRIBA y grande — es lo que el usuario quiere que se vea primero.
            sb.Append("<p>Te pasamos el resumen de tu cuenta.</p>");
            sb.Append("<div style=\"background:#fef9c3;border-radius:8px;padding:14px 16px;margin:0 0 18px;display:inline-block;\">" +
                      $"<div style=\"font-size:13px;color:#78350f;\">Saldo pendiente al {hoy:dd/MM/yyyy}</div>" +
                      $"<div style=\"font-size:26px;font-weight:bold;color:#78350f;\">{Plata(saldoTotal)}</div></div>");
        }
        sb.Append(tabla);
        sb.Append("<p style=\"font-size:13px;color:#555;margin-top:16px;\">Si querés el detalle completo de los comprobantes pendientes, pedínoslo y te lo mandamos.<br>" +
                  "Si ya lo pagaste o ves algo que no coincide, avisanos y lo revisamos.</p>");
        sb.Append($"<p>Saludos,<br>{negocio}</p></div>");
        return sb.ToString();
    }

    /// <summary>Datos del comprobante que hacen falta para pintar el renglón.</summary>
    private sealed record InfoComprobante(string? TipoComprobante, int? ArcaPtoVta, int? ArcaCbteNro, int? ClienteId)
    {
        public bool EsFiscal => TipoComprobante is "FA" or "FB" or "FC" or "NCA" or "NCB" or "NCC";
        public bool EsNotaCredito => TipoComprobante is not null && TipoComprobante.StartsWith("NC");
    }

    /// <summary>Una cuenta del estado: "Facturas" (fiscal) o "Cotizaciones" (no fiscal).</summary>
    private sealed class BloqueCuenta
    {
        public string Titulo = "";
        public bool EsFiscal;
        public decimal SaldoAnterior;
        public decimal SaldoFinal;
        public DateTime? Desde;
        public List<CafeSaldosService.MovimientoCuenta> Movimientos = new();
    }

    /// <summary>2026-08-24: ESTADO DE CUENTA en HTML, con el formato que usan los proveedores y que
    /// el usuario pidió copiar: saldo anterior, cada movimiento (la factura suma en DEBE, el recibo
    /// y la nota de crédito restan en HABER) y el saldo corriendo a la derecha.
    ///
    /// Decisiones del usuario (24/08):
    ///  - Las SUCURSALES son una sola cuenta (van mezcladas por fecha), pero FACTURAS y COTIZACIONES
    ///    son DOS cuentas separadas, cada una con su saldo.
    ///  - El historial arranca en lo que pase primero: el último pago, o el comprobante impago más
    ///    viejo. Así se ve siempre el último pago Y todo lo que quedó sin pagar.
    ///  - Un recibo = una línea (si tocó varias sucursales, se suma).
    ///  - Los recibos en $0 (compensación factura + nota de crédito) no se muestran.</summary>
    public async Task<(string html, decimal saldoTotal)> ArmarEstadoCuentaHtmlAsync(List<int> ids, int mesesAtras = 3)
    {
        var movs = new List<CafeSaldosService.MovimientoCuenta>();
        foreach (var id in ids) movs.AddRange(await _saldos.GetMovimientosClienteAsync(id));

        // Un recibo que pagó facturas de varias sucursales venía en una línea por sucursal: el
        // cliente veía su transferencia partida en pedazos y no la podía cruzar con el banco.
        movs = movs
            .GroupBy(m => new { m.Fecha.Date, m.Tipo, m.Numero })
            .Select(g => new CafeSaldosService.MovimientoCuenta(
                g.Min(x => x.Fecha), g.Key.Tipo, g.Key.Numero,
                g.Sum(x => x.Debe), g.Sum(x => x.Haber), null))
            .Where(m => Math.Abs(m.Debe) > 0.005m || Math.Abs(m.Haber) > 0.005m)
            .OrderBy(m => m.Fecha).ThenBy(m => m.Numero)
            .ToList();

        // Datos de cada comprobante: si es fiscal, su número de ARCA y de qué sucursal es.
        var infoVentas = await _db.CafeVentas
            .Where(v => v.ClienteId != null && ids.Contains(v.ClienteId.Value) && v.Estado != "anulado")
            .Select(v => new { v.Numero, v.TipoComprobante, v.ArcaPtoVta, v.ArcaCbteNro, v.ClienteId })
            .ToListAsync();
        var porNumero = infoVentas.Where(v => v.Numero != null)
            .GroupBy(v => v.Numero!)
            .ToDictionary(g => g.Key, g => new InfoComprobante(
                g.First().TipoComprobante, g.First().ArcaPtoVta, g.First().ArcaCbteNro, g.First().ClienteId));
        var nombres = await _db.CafeClientes.Where(c => ids.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre }).ToListAsync();
        var nombreCorto = ArmarNombresCortos(nombres.ToDictionary(x => x.Id, x => x.Nombre));
        var variasCuentas = ids.Count > 1;

        // Los pagos siguen a la cuenta donde se imputaron.
        var fiscalPorRecibo = await MapaRecibosFiscalesAsync(ids);

        bool EsFiscalMov(CafeSaldosService.MovimientoCuenta m)
        {
            if (m.Tipo == "Cobranza") return fiscalPorRecibo.TryGetValue(m.Numero, out var f) && f;
            return porNumero.TryGetValue(m.Numero, out var v) && v.EsFiscal;
        }

        var bloques = new List<BloqueCuenta>
        {
            new() { Titulo = "Cuenta facturas", EsFiscal = true },
            new() { Titulo = "Cuenta cotizaciones", EsFiscal = false }
        };
        // El comprobante impago más viejo de cada cuenta: el historial nunca puede arrancar
        // después de él, si no el cliente ve un saldo sin poder saber de dónde sale.
        var pendientes = new List<CafeSaldosService.VentaCuenta>();
        foreach (var id in ids)
            pendientes.AddRange((await _saldos.GetVentasCuentaAsync(id)).Where(v => v.Pendiente));
        DateTime? ImpagoMasViejo(bool fiscal) => pendientes
            .Where(v => (v.TipoComprobante is "FA" or "FB" or "FC") == fiscal)
            .Select(v => (DateTime?)v.Fecha.Date).Min();

        foreach (var b in bloques)
        {
            var propios = movs.Where(m => EsFiscalMov(m) == b.EsFiscal).ToList();
            if (propios.Count == 0) continue;
            b.SaldoFinal = propios.Sum(m => m.Debe - m.Haber);
            b.Desde = CorteDesde(propios, ImpagoMasViejo(b.EsFiscal));
            b.SaldoAnterior = propios.Where(m => m.Fecha.Date < b.Desde!.Value).Sum(m => m.Debe - m.Haber);
            b.Movimientos = propios.Where(m => m.Fecha.Date >= b.Desde!.Value).ToList();
        }

        var conSaldo = bloques.Where(b => b.Movimientos.Count > 0 || Math.Abs(b.SaldoFinal) > CafeSaldosService.Umbral).ToList();
        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        var sb = new StringBuilder();
        foreach (var b in conSaldo)
        {
            if (conSaldo.Count > 1)
                sb.Append($"<p style=\"margin:16px 0 6px;font-size:14px;font-weight:bold;\">{b.Titulo}</p>");
            sb.Append(TablaHtml(b, hoy, porNumero, nombreCorto, variasCuentas));
        }
        return (sb.ToString(), bloques.Sum(b => b.SaldoFinal));
    }

    /// <summary>Dónde arranca el historial: el último pago, o el comprobante impago más viejo — lo
    /// que pase primero. Si no hay pagos, los últimos 10 movimientos.</summary>
    private static DateTime CorteDesde(List<CafeSaldosService.MovimientoCuenta> movs, DateTime? impagoMasViejo)
    {
        var ultimoPago = movs.Where(m => m.Tipo == "Cobranza").Select(m => (DateTime?)m.Fecha.Date).Max();
        var candidatos = new List<DateTime>();
        if (ultimoPago.HasValue) candidatos.Add(ultimoPago.Value);
        if (impagoMasViejo.HasValue) candidatos.Add(impagoMasViejo.Value);
        if (candidatos.Count == 0)
            return movs.Count > 10 ? movs[^10].Fecha.Date : movs[0].Fecha.Date;
        return candidatos.Min();
    }

    /// <summary>Para cada recibo, si lo que canceló fue fiscal (factura) o no (cotización).</summary>
    private async Task<Dictionary<string, bool>> MapaRecibosFiscalesAsync(List<int> ids)
    {
        var filas = await _db.CafeCobranzasComprobantes
            .Where(cc => cc.Cobranza!.Estado == "VIGENTE" && cc.VentaId != null
                      && cc.Venta!.ClienteId != null && ids.Contains(cc.Venta.ClienteId.Value))
            .Select(cc => new { cc.Cobranza!.Numero, cc.Venta!.TipoComprobante, cc.Importe })
            .ToListAsync();
        return filas.GroupBy(f => f.Numero)
            .ToDictionary(g => g.Key, g => g.Sum(x =>
                x.TipoComprobante is "FA" or "FB" or "FC" or "NCA" or "NCB" or "NCC" ? Math.Abs(x.Importe) : 0m)
                >= g.Sum(x => Math.Abs(x.Importe)) / 2m);
    }

    /// <summary>Recorta el nombre de cada sucursal sacando la parte que comparten todas
    /// ("DULCE LUGAR S.R.L SUCURSAL CANNING" → "Canning").</summary>
    private static Dictionary<int, string> ArmarNombresCortos(Dictionary<int, string> nombres)
    {
        if (nombres.Count <= 1) return nombres;
        var listas = nombres.Values.Select(n => n.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        var comun = 0;
        while (comun < listas.Min(l => l.Length) - 1 &&
               listas.All(l => l[comun].Equals(listas[0][comun], StringComparison.OrdinalIgnoreCase)))
            comun++;
        return nombres.ToDictionary(kv => kv.Key, kv =>
        {
            var partes = kv.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(comun).ToList();
            var txt = string.Join(" ", partes);
            return string.IsNullOrWhiteSpace(txt) ? kv.Value : System.Globalization.CultureInfo
                .GetCultureInfo("es-AR").TextInfo.ToTitleCase(txt.ToLower());
        });
    }

    private string TablaHtml(BloqueCuenta b, DateTime hoy, Dictionary<string, InfoComprobante> porNumero,
        Dictionary<int, string> nombreCorto, bool variasCuentas)
    {
        const string celda = "padding:5px 12px;";
        var sb = new StringBuilder();
        var saldo = b.SaldoAnterior;
        sb.Append("<table cellspacing=\"0\" style=\"border-collapse:collapse;font-family:Arial,Helvetica,sans-serif;font-size:13px;\">");
        sb.Append($"<tr style=\"background:#f3f4f6;\">" +
                  $"<th align=\"left\" style=\"{celda}\">FECHA</th><th align=\"left\" style=\"{celda}\">TIPO</th><th align=\"left\" style=\"{celda}\">COMPROBANTE</th>" +
                  $"<th align=\"right\" style=\"{celda}\">DEBE</th><th align=\"right\" style=\"{celda}\">HABER</th><th align=\"right\" style=\"{celda}\">SALDO</th></tr>");
        if (Math.Abs(b.SaldoAnterior) > 0.005m && b.Desde.HasValue)
            sb.Append($"<tr><td colspan=\"5\" style=\"{celda}\"><b>SALDO AL {b.Desde.Value.AddDays(-1):dd/MM/yyyy}</b></td>" +
                      $"<td align=\"right\" style=\"{celda}\"><b>{Plata(b.SaldoAnterior)}</b></td></tr>");

        foreach (var m in b.Movimientos)
        {
            saldo += m.Debe - m.Haber;
            porNumero.TryGetValue(m.Numero, out var info);
            var tipo = m.Tipo == "Cobranza" ? "REC"
                     : info?.EsNotaCredito == true ? "N/C"
                     : (b.EsFiscal ? "FAC" : "COT");
            // En las facturas mostramos el número de ARCA, que es el que el cliente tiene impreso.
            var numero = m.Numero;
            if (info?.ArcaCbteNro is int nro)
                numero = $"{info.ArcaPtoVta ?? 0:00000}-{nro:00000000}";
            if (variasCuentas && info?.ClienteId is int cliId && nombreCorto.TryGetValue(cliId, out var suc))
                numero += $" <span style=\"color:#6b7280;\">({suc})</span>";
            sb.Append("<tr style=\"border-top:1px solid #e5e7eb;\">" +
                      $"<td style=\"{celda}\">{m.Fecha:dd/MM/yy}</td><td style=\"{celda}\">{tipo}</td><td style=\"{celda}\">{numero}</td>" +
                      $"<td align=\"right\" style=\"{celda}\">{(m.Debe > 0 ? Plata(m.Debe) : "")}</td>" +
                      $"<td align=\"right\" style=\"{celda}\">{(m.Haber > 0 ? Plata(m.Haber) : "")}</td>" +
                      $"<td align=\"right\" style=\"{celda}\">{Plata(saldo)}</td></tr>");
        }
        sb.Append($"<tr style=\"background:#fef9c3;border-top:2px solid #d1d5db;\"><td colspan=\"5\" style=\"{celda}\"><b>SALDO AL {hoy:dd/MM/yyyy}</b></td>" +
                  $"<td align=\"right\" style=\"{celda}\"><b>{Plata(b.SaldoFinal)}</b></td></tr>");
        sb.Append("</table>");
        return sb.ToString();
    }

    private byte[] GenerarPdf(CafeCobranza c, CafeSetting? cfg)
    {
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
        return _pdf.GenerarPdfBytes(c, c.Cliente!, comps, medios, cfg);
    }
}
