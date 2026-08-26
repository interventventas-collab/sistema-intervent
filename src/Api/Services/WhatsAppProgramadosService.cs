using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-26: manda UN mensaje programado cuando le llega la hora.
///
/// Vive aparte del robot (WhatsAppProgramadosBackgroundService) para que el mismo código
/// sirva también cuando el operador toca "mandalo ahora" desde la pantalla.
///
/// Nunca tira excepción hacia afuera: siempre deja la fila resuelta (ENVIADO o ERROR con el
/// motivo escrito en castellano). Un programado que falla en silencio es peor que uno que no
/// sale: el operador se queda esperando algo que nunca pasó.
/// </summary>
public class WhatsAppProgramadosService
{
    private readonly AppDbContext _db;
    private readonly WhatsAppOutboundService _outbound;
    private readonly MetaWhatsAppService _meta;
    private readonly WaLiveNotifier _waLive;
    private readonly ILogger<WhatsAppProgramadosService> _log;

    public WhatsAppProgramadosService(AppDbContext db, WhatsAppOutboundService outbound,
        MetaWhatsAppService meta, WaLiveNotifier waLive, ILogger<WhatsAppProgramadosService> log)
    {
        _db = db; _outbound = outbound; _meta = meta; _waLive = waLive; _log = log;
    }

