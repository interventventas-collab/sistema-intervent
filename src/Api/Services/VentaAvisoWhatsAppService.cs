using Api.Controllers;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-06 (pedido del usuario): AVISO DE VENTA A LOS INTERNOS por WhatsApp.
///
/// Antes: al emitir una venta con la copia por WhatsApp tildada, al interno (Gabriel/Osmar/Germán)
/// le llegaba el PDF entero del comprobante.
/// Ahora: le llega un mensajito RESUMEN (N° venta, cliente, importe, detalle) con 3 BOTONES. Según
/// cuál toque, el bot le responde solo:
///   • Comprobante  → el PDF de la factura/comprobante (el mismo de siempre)
///   • Cuenta corriente → el estado de cuenta del cliente (saldo + últimos movimientos)
///   • Detalle      → la lista de productos/cantidades/precios de esa venta, en texto
///
/// Todo es editable desde ⚙️ WhatsApp → "Mensajes del bot" (textos + botones en BotTextos; el
/// interruptor on/off en AppSetting "whatsapp.venta_aviso.enabled").
///
/// Los ids de los botones son "bot:venta:{accion}:{ventaId}" y vuelven por el webhook. El webhook
/// los detecta ANTES de los otros bots y llama a <see cref="HandleBotonAsync"/>.
///
/// Vive como servicio (no en un controller) porque lo usan DOS lugares: el endpoint que dispara el
/// aviso al emitir (WhatsAppTwilioController) y el webhook que atiende el botón. La URL base va como
/// parámetro porque el webhook no tiene el Request del controller.
/// </summary>
public sealed class VentaAvisoWhatsAppService
{
    private readonly AppDbContext _db;
    private readonly MetaWhatsAppService _meta;
    private readonly CafeVentasController _ventas;      // reusa GenerarPdfBytesAsync / BuildPdfFilename
    private readonly CafeClientesController _clientes;  // reusa GetEstadoCuentaAsync
    private readonly ILogger<VentaAvisoWhatsAppService> _logger;

    // Mismo volumen montado donde WhatsAppTwilioController guarda sus adjuntos, servidos por
    // GET /api/whatsapp/twilio/files/{token}.pdf (público, sin auth) para que Meta los descargue.
    private const string UploadsDir = "/data/whatsapp-uploads";
    private const string EnabledKey = "whatsapp.venta_aviso.enabled";

    public VentaAvisoWhatsAppService(AppDbContext db, MetaWhatsAppService meta,
        CafeVentasController ventas, CafeClientesController clientes,
        ILogger<VentaAvisoWhatsAppService> logger)
    {
        _db = db;
        _meta = meta;
        _ventas = ventas;
        _clientes = clientes;
        _logger = logger;
    }

    /// <summary>¿Está prendido el aviso con botones? Default true. Apagado = value "false".</summary>
    public async Task<bool> AvisoHabilitadoAsync() =>
        !await _db.AppSettings.AnyAsync(s => s.Key == EnabledKey && s.Value == "false");

    /// <summary>
    /// Dispara el aviso al emitir. Si el aviso está PRENDIDO manda el resumen con botones; si está
    /// APAGADO cae al comportamiento viejo (manda el PDF entero) para no romper nada. baseUrl es
    /// tipo "https://host" (para armar la URL pública del PDF cuando haga falta).
    /// </summary>
    public async Task<(bool ok, string? err)> EnviarAvisoAsync(string numeroRaw, int ventaId, string? lineaPhoneId, string baseUrl)
    {
        var v = await _db.CafeVentas
            .Include(x => x.Items)
            .Include(x => x.ClienteNav)
            .FirstOrDefaultAsync(x => x.Id == ventaId);
        if (v is null) return (false, "Venta no encontrada");

        var inbox = MetaWhatsAppService.ToInboxWhatsApp(numeroRaw);

        // Apagado → comportamiento histórico: el PDF entero del comprobante.
        if (!await AvisoHabilitadoAsync())
        {
            var (url, filename) = await GenerarComprobanteUrlAsync(v, numeroRaw, baseUrl);
            var capPdf = $"📄 Copia del comprobante {v.Numero}";
            var sidPdf = await _meta.SendMediaAsync(numeroRaw, url, capPdf, isDocument: true, filename: filename, lineaPhoneId: lineaPhoneId);
            await RegistrarSalienteAsync(inbox, capPdf, sidPdf, url, filename, lineaPhoneId);
            return (sidPdf is not null, sidPdf is null ? "Meta no aceptó el mensaje (¿ventana de 24hs cerrada?)" : null);
        }

        // Prendido → resumen + 3 botones.
        var textos = await BotTextos.CargarAsync(_db);
        var cuerpo = RenderCuerpo(textos.AvisoVentaCuerpo, v);
        var botones = new (string Id, string Title)[]
        {
            ($"bot:venta:comprobante:{v.Id}", textos.AvisoVentaBotonComprobante),
            ($"bot:venta:cc:{v.Id}",          textos.AvisoVentaBotonCc),
            ($"bot:venta:detalle:{v.Id}",     textos.AvisoVentaBotonDetalle),
        };
        var sid = await _meta.SendButtonsAsync(numeroRaw, cuerpo, botones, lineaPhoneId: lineaPhoneId);
        await RegistrarSalienteAsync(inbox, cuerpo + " [botones: comprobante / cuenta corriente / detalle]", sid, null, null, lineaPhoneId);
        return (sid is not null, sid is null ? "Meta no aceptó el mensaje (¿ventana de 24hs cerrada?)" : null);
    }

