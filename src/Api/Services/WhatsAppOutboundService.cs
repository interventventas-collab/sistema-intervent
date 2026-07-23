using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// "Repartidor" de WhatsApp saliente: decide por qué proveedor mandar cada mensaje —
/// la API oficial de Meta (Cloud API) o Twilio.
///
/// Regla: responde por el MISMO canal por el que venía conversando ese número (el último
/// mensaje entrante). Si no hay historial, prefiere Meta cuando está configurado, y si no,
/// cae a Twilio. Mientras META_WA_* esté vacío, SIEMPRE usa Twilio → no cambia nada de lo
/// que ya funciona en producción.
///
/// Devuelve (Id del proveedor: SID de Twilio o wamid de Meta, Canal usado: "TWILIO"/"CLOUD").
/// </summary>
public class WhatsAppOutboundService
{
    private readonly MetaWhatsAppService _meta;
    private readonly TwilioWhatsAppService _twilio;
    private readonly AppDbContext _db;
    private readonly ILogger<WhatsAppOutboundService> _logger;

    public WhatsAppOutboundService(MetaWhatsAppService meta, TwilioWhatsAppService twilio,
        AppDbContext db, ILogger<WhatsAppOutboundService> logger)
    {
        _meta = meta;
        _twilio = twilio;
        _db = db;
        _logger = logger;
    }

    /// <summary>True si al menos un proveedor (Meta o Twilio) está configurado.</summary>
    public bool AnyConfigured => _meta.IsConfigured || _twilio.IsConfigured;

    public async Task<(string? Id, string Canal)> SendTextAsync(string numero, string body)
    {
        var canal = await PickCanalAsync(numero);
        if (canal == "CLOUD")
            return (await _meta.SendTextAsync(numero, body), "CLOUD");
        return (await _twilio.SendTextAsync(numero, body), "TWILIO");
    }

    /// <summary>Envía un adjunto. <paramref name="nombreArchivo"/> es el nombre ORIGINAL (ej "factura.pdf"):
    /// se usa para saber si va como documento o como imagen. OJO: la URL del adjunto es del tipo
    /// /files/{token} y NO tiene extensión, por eso NO se puede deducir el tipo desde la URL.</summary>
    public async Task<(string? Id, string Canal)> SendMediaAsync(string numero, string mediaUrl, string? caption = null, string? nombreArchivo = null)
    {
        var canal = await PickCanalAsync(numero);
        if (canal == "CLOUD")
        {
            var esDoc = EsDocumento(nombreArchivo);
            var id = await _meta.SendMediaAsync(numero, mediaUrl, caption, esDoc, nombreArchivo);
            return (id, "CLOUD");
        }
        return (await _twilio.SendMediaAsync(numero, mediaUrl, caption), "TWILIO");
    }

    /// <summary>Elige el canal según el último entrante de ese número; fallback: Meta si está, sino Twilio.</summary>
    private async Task<string> PickCanalAsync(string numero)
    {
        var ultimoCanal = await _db.WhatsAppTwilioMensajes
            .Where(m => m.Numero == numero && m.Direccion == "INCOMING")
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => m.Canal)
            .FirstOrDefaultAsync();

        if (ultimoCanal == "CLOUD" && _meta.IsConfigured) return "CLOUD";
        if (ultimoCanal == "TWILIO" && _twilio.IsConfigured) return "TWILIO";
        return _meta.IsConfigured ? "CLOUD" : "TWILIO";
    }

    /// <summary>Decide si el adjunto va como DOCUMENTO. Criterio: solo las imágenes reales
    /// (.jpg/.jpeg/.png/.webp) van como "image"; TODO lo demás (pdf, excel, word, o sin
    /// extensión conocida) va como "document", que acepta cualquier tipo de archivo.</summary>
    private static bool EsDocumento(string? nombreArchivo)
    {
        if (string.IsNullOrWhiteSpace(nombreArchivo)) return true; // sin nombre → documento (más seguro)
        var ext = Path.GetExtension(nombreArchivo).ToLowerInvariant();
        return ext is not (".jpg" or ".jpeg" or ".png" or ".webp");
    }
}