    /// <summary>¿Se le puede escribir texto libre a este número AHORA? (ventana de 24 hs de WhatsApp).
    /// Se mide contra el último mensaje ENTRANTE: si el cliente no escribió en las últimas 24 hs,
    /// Meta solo acepta plantillas aprobadas.</summary>
    public async Task<bool> VentanaAbiertaAsync(string numero)
    {
        var ultEntrante = await _db.WhatsAppTwilioMensajes.AsNoTracking()
            .Where(x => x.Numero == numero && x.Direccion == "INCOMING")
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => (DateTime?)x.CreatedAt)
            .FirstOrDefaultAsync();
        return ultEntrante != null && (DateTime.UtcNow - ultEntrante.Value).TotalHours < 24;
    }

    /// <summary>Manda la fila y la deja resuelta. Devuelve (ok, error para mostrar).</summary>
    public async Task<(bool Ok, string? Error)> ProcesarAsync(WhatsAppMensajeProgramado fila)
    {
        if (fila.Estado != WhatsAppMensajeProgramado.EstadoPendiente)
            return (false, "El mensaje ya no estaba pendiente.");

        fila.Intentos++;
        fila.UpdatedAt = DateTime.UtcNow;

        try
        {
            // La plantilla es la única que atraviesa la ventana cerrada. Para texto y adjunto,
            // si la ventana se cerró mientras el mensaje esperaba, NO intentamos: cortamos acá con
            // un motivo claro (si lo mandáramos igual, Meta lo rechaza y el operador no sabe por qué).
            if (fila.Tipo != WhatsAppMensajeProgramado.TipoPlantilla && !await VentanaAbiertaAsync(fila.Numero))
                return Fallar(fila, "No salió: cuando llegó la hora ya habían pasado más de 24 hs desde el último mensaje del cliente, "
                    + "y WhatsApp no deja escribir texto libre fuera de esa ventana. Programalo como plantilla si necesitás escribirle igual.");

            string? id; string canal; string? linea;

            switch (fila.Tipo)
            {
                case WhatsAppMensajeProgramado.TipoPlantilla:
                {
                    if (!_meta.IsConfigured)
                        return Fallar(fila, "No salió: WhatsApp Cloud (Meta) no está configurado, y las plantillas solo salen por ahí.");
                    var vars = LeerVariables(fila.VariablesJson);
                    id = await _meta.SendTemplateAsync(MetaWhatsAppService.NormalizeTo(fila.Numero),
                        fila.Plantilla ?? "", fila.Idioma ?? "es_AR", vars, lineaPhoneId: fila.LineaPhoneId);
                    canal = "CLOUD"; linea = fila.LineaPhoneId;
                    break;
                }
                case WhatsAppMensajeProgramado.TipoAdjunto:
                {
                    (id, canal, linea) = await _outbound.SendMediaAsync(fila.Numero, fila.MediaUrl ?? "",
                        string.IsNullOrWhiteSpace(fila.Texto) ? null : fila.Texto,
                        fila.MediaFilename, fila.LineaPhoneId);
                    break;
                }
                default:
                {
                    (id, canal, linea) = await _outbound.SendTextAsync(fila.Numero, fila.Texto ?? "", fila.LineaPhoneId);
                    break;
                }
            }

            // Sin id del proveedor NO hubo entrega. Mismo criterio que el envío normal del chat:
            // no dejamos una burbuja falsa que diga "enviado" cuando el cliente no recibió nada.
            if (string.IsNullOrEmpty(id))
                return Fallar(fila, fila.Tipo == WhatsAppMensajeProgramado.TipoPlantilla
                    ? "No salió: Meta rechazó la plantilla. Revisá el número, las variables o el medio de pago de la cuenta de WhatsApp."
                    : "No salió: WhatsApp rechazó el mensaje. Revisá el número y probá mandarlo a mano.");

            var msg = new WhatsAppTwilioMensaje
            {
                Direccion = "OUTGOING",
                Numero = fila.Numero,
                Cuerpo = TextoParaElChat(fila),
                MediaUrl = fila.Tipo == WhatsAppMensajeProgramado.TipoAdjunto ? fila.MediaUrl : null,
                MediaFilename = fila.Tipo == WhatsAppMensajeProgramado.TipoAdjunto ? fila.MediaFilename : null,
                NumMedia = fila.Tipo == WhatsAppMensajeProgramado.TipoAdjunto ? 1 : 0,
                TwilioMessageSid = id,
                Canal = canal,
                LineaPhoneId = linea,
                Procesado = true,
                CreatedAt = DateTime.UtcNow
            };
            _db.WhatsAppTwilioMensajes.Add(msg);

            fila.Estado = WhatsAppMensajeProgramado.EstadoEnviado;
            fila.EnviadoAt = DateTime.UtcNow;
            fila.Error = null;
            fila.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();

            fila.MensajeId = msg.Id;
            await _db.SaveChangesAsync();

            // Que aparezca al instante en las pantallas abiertas (escritorio y celular).
            await _waLive.AvisarAsync(fila.Numero, linea, "OUTGOING");
            return (true, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[Programados] falló el mensaje {Id} para {Numero}", fila.Id, fila.Numero);
            return Fallar(fila, $"No salió por un error del sistema: {ex.Message}");
        }
    }

    /// <summary>Deja la fila en ERROR con el motivo y lo guarda. El motivo se muestra tal cual.</summary>
    private (bool, string?) Fallar(WhatsAppMensajeProgramado fila, string motivo)
    {
        fila.Estado = WhatsAppMensajeProgramado.EstadoError;
        fila.Error = motivo.Length > 400 ? motivo[..400] : motivo;
        fila.UpdatedAt = DateTime.UtcNow;
        try { _db.SaveChanges(); }
        catch (Exception ex) { _log.LogWarning(ex, "[Programados] no pude anotar el error del mensaje {Id}", fila.Id); }
        return (false, fila.Error);
    }

    /// <summary>Qué queda escrito en la burbuja del chat. En la plantilla mostramos el cuerpo ya
    /// armado (con las variables reemplazadas); si no lo tenemos, al menos su nombre.</summary>
    private static string TextoParaElChat(WhatsAppMensajeProgramado fila)
    {
        if (fila.Tipo == WhatsAppMensajeProgramado.TipoPlantilla)
        {
            var cuerpo = MetaWhatsAppService.RenderTemplateBody(fila.CuerpoPreview, LeerVariables(fila.VariablesJson));
            return string.IsNullOrWhiteSpace(cuerpo) ? $"[Plantilla: {fila.Plantilla}]" : cuerpo;
        }
        return fila.Texto ?? "";
    }

    private static List<string> LeerVariables(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
    }
}
