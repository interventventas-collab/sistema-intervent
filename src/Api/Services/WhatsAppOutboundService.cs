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
    private readonly InstagramDmService _ig;
    private readonly AppDbContext _db;
    private readonly ILogger<WhatsAppOutboundService> _logger;

    /// <summary>Prefijo con el que se guarda el número de una conversación de Instagram:
    /// "ig:{IGSID}" (IGSID = id del usuario de Instagram). Así conviven WA e IG en la misma tabla.</summary>
    public const string IgPrefix = "ig:";

    public WhatsAppOutboundService(MetaWhatsAppService meta, TwilioWhatsAppService twilio,
        InstagramDmService ig, AppDbContext db, ILogger<WhatsAppOutboundService> logger)
    {
        _meta = meta;
        _twilio = twilio;
        _ig = ig;
        _db = db;
        _logger = logger;
    }

    /// <summary>True si al menos un canal (Meta WhatsApp, Twilio o Instagram) está configurado.</summary>
    public bool AnyConfigured => _meta.IsConfigured || _twilio.IsConfigured || _ig.IsConfigured;

    // 2026-08-01: devuelve además la Línea usada (para guardarla en el saliente y que el mismo
    // contacto en 2 líneas NO se cruce). lineaOverride = mandar por una línea concreta (la del chat abierto).
    public async Task<(string? Id, string Canal, string? Linea)> SendTextAsync(string numero, string body, string? lineaOverride = null)
    {
        var (canal, linea) = await PickCanalYLineaAsync(numero, lineaOverride);
        if (canal == "INSTAGRAM")
            return (await _ig.SendTextAsync(linea ?? "", IgsidDe(numero), body), "INSTAGRAM", linea);
        if (canal == "CLOUD")
            return (await _meta.SendTextAsync(numero, body, lineaPhoneId: linea), "CLOUD", linea);
        return (await _twilio.SendTextAsync(numero, body), "TWILIO", null);
    }

    /// <summary>Saca el IGSID puro del "ig:{IGSID}" guardado como número de la conversación.</summary>
    private static string IgsidDe(string numero)
        => numero.StartsWith(IgPrefix, StringComparison.Ordinal) ? numero[IgPrefix.Length..] : numero;

    /// <summary>Envía un adjunto. <paramref name="nombreArchivo"/> es el nombre ORIGINAL (ej "factura.pdf"):
    /// se usa para saber si va como documento o como imagen. OJO: la URL del adjunto es del tipo
    /// /files/{token} y NO tiene extensión, por eso NO se puede deducir el tipo desde la URL.</summary>
    public async Task<(string? Id, string Canal, string? Linea)> SendMediaAsync(string numero, string mediaUrl, string? caption = null, string? nombreArchivo = null, string? lineaOverride = null)
    {
        var (canal, linea) = await PickCanalYLineaAsync(numero, lineaOverride);
        if (canal == "INSTAGRAM")
        {
            // IG DM solo manda imágenes por link; para PDF/otros mandamos el link como texto.
            var esImagen = !EsDocumento(nombreArchivo);
            if (esImagen)
                return (await _ig.SendImageAsync(linea ?? "", IgsidDe(numero), mediaUrl), "INSTAGRAM", linea);
            var texto = string.IsNullOrWhiteSpace(caption) ? mediaUrl : $"{caption}\n{mediaUrl}";
            return (await _ig.SendTextAsync(linea ?? "", IgsidDe(numero), texto), "INSTAGRAM", linea);
        }
        if (canal == "CLOUD")
        {
            // 2026-08-01: si es audio (nota de voz), va como type:audio → llega con el play nativo.
            if (EsAudio(nombreArchivo))
            {
                var idAudio = await _meta.SendAudioAsync(numero, mediaUrl, lineaPhoneId: linea);
                return (idAudio, "CLOUD", linea);
            }
            var esDoc = EsDocumento(nombreArchivo);
            var id = await _meta.SendMediaAsync(numero, mediaUrl, caption, esDoc, nombreArchivo, lineaPhoneId: linea);
            return (id, "CLOUD", linea);
        }
        return (await _twilio.SendMediaAsync(numero, mediaUrl, caption), "TWILIO", null);
    }

    /// <summary>Decide si el adjunto es un AUDIO (nota de voz) por su extensión.</summary>
    private static bool EsAudio(string? nombreArchivo)
    {
        if (string.IsNullOrWhiteSpace(nombreArchivo)) return false;
        var ext = Path.GetExtension(nombreArchivo).ToLowerInvariant();
        return ext is ".ogg" or ".oga" or ".opus" or ".mp3" or ".m4a" or ".aac" or ".amr" or ".wav";
    }

    /// <summary>Elige el canal según el último entrante de ese número; fallback: Meta si está, sino Twilio.
    /// 2026-07-23 (multi-línea): además devuelve por QUÉ línea de Meta venía conversando ese número,
    /// así la respuesta sale siempre por la misma línea donde el cliente escribió (null = la default).</summary>
    private async Task<(string Canal, string? Linea)> PickCanalYLineaAsync(string numero, string? lineaOverride = null)
    {
        var ultimo = await _db.WhatsAppTwilioMensajes
            .Where(m => m.Numero == numero && m.Direccion == "INCOMING")
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Canal, m.LineaPhoneId })
            .FirstOrDefaultAsync();

        // Instagram: se reconoce por el prefijo "ig:" del número. La "línea" es el IG User ID
        // de la cuenta (guardado en LineaPhoneId del último entrante; o el override del chat abierto).
        if (numero.StartsWith(IgPrefix, StringComparison.Ordinal))
            return ("INSTAGRAM", string.IsNullOrEmpty(lineaOverride) ? ultimo?.LineaPhoneId : lineaOverride);

        // 2026-08-01: si el llamador indica una línea concreta (la del chat abierto), respondemos por
        // ESA línea de Cloud API — así el mismo contacto escribiendo a 2 líneas nuestras no se cruza.
        if (!string.IsNullOrEmpty(lineaOverride) && _meta.IsConfigured) return ("CLOUD", lineaOverride);

        if (ultimo?.Canal == "CLOUD" && _meta.IsConfigured) return ("CLOUD", ultimo.LineaPhoneId);
        if (ultimo?.Canal == "TWILIO" && _twilio.IsConfigured) return ("TWILIO", null);
        return (_meta.IsConfigured ? "CLOUD" : "TWILIO", null);
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