    /// <summary>
    /// Atiende el toque de un botón del aviso ("bot:venta:{accion}:{ventaId}"). Lo llama el webhook.
    /// fromWaId = wa id crudo del interno (para mandarle); numeroInbox = "whatsapp:+…" (para la bandeja).
    /// </summary>
    public async Task HandleBotonAsync(string fromWaId, string numeroInbox, string accion, int ventaId, string? lineaPhoneId, string baseUrl)
    {
        var v = await _db.CafeVentas
            .Include(x => x.Items)
            .Include(x => x.ClienteNav)
            .FirstOrDefaultAsync(x => x.Id == ventaId);
        if (v is null)
        {
            var sidNf = await _meta.SendTextAsync(fromWaId, "No encontré esa venta (puede haber sido anulada).", lineaPhoneId: lineaPhoneId);
            await RegistrarSalienteAsync(numeroInbox, "No encontré esa venta.", sidNf, null, null, lineaPhoneId);
            return;
        }

        switch (accion)
        {
            case "comprobante":
            {
                var (url, filename) = await GenerarComprobanteUrlAsync(v, fromWaId, baseUrl);
                var cap = $"📄 Comprobante {v.Numero}";
                var sid = await _meta.SendMediaAsync(fromWaId, url, cap, isDocument: true, filename: filename, lineaPhoneId: lineaPhoneId);
                await RegistrarSalienteAsync(numeroInbox, cap, sid, url, filename, lineaPhoneId);
                break;
            }
            case "cc":
            {
                var texto = await ArmarCuentaCorrienteAsync(v);
                var sid = await _meta.SendTextAsync(fromWaId, texto, lineaPhoneId: lineaPhoneId);
                await RegistrarSalienteAsync(numeroInbox, texto, sid, null, null, lineaPhoneId);
                break;
            }
            case "detalle":
            {
                var texto = ArmarDetalle(v);
                var sid = await _meta.SendTextAsync(fromWaId, texto, lineaPhoneId: lineaPhoneId);
                await RegistrarSalienteAsync(numeroInbox, texto, sid, null, null, lineaPhoneId);
                break;
            }
            default:
                _logger.LogWarning("[Aviso venta] acción desconocida '{Accion}' para venta {VentaId}", accion, ventaId);
                break;
        }
    }

    // ─────────────────────────── helpers ───────────────────────────

    /// <summary>Total del comprobante: con IVA si es factura ARCA (ArcaImpTotal), si no el Total.</summary>
    private static decimal ImporteVenta(CafeVenta v) =>
        (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;

    private static string NombreCliente(CafeVenta v) =>
        !string.IsNullOrWhiteSpace(v.ClienteNombreSnapshot) ? v.ClienteNombreSnapshot!
        : (v.ClienteNav?.Nombre ?? "Cliente");

    private static string Money(decimal m) => "$" + m.ToString("N2");

    /// <summary>Reemplaza los comodines del mensaje editable por los datos de la venta.</summary>
    private static string RenderCuerpo(string plantilla, CafeVenta v)
    {
        // Detalle corto para el resumen: hasta 4 renglones "Cantidad× Nombre", + "y N más".
        var items = v.Items?.ToList() ?? new List<CafeVentaItem>();
        var partes = items.Take(4).Select(i => $"{i.Cantidad}× {i.ProductoNombreSnapshot}".Trim()).ToList();
        var detalle = string.Join(", ", partes);
        if (items.Count > 4) detalle += $" y {items.Count - 4} más";
        if (string.IsNullOrWhiteSpace(detalle)) detalle = "—";

        return (plantilla ?? "")
            .Replace("{numero}", v.Numero)
            .Replace("{cliente}", NombreCliente(v))
            .Replace("{importe}", Money(ImporteVenta(v)))
            .Replace("{detalle}", detalle);
    }

    /// <summary>Detalle completo de la venta (todos los renglones) para el botón "Detalle".</summary>
    private static string ArmarDetalle(CafeVenta v)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("🧾 *Venta N° ").Append(v.Numero).Append("*\n");
        sb.Append("👤 ").Append(NombreCliente(v)).Append('\n');
        sb.Append('\n');
        var items = v.Items?.ToList() ?? new List<CafeVentaItem>();
        if (items.Count == 0)
        {
            sb.Append("(sin renglones)\n");
        }
        else
        {
            foreach (var i in items)
            {
                var sub = i.Cantidad * i.PrecioUnitario;
                sb.Append("• ").Append(i.Cantidad).Append("× ").Append(i.ProductoNombreSnapshot)
                  .Append("  —  ").Append(Money(sub)).Append('\n');
            }
        }
        sb.Append('\n').Append("💰 *Total: ").Append(Money(ImporteVenta(v))).Append('*');
        return sb.ToString();
    }

