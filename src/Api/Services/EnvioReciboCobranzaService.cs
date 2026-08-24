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
        var (tabla, saldoFinal) = await ArmarEstadoCuentaHtmlAsync(ids);
        var negocio = cfg?.NegocioNombre ?? "Frikaf";
        var intro = saldoFinal <= CafeSaldosService.Umbral
            ? "Te pasamos el resumen de tu cuenta. <b>Está al día, no hay saldo pendiente.</b> ¡Gracias!"
            : $"Te pasamos el resumen de tu cuenta. El saldo pendiente es de <b>{Plata(saldoFinal)}</b>.";
        return "<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#111;\">" +
               $"<p>Hola {saludo},</p><p>{intro}</p>" + tabla +
               "<p style=\"font-size:13px;color:#555;\">Si ya lo pagaste o ves algo que no coincide, avisanos y lo revisamos.</p>" +
               $"<p>Saludos,<br>{negocio}</p></div>";
    }

    /// <summary>2026-08-24: ESTADO DE CUENTA en HTML, con el formato que usan los proveedores y que
    /// el usuario pidió copiar: saldo anterior arriba, después cada movimiento (la factura suma en
    /// DEBE, el recibo y la nota de crédito restan en HABER) y el saldo corriendo a la derecha.
    /// Es la cuenta GLOBAL: si el cliente tiene varias sucursales cargadas, van todas mezcladas
    /// por fecha, porque para él es una sola cuenta.</summary>
    public async Task<(string html, decimal saldoFinal)> ArmarEstadoCuentaHtmlAsync(List<int> ids, int mesesAtras = 3)
    {
        var movs = new List<CafeSaldosService.MovimientoCuenta>();
        foreach (var id in ids) movs.AddRange(await _saldos.GetMovimientosClienteAsync(id));
        movs = movs.OrderBy(m => m.Fecha).ThenBy(m => m.Numero).ToList();

        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        var desde = hoy.AddMonths(-mesesAtras);
        var anteriores = movs.Where(m => m.Fecha.Date < desde).ToList();
        var delPeriodo = movs.Where(m => m.Fecha.Date >= desde).ToList();
        // Si en el periodo no hubo movimiento, mostramos todo (una cuenta quieta no dice nada).
        if (delPeriodo.Count == 0) { delPeriodo = movs; anteriores = new(); }

        var saldo = anteriores.Sum(m => m.Debe - m.Haber);
        var saldoAnterior = saldo;

        var sb = new StringBuilder();
        const string celda = "padding:5px 12px;";
        sb.Append("<table cellspacing=\"0\" style=\"border-collapse:collapse;font-family:Arial,Helvetica,sans-serif;font-size:13px;\">");
        sb.Append($"<tr style=\"background:#f3f4f6;\">" +
                  $"<th align=\"left\" style=\"{celda}\">FECHA</th><th align=\"left\" style=\"{celda}\">TIPO</th><th align=\"left\" style=\"{celda}\">COMPROBANTE</th>" +
                  $"<th align=\"right\" style=\"{celda}\">DEBE</th><th align=\"right\" style=\"{celda}\">HABER</th><th align=\"right\" style=\"{celda}\">SALDO</th></tr>");
        if (anteriores.Count > 0)
            sb.Append($"<tr><td colspan=\"5\" style=\"{celda}\"><b>SALDO AL {desde.AddDays(-1):dd/MM/yyyy}</b></td>" +
                      $"<td align=\"right\" style=\"{celda}\"><b>{Plata(saldoAnterior)}</b></td></tr>");

        foreach (var m in delPeriodo)
        {
            saldo += m.Debe - m.Haber;
            var tipo = m.Tipo switch
            {
                "Cobranza" => "REC",
                "Nota Crédito" => "N/C",
                _ => "FAC"
            };
            sb.Append("<tr style=\"border-top:1px solid #e5e7eb;\">" +
                      $"<td style=\"{celda}\">{m.Fecha:dd/MM/yy}</td><td style=\"{celda}\">{tipo}</td><td style=\"{celda}\">{m.Numero}</td>" +
                      $"<td align=\"right\" style=\"{celda}\">{(m.Debe > 0 ? Plata(m.Debe) : "")}</td>" +
                      $"<td align=\"right\" style=\"{celda}\">{(m.Haber > 0 ? Plata(m.Haber) : "")}</td>" +
                      $"<td align=\"right\" style=\"{celda}\">{Plata(saldo)}</td></tr>");
        }
        sb.Append($"<tr style=\"background:#fef9c3;border-top:2px solid #d1d5db;\"><td colspan=\"5\" style=\"{celda}\"><b>SALDO AL {hoy:dd/MM/yyyy}</b></td>" +
                  $"<td align=\"right\" style=\"{celda}\"><b>{Plata(saldo)}</b></td></tr>");
        sb.Append("</table>");
        return (sb.ToString(), saldo);
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
