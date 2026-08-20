using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-20: manda el comprobante de una venta al cliente, por mail o por WhatsApp, y deja
/// anotado cómo le fue en Cafe_VentasEnvios (lo que pintan los cartelitos 📧/📱 del listado).
///
/// Vive acá y no en el controller porque lo usan TRES lugares: la pantalla de ventas (mandar
/// ahora), el robot de la cola (mandar a los N minutos) y el reenvío. Si cada uno tuviera su
/// copia, el día que se arregle algo en uno los otros quedan con el error viejo — y acá lo que
/// se manda son facturas a clientes.
///
/// El PDF se arma SIEMPRE en el momento del envío (nunca se guarda al encolar): así, si la
/// venta se corrige dentro de la demora, sale la versión corregida sin hacer nada.
/// </summary>
public class EnvioComprobanteService
{
    private readonly AppDbContext _db;
    private readonly IntegrationService _integrations;
    private readonly WhatsAppOutboundService _outbound;
    private readonly IServiceProvider _sp;
    private readonly ILogger<EnvioComprobanteService> _log;

    private const string UploadsDir = "/data/whatsapp-uploads";
    /// <summary>Cuántos minutos espera un envío automático antes de salir. Editable desde la pantalla.</summary>
    public const string KeyDemora = "ventas.envio.demora_minutos";
    private const int DemoraDefault = 5;