    /// <summary>Estado de cuenta del cliente para el botón "Cuenta corriente" (saldo + últimos movimientos).</summary>
    private async Task<string> ArmarCuentaCorrienteAsync(CafeVenta v)
    {
        if (v.ClienteId is null)
            return "Esta venta no tiene un cliente de cuenta corriente asociado (venta de mostrador).";

        var ec = await _clientes.GetEstadoCuentaAsync(v.ClienteId.Value);
        if (ec is null)
            return "No pude cargar la cuenta corriente del cliente.";

        var sb = new System.Text.StringBuilder();
        sb.Append("📊 *Cuenta corriente*\n");
        sb.Append("👤 ").Append(ec.ClienteNombre).Append('\n');
        sb.Append("💰 Saldo actual: *").Append(Money(ec.Saldo)).Append("*\n");
        if (ec.Saldo > 0m) sb.Append("   (el cliente DEBE)\n");
        else if (ec.Saldo < 0m) sb.Append("   (saldo a favor del cliente)\n");

        // Últimos movimientos (los más recientes primero, hasta 8).
        var ultimos = ec.Movimientos.AsEnumerable().Reverse().Take(8).ToList();
        if (ultimos.Count > 0)
        {
            sb.Append('\n').Append("Últimos movimientos:\n");
            foreach (var m in ultimos)
            {
                var signo = m.Debe > 0 ? $"+{Money(m.Debe)}" : $"-{Money(m.Haber)}";
                sb.Append("• ").Append(m.Fecha.ToString("dd/MM")).Append("  ")
                  .Append(m.Tipo).Append(' ').Append(m.Numero)
                  .Append("  ").Append(signo).Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>Genera el PDF del comprobante, lo guarda como upload público y devuelve (url, filename).
    /// Reusa exactamente la misma generación que el resto del sistema (factura ARCA / cotización).</summary>
    private async Task<(string url, string filename)> GenerarComprobanteUrlAsync(CafeVenta v, string numeroDestino, string baseUrl)
    {
        var cfg = await _db.CafeSettings.FindAsync(1);
        var bytes = await _ventas.GenerarPdfBytesAsync(v, cfg);
        var filename = CafeVentasController.BuildPdfFilename(v);
        if (!filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) filename += ".pdf";

        Directory.CreateDirectory(UploadsDir);
        var token = Guid.NewGuid().ToString("N");
        var stored = token + ".pdf";
        await System.IO.File.WriteAllBytesAsync(Path.Combine(UploadsDir, stored), bytes);

        _db.WhatsAppTwilioUploads.Add(new WhatsAppTwilioUpload
        {
            Token = token,
            OriginalFilename = filename,
            StoredFilename = stored,
            ContentType = "application/pdf",
            SizeBytes = bytes.Length,
            NumeroDestino = MetaWhatsAppService.ToInboxWhatsApp(numeroDestino),
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        });
        await _db.SaveChangesAsync();

        var url = $"{baseUrl.TrimEnd('/')}/api/whatsapp/twilio/files/{token}.pdf";
        return (url, filename);
    }

    /// <summary>Registra el saliente en la bandeja (para que la conversación se vea completa).</summary>
    private async Task RegistrarSalienteAsync(string numeroInbox, string cuerpo, string? sid,
        string? mediaUrl, string? mediaFilename, string? lineaPhoneId)
    {
        _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
        {
            Direccion = "OUTGOING",
            Numero = numeroInbox,
            Cuerpo = cuerpo,
            MediaUrl = mediaUrl,
            MediaFilename = mediaFilename,
            LineaPhoneId = lineaPhoneId,
            NumMedia = mediaUrl != null ? 1 : 0,
            TwilioMessageSid = sid,
            Canal = "CLOUD",
            Procesado = true,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }
}
