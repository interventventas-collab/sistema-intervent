using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace Api.Services;

/// <summary>
/// 2026-07-09: Lee una casilla de correo (IMAP) donde llegan las facturas de proveedores (MeLi, COLOMBRARO, etc.),
/// baja los PDF adjuntos y los deja en la carpeta "facturas recibidas" para que el match por QR los pegue solos.
/// Se configura por variables de entorno: FACTURAS_IMAP_{HOST,PORT,USER,PASS}. Si no está configurado, no hace nada.
/// </summary>
public class FacturasEmailService
{
    private readonly FileStorageService _storage;
    private readonly ILogger<FacturasEmailService> _logger;

    public FacturasEmailService(FileStorageService storage, ILogger<FacturasEmailService> logger)
    {
        _storage = storage; _logger = logger;
    }

    /// <summary>Entra a la casilla (IMAP), baja los PDF adjuntos de los mails NO leídos a la carpeta indicada y los
    /// marca como leídos. Devuelve (ok, cantidadDePdf, mensaje). La config (host/user/clave/carpeta) viene de la base.</summary>
    public async Task<(bool ok, int pdfs, string mensaje)> BajarAdjuntosAsync(
        string host, int port, string user, string pass, string? folderName, string subcarpeta, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass))
            return (false, 0, "El correo de facturas no está configurado (falta casilla, usuario o clave).");
        if (port <= 0) port = 993;

        var dir = _storage.ResolveSafe(subcarpeta);
        Directory.CreateDirectory(dir);

        int pdfs = 0, mails = 0;
        try
        {
            using var client = new ImapClient();
            await client.ConnectAsync(host, port, SecureSocketOptions.SslOnConnect, ct);
            await client.AuthenticateAsync(user, pass, ct);

            // Lee SOLO la etiqueta/carpeta indicada (ej. "Facturas" en Gmail) para no tocar el correo personal.
            // Si no se indica carpeta, usa la bandeja de entrada (ideal en una casilla dedicada).
            IMailFolder inbox = client.Inbox;
            if (!string.IsNullOrWhiteSpace(folderName))
            {
                IMailFolder? encontrada = null;
                try { encontrada = await client.GetFolderAsync(folderName, ct); } catch { }
                if (encontrada is null)
                    foreach (var f in await client.GetFoldersAsync(client.PersonalNamespaces[0], cancellationToken: ct))
                        if (string.Equals(f.Name, folderName, StringComparison.OrdinalIgnoreCase)) { encontrada = f; break; }
                if (encontrada is null) { await client.DisconnectAsync(true, ct); return (false, 0, $"No encontré la etiqueta/carpeta '{folderName}' en la casilla."); }
                inbox = encontrada;
            }
            await inbox.OpenAsync(FolderAccess.ReadWrite, ct);

            var uids = await inbox.SearchAsync(SearchQuery.NotSeen, ct);
            foreach (var uid in uids)
            {
                if (ct.IsCancellationRequested) break;
                var msg = await inbox.GetMessageAsync(uid, ct);
                bool huboPdf = false;
                foreach (var att in msg.Attachments.OfType<MimePart>())
                {
                    var nombre = att.FileName ?? att.ContentDisposition?.FileName ?? "adjunto.pdf";
                    var esPdf = nombre.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(att.ContentType?.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);
                    if (!esPdf) continue;

                    var destino = Path.Combine(dir, NombreUnico(dir, uid.Id, nombre));
                    using (var fs = File.Create(destino))
                        await att.Content.DecodeToAsync(fs, ct);
                    pdfs++; huboPdf = true;
                }
                if (huboPdf) mails++;
                // Marco leído SIEMPRE (aunque no tuviera PDF) para no reprocesar; si querés conservar, cambiar acá.
                await inbox.AddFlagsAsync(uid, MessageFlags.Seen, true, ct);
            }
            await client.DisconnectAsync(true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[FacturasCorreo] Error leyendo la casilla");
            return (false, pdfs, "No pude leer la casilla: " + ex.GetBaseException().Message);
        }
        return (true, pdfs, $"Correo: {pdfs} PDF bajados de {mails} mail(s).");
    }

    private static string NombreUnico(string dir, uint uid, string nombre)
    {
        var seguro = string.Concat(Path.GetFileNameWithoutExtension(nombre).Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        if (string.IsNullOrWhiteSpace(seguro)) seguro = "factura";
        var final = $"mail{uid}-{seguro}.pdf";
        return final;
    }
}