    public EnvioComprobanteService(AppDbContext db, IntegrationService integrations,
        WhatsAppOutboundService outbound, IServiceProvider sp, ILogger<EnvioComprobanteService> log)
    {
        _db = db; _integrations = integrations; _outbound = outbound; _sp = sp; _log = log;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Configuración
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Minutos de espera antes de que salga un envío. 0 = sale al toque.</summary>
    public async Task<int> GetDemoraMinutosAsync()
    {
        var row = await _db.AppSettings.FindAsync(KeyDemora);
        if (row is null || !int.TryParse(row.Value, out var min)) return DemoraDefault;
        return Math.Clamp(min, 0, 240);
    }

    public async Task SetDemoraMinutosAsync(int minutos)
    {
        minutos = Math.Clamp(minutos, 0, 240);
        var row = await _db.AppSettings.FindAsync(KeyDemora);
        if (row is null) _db.AppSettings.Add(new AppSetting { Key = KeyDemora, Value = minutos.ToString(), UpdatedAt = DateTime.UtcNow });
        else { row.Value = minutos.ToString(); row.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  La cola
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Anota (o pisa) el envío de una venta por un canal. Si demoraMinutos es null usa la
    /// configurada. Devuelve la fila para que la pantalla pueda mostrar el "sale en X minutos".</summary>
    public async Task<CafeVentaEnvio> ProgramarAsync(int ventaId, string canal, string destino,
        string? lineaPhoneId = null, bool automatico = false, int? demoraMinutos = null)
    {
        var demora = demoraMinutos ?? await GetDemoraMinutosAsync();
        var fila = await _db.CafeVentasEnvios.FirstOrDefaultAsync(x => x.VentaId == ventaId && x.Canal == canal);
        if (fila is null)
        {
            fila = new CafeVentaEnvio { VentaId = ventaId, Canal = canal };
            _db.CafeVentasEnvios.Add(fila);
        }
        fila.Estado = CafeVentaEnvio.EstadoPendiente;
        fila.Destino = destino;
        fila.LineaPhoneId = lineaPhoneId;
        fila.ProgramadoPara = DateTime.UtcNow.AddMinutes(demora);
        fila.EnviadoAt = null;
        fila.Error = null;
        fila.Intentos = 0;
        fila.Automatico = automatico;
        fila.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return fila;
    }

    /// <summary>Cancela un envío que todavía no salió. Devuelve false si ya había salido.</summary>
    public async Task<bool> CancelarAsync(int ventaId, string canal)
    {
        var fila = await _db.CafeVentasEnvios.FirstOrDefaultAsync(x => x.VentaId == ventaId && x.Canal == canal);
        if (fila is null || fila.Estado != CafeVentaEnvio.EstadoPendiente) return false;
        fila.Estado = CafeVentaEnvio.EstadoCancelado;
        fila.ProgramadoPara = null;
        fila.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>Cancela TODO lo que esté esperando salir para esa venta. Se llama al anular una
    /// venta: sin esto, anulás una factura y a los tres minutos le llega igual al cliente.</summary>
    public async Task<int> CancelarPendientesDeVentaAsync(int ventaId, string motivo)
    {
        var filas = await _db.CafeVentasEnvios
            .Where(x => x.VentaId == ventaId && x.Estado == CafeVentaEnvio.EstadoPendiente).ToListAsync();
        foreach (var f in filas)
        {
            f.Estado = CafeVentaEnvio.EstadoCancelado;
            f.ProgramadoPara = null;
            f.Error = motivo;
            f.UpdatedAt = DateTime.UtcNow;
        }
        if (filas.Count > 0) await _db.SaveChangesAsync();
        return filas.Count;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  El envío
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Manda una fila de la cola AHORA y deja anotado el resultado. Lo usa el robot
    /// cuando llega la hora y el botón "Mandar ya" de la pantalla.</summary>
    public async Task<(bool ok, string? error)> ProcesarAsync(CafeVentaEnvio fila)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).ThenInclude(i => i.ProductoNav)
            .FirstOrDefaultAsync(x => x.Id == fila.VentaId);
        if (v is null) return await MarcarErrorAsync(fila, "La venta ya no existe.");

        // 1) Venta anulada → no sale nunca (aunque alguien la haya encolado antes de anularla).
        if (string.Equals(v.Estado, "anulado", StringComparison.OrdinalIgnoreCase))
        {
            fila.Estado = CafeVentaEnvio.EstadoCancelado;
            fila.ProgramadoPara = null;
            fila.Error = "La venta se anuló antes de que saliera.";
            fila.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return (false, fila.Error);
        }

        // 2) Factura de ARCA sin CAE → NO sale todavía: espera a que esté aprobada de verdad.
        //    Si sale antes y hay que rehacerla, el cliente ya la recibió. Se reintenta en el
        //    próximo ciclo; a las 24 hs se da por vencida y queda el error visible.
        if (EsFacturaArca(v) && !EstaAutorizada(v))
        {
            if (fila.CreatedAt < DateTime.UtcNow.AddHours(-24))
                return await MarcarErrorAsync(fila, "La factura nunca quedó aprobada en ARCA, así que no se le mandó al cliente.");
            fila.Intentos++;
            fila.ProgramadoPara = DateTime.UtcNow.AddMinutes(2);
            fila.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return (false, "Esperando el CAE de ARCA.");
        }

        try
        {
            var (ok, error) = fila.Canal == CafeVentaEnvio.CanalEmail
                ? await EnviarEmailAsync(v, fila.Destino ?? "")
                : await EnviarWhatsappAsync(v, fila.Destino ?? "", fila.LineaPhoneId);
            if (!ok) return await MarcarErrorAsync(fila, error ?? "No se pudo enviar.");

            fila.Estado = CafeVentaEnvio.EstadoEnviado;
            fila.EnviadoAt = DateTime.UtcNow;
            fila.ProgramadoPara = null;
            fila.Error = null;
            fila.Intentos++;
            fila.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return (true, null);
        }
        catch (Exception ex)
        {
            return await MarcarErrorAsync(fila, ex.Message);
        }
    }

    private async Task<(bool ok, string? error)> MarcarErrorAsync(CafeVentaEnvio fila, string error)
    {
        fila.Estado = CafeVentaEnvio.EstadoError;
        fila.ProgramadoPara = null;
        fila.Error = error.Length > 400 ? error[..400] : error;
        fila.Intentos++;
        fila.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (false, fila.Error);
    }

    public static bool EsFacturaArca(CafeVenta v) =>
        v.TipoComprobante is "FA" or "FB" or "FC" or "NCA" or "NCB" or "NCC";

    public static bool EstaAutorizada(CafeVenta v) =>
        v.ArcaEstado == "autorizado" && !string.IsNullOrEmpty(v.ArcaCae)
        && v.ArcaCbteNro.HasValue && v.ArcaPtoVta.HasValue && v.ArcaCbteTipoNum.HasValue;

    /// <summary>Total que ve el cliente: con IVA si es factura de ARCA, si no el total de la venta.
    /// Es la MISMA cuenta que usa el cuerpo del mail, para que nunca digan cosas distintas.</summary>
    public static decimal MontoCliente(CafeVenta v) =>
        (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;

    /// <summary>Texto que acompaña al PDF en WhatsApp. SIN link (2026-08-20, pedido del dueño):
    /// va el comprobante adjunto y el total escrito, que es lo que el cliente necesita ver de una.</summary>
    public static string CaptionWhatsApp(CafeVenta v)
    {
        var hola = string.IsNullOrWhiteSpace(v.ClienteNombreSnapshot) ? "Hola!" : $"Hola {v.ClienteNombreSnapshot}!";
        return $"{hola} Te paso el comprobante {v.Numero}.\n" +
               $"Total: ${MontoCliente(v):N2}\n\n" +
               "Cualquier cosa avisame. Gracias!";
    }

    // ---- Mail ----

    /// <summary>Manda el PDF por mail usando la casilla configurada en Integraciones (email-smtp).</summary>
    public async Task<(bool ok, string? error)> EnviarEmailAsync(CafeVenta v, string to)
    {
        if (string.IsNullOrWhiteSpace(to)) return (false, "El cliente no tiene correo cargado.");

        var integration = await _integrations.GetByProviderAsync("email-smtp");
        if (integration is null) return (false, "No hay configuración de email. Configurala en Integraciones.");
        var secret = await _integrations.GetSecretAsync("email-smtp");
        if (string.IsNullOrEmpty(secret)) return (false, "No hay contraseña SMTP configurada.");

        string smtpHost = "smtp.gmail.com"; int smtpPort = 587; bool smtpTls = true;
        string fromAddress = "", fromName = "", username = "";
        if (!string.IsNullOrEmpty(integration.Settings))
        {
            try
            {
                var root = System.Text.Json.JsonDocument.Parse(integration.Settings).RootElement;
                if (root.TryGetProperty("smtpHost", out var h)) smtpHost = h.GetString() ?? smtpHost;
                if (root.TryGetProperty("smtpPort", out var p)) smtpPort = p.GetInt32();
                if (root.TryGetProperty("smtpTls", out var t)) smtpTls = t.GetBoolean();
                if (root.TryGetProperty("fromAddress", out var f)) fromAddress = f.GetString() ?? "";
                if (root.TryGetProperty("fromName", out var n)) fromName = n.GetString() ?? "";
                if (root.TryGetProperty("username", out var u)) username = u.GetString() ?? "";
            }
            catch { }
        }
        if (string.IsNullOrEmpty(fromAddress)) return (false, "No hay email de remitente configurado en Integraciones.");

        var cfg = await _db.CafeSettings.FindAsync(1);
        var pdfBytes = await GenerarPdfAsync(v, cfg);

        var subject = $"Comprobante {v.Numero} - {cfg?.NegocioNombre ?? "Frikaf"}";
        var body = $"Hola{(string.IsNullOrWhiteSpace(v.ClienteNombreSnapshot) ? "" : " " + v.ClienteNombreSnapshot)},\n\n" +
                   $"Te adjuntamos el comprobante {v.Numero} por ${MontoCliente(v):N2}.\n\n" +
                   "Cualquier consulta, escribinos.\n\n" +
                   $"Saludos,\n{cfg?.NegocioNombre ?? "Frikaf"}";

        using var client = new System.Net.Mail.SmtpClient(smtpHost, smtpPort)
        {
            Credentials = new System.Net.NetworkCredential(string.IsNullOrEmpty(username) ? fromAddress : username, secret),
            EnableSsl = smtpTls,
            Timeout = 30000
        };
        using var message = new System.Net.Mail.MailMessage
        {
            From = new System.Net.Mail.MailAddress(fromAddress, string.IsNullOrEmpty(fromName) ? fromAddress : fromName),
            Subject = subject,
            Body = body,
            IsBodyHtml = false
        };
        message.To.Add(to);
        using var ms = new MemoryStream(pdfBytes);
        message.Attachments.Add(new System.Net.Mail.Attachment(ms, NombrePdf(v), "application/pdf"));
        await client.SendMailAsync(message);
        return (true, null);
    }

    // ---- WhatsApp ----

    /// <summary>Manda el PDF por la API oficial de Meta, por la línea indicada (null = la de por
    /// defecto). Meta descarga el archivo de una URL nuestra, así que el PDF se deja en disco con
    /// un token temporal, igual que el envío manual de la pantalla de WhatsApp.</summary>
    public async Task<(bool ok, string? error)> EnviarWhatsappAsync(CafeVenta v, string numero, string? lineaPhoneId)
    {
        if (string.IsNullOrWhiteSpace(numero))
            return (false, "El cliente no tiene teléfono cargado ni un chat de WhatsApp vinculado.");
        if (!_outbound.AnyConfigured) return (false, "WhatsApp no está configurado.");

        var baseUrl = (await _db.AppSettings.FindAsync("mapeo.public_base_url"))?.Value?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            return (false, "Falta configurar la dirección pública del sistema (Mapeo → dirección pública); sin eso Meta no puede bajar el PDF.");

        var numeroNorm = MetaWhatsAppService.ToInboxWhatsApp(numero);
        var cfg = await _db.CafeSettings.FindAsync(1);
        var pdfBytes = await GenerarPdfAsync(v, cfg);
        var filename = NombrePdf(v);

        Directory.CreateDirectory(UploadsDir);
        var token = Guid.NewGuid().ToString("N");
        var stored = token + ".pdf";
        await File.WriteAllBytesAsync(Path.Combine(UploadsDir, stored), pdfBytes);
        _db.WhatsAppTwilioUploads.Add(new WhatsAppTwilioUpload
        {
            Token = token,
            OriginalFilename = filename,
            StoredFilename = stored,
            ContentType = "application/pdf",
            SizeBytes = pdfBytes.Length,
            NumeroDestino = numeroNorm,
            CreatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        });
        await _db.SaveChangesAsync();

        var mediaUrl = $"{baseUrl}/api/whatsapp/twilio/files/{token}.pdf";
        var caption = CaptionWhatsApp(v);
        var (sid, canal, lin) = await _outbound.SendMediaAsync(numeroNorm, mediaUrl, caption, filename, lineaPhoneId);
        if (string.IsNullOrEmpty(sid))
            return (false, "Meta no aceptó el mensaje. Suele pasar si el cliente no te escribió en las últimas 24 hs.");

        // Queda en la bandeja como cualquier otro mensaje saliente, así se ve en la charla.
        _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
        {
            Direccion = "OUTGOING",
            Numero = numeroNorm,
            Cuerpo = caption,
            MediaUrl = mediaUrl,
            MediaFilename = filename,
            NumMedia = 1,
            TwilioMessageSid = sid,
            Canal = canal,
            LineaPhoneId = lin,
            Procesado = true,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return (true, null);
    }

    // ---- PDF ----

    /// <summary>Reusa la MISMA generación del botón "Descargar PDF" (factura ARCA con CAE si está
    /// autorizada, cotización si no). El controller está registrado en DI justamente para esto.</summary>
    private async Task<byte[]> GenerarPdfAsync(CafeVenta v, CafeSetting? cfg)
    {
        var ventas = _sp.GetRequiredService<Controllers.CafeVentasController>();
        return await ventas.GenerarPdfBytesAsync(v, cfg);
    }

    private static string NombrePdf(CafeVenta v)
    {
        var f = Controllers.CafeVentasController.BuildPdfFilename(v);
        return f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? f : f + ".pdf";
    }
}
