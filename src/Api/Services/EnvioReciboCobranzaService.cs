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
        return await _envio.EnviarEmailConAdjuntoAsync(to!, asunto, cuerpo);
    }

    /// <summary>El mail de resumen de saldo tal cual le va a llegar al cliente. Lo usa el envío
    /// y también la vista previa del panel (para poder leerlo ANTES de mandarlo).</summary>
    public async Task<(string asunto, string cuerpo)> ArmarMailSaldoAsync(int clienteId)
    {
        var cli = await _db.CafeClientes.FirstOrDefaultAsync(x => x.Id == clienteId);
        var cfg = await _db.CafeSettings.FindAsync(1);
        var hoy = DateTime.UtcNow.AddHours(-3);
        var resumen = await ArmarResumenDeCuentasAsync(new List<int> { clienteId },
            "El saldo de tu cuenta es de", "✅ Tu cuenta está al día. ¡Gracias!");
        var asunto = $"Resumen de tu cuenta al {hoy:dd/MM/yyyy} - {cfg?.NegocioNombre ?? "Frikaf"}";
        var cuerpo = $"Hola {cli?.Nombre},\n\n" + resumen + "\n\n" +
                     "Si ya lo pagaste o ves algo que no coincide, avisanos y lo revisamos.\n\n" +
                     $"Saludos,\n{cfg?.NegocioNombre ?? "Frikaf"}";
        return (asunto, cuerpo);
    }

    /// <summary>Arma el detalle de deuda de una o varias cuentas (sucursales del mismo grupo).</summary>
    private async Task<string> ArmarResumenDeCuentasAsync(List<int> ids, string encabezado, string textoAlDia)
    {
        if (ids.Count == 0) return "";

        var nombres = await _db.CafeClientes.Where(x => ids.Contains(x.Id))
            .Select(x => new { x.Id, x.Nombre }).ToListAsync();
        var nombreDe = nombres.ToDictionary(x => x.Id, x => x.Nombre);

        var sb = new StringBuilder();
        decimal totalGrupo = 0m;
        var bloques = new List<(string nombre, decimal saldo, List<CafeSaldosService.VentaCuenta> pendientes)>();
        foreach (var id in ids)
        {
            var saldo = await _saldos.GetSaldoClienteAsync(id);
            var pend = (await _saldos.GetVentasCuentaAsync(id))
                .Where(v => v.Pendiente).OrderBy(v => v.Fecha).ToList();
            totalGrupo += saldo;
            bloques.Add((nombreDe.TryGetValue(id, out var n) ? n : "Cuenta", saldo, pend));
        }

        if (totalGrupo <= CafeSaldosService.Umbral) return textoAlDia;

        var variasCuentas = bloques.Count(b => b.saldo > CafeSaldosService.Umbral) > 1;
        sb.Append($"{encabezado} {Plata(totalGrupo)}:\n");

        foreach (var b in bloques)
        {
            if (b.saldo <= CafeSaldosService.Umbral) continue;
            if (variasCuentas) sb.Append($"\n• {b.nombre}: {Plata(b.saldo)}\n");
            // Hasta 12 comprobantes por cuenta: si son más, se resume. El detalle completo lo tiene
            // el operador en el sistema; al cliente le alcanza con las más viejas.
            var muestra = b.pendientes.Take(12).ToList();
            foreach (var v in muestra)
                sb.Append($"   {v.Numero} ({v.Fecha:dd/MM/yyyy}): {Plata(v.Saldo)}\n");
            if (b.pendientes.Count > muestra.Count)
                sb.Append($"   … y {b.pendientes.Count - muestra.Count} comprobante(s) más\n");
        }
        return sb.ToString().TrimEnd();
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
