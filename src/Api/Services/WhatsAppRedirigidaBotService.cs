using System.Globalization;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-26 (pedido del dueño): CARGAR UNA REDIRIGIDA ESCRIBIENDO "redi" AL WHATSAPP FRIKAF.
///
/// Versión SIMPLE (la primera tenía listas de clientes y el dueño dijo que demoraba mucho: "que el
/// trabajo grande de elegir remitente y destinatario sea con la PC desde la bolsita"). Por WhatsApp solo:
///   1) un mensaje escrito con los datos (quién la mandó, a quién le llegó, lo que sepa)
///   2) el importe
///   3) la foto del comprobante (o "Listo") → queda PENDIENTE en la bolsita, sin más preguntas.
/// Fotos/PDF se aceptan en cualquier momento de la charla. Cliente y quién la recibe se eligen en la PC
/// al volcar. NO toca plata: la cobranza la hace la oficina.
/// </summary>
public class WhatsAppRedirigidaBotService
{
    /// <summary>La línea FRIKAF (11 2252-5458). Sin línea (null) = la default, que también es FRIKAF.</summary>
    public const string LineaFrikaf = "1242473442281483";
    private const int MinutosParaContestar = 30;

    private readonly AppDbContext _db;
    private readonly MetaWhatsAppService _meta;
    private readonly ILogger<WhatsAppRedirigidaBotService> _log;

    public WhatsAppRedirigidaBotService(AppDbContext db, MetaWhatsAppService meta, ILogger<WhatsAppRedirigidaBotService> log)
    {
        _db = db; _meta = meta; _log = log;
    }

    private static readonly HashSet<string> Arranque = new(StringComparer.OrdinalIgnoreCase)
    { "redi", "redirigida", "redirigido", "redirigidas" };

    /// <summary>true = lo atendió este asistente (el webhook no sigue). false = no era para acá.</summary>
    public async Task<bool> TryHandleAsync(string fromWaId, string numero, string? tipo,
        string? idInteractivo, string? cuerpo, string? lineaId, string? mediaUrl, string? mediaNombre)
    {
        if (lineaId is not null && lineaId != LineaFrikaf) return false;
        try
        {
            var texto = (cuerpo ?? "").Trim();
            if (tipo == "text" && Arranque.Contains(texto))
            {
                var aut = await BuscarAutorizadoAsync(numero);
                if (aut is null)
                {
                    _log.LogInformation("[BotRedi] '{Num}' escribió redi pero no está autorizado", numero);
                    return false;
                }
                await IniciarAsync(fromWaId, numero, aut.Nombre, lineaId);
                return true;
            }

            var esBoton = !string.IsNullOrEmpty(idInteractivo) && idInteractivo.StartsWith("redi:", StringComparison.Ordinal);
            var p = await BorradorAsync(numero);
            if (p is null)
            {
                if (esBoton) await ResponderAsync(fromWaId, numero, "Se venció la carga anterior. Escribí *redi* para empezar de nuevo.", lineaId);
                return esBoton;
            }

            if (esBoton)
            {
                if (idInteractivo == "redi:salir") return await CancelarAsync(fromWaId, numero, p, lineaId);
                if (idInteractivo == "redi:listo") return await TerminarAsync(fromWaId, numero, p, lineaId);
                return true;
            }

            // Foto / PDF / archivo: se acepta en cualquier momento de la charla.
            if (tipo is "image" or "document" or "video" or "audio" or "sticker")
            {
                await AgregarAdjuntoAsync(fromWaId, numero, p, mediaUrl, mediaNombre, lineaId);
                // Si vino con texto al pie y todavía no había mensaje, ese texto es el mensaje.
                if (string.IsNullOrEmpty(p.Mensaje) && texto.Length > 0) { p.Mensaje = Recortar(texto, 1000); await Paso(p, "importe"); }
                await SeguirAsync(fromWaId, numero, p, lineaId, recibida: true);
                return true;
            }
            if (tipo != "text" || texto.Length == 0) return true;
            if (EsCancelar(texto)) return await CancelarAsync(fromWaId, numero, p, lineaId);

            switch (p.Paso)
            {
                case "mensaje":
                    p.Mensaje = Recortar(texto, 1000);
                    await Paso(p, "importe");
                    await SeguirAsync(fromWaId, numero, p, lineaId);
                    return true;
                case "importe":
                    var monto = MontoParser.Parse(texto);
                    if (monto is null || monto <= 0)
                    {
                        await ResponderAsync(fromWaId, numero, "No entendí el importe. Escribí solo el número, por ejemplo *45000*.", lineaId);
                        return true;
                    }
                    p.Importe = monto.Value;
                    await Paso(p, "foto");
                    await SeguirAsync(fromWaId, numero, p, lineaId);
                    return true;
                default:
                    // En el paso de la foto, un texto se suma al mensaje (por si se acordó de algo).
                    p.Mensaje = Recortar(string.IsNullOrEmpty(p.Mensaje) ? texto : $"{p.Mensaje} · {texto}", 1000);
                    await Paso(p, "foto");
                    await SeguirAsync(fromWaId, numero, p, lineaId);
                    return true;
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[BotRedi] error atendiendo a {Num}", numero);
            try { await ResponderAsync(fromWaId, numero, "Uf, algo falló cargando la redirigida. Escribí *redi* para empezar de nuevo.", lineaId); } catch { }
            return true;
        }
    }

    private async Task IniciarAsync(string fromWaId, string numero, string nombre, string? lineaId)
    {
        // Una sola carga por número: si había una a medias, se descarta.
        var viejos = await _db.CafeRedirigidasPendientes.Include(x => x.Adjuntos)
            .Where(x => x.EnviadoNumero == numero && x.Estado == "BORRADOR").ToListAsync();
        foreach (var v in viejos) { _db.CafeRedirigidasPendientesAdjuntos.RemoveRange(v.Adjuntos); _db.CafeRedirigidasPendientes.Remove(v); }
        _db.CafeRedirigidasPendientes.Add(new CafeRedirigidaPendiente
        {
            Estado = "BORRADOR", Paso = "mensaje", ExpiraAt = DateTime.UtcNow.AddMinutes(MinutosParaContestar),
            EnviadoPor = nombre, EnviadoNumero = numero
        });
        await _db.SaveChangesAsync();
        await ResponderAsync(fromWaId, numero,
            $"🔁 *Cargar una REDIRIGIDA*\nHola {nombre} 👋\n\nEscribí en un mensaje los datos: *quién la mandó y a quién le llegó*, lo que sepas.\n_(escribí *salir* para cancelar)_", lineaId);
    }

    /// <summary>Pregunta lo que falte: mensaje → importe → foto.</summary>
    private async Task SeguirAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string? lineaId, bool recibida = false)
    {
        var ok = recibida ? "📎 Foto recibida.\n\n" : "";
        if (string.IsNullOrEmpty(p.Mensaje))
        {
            await ResponderAsync(fromWaId, numero, ok + "Escribí en un mensaje *quién la mandó y a quién le llegó*.", lineaId);
            return;
        }
        if (p.Importe <= 0)
        {
            await Paso(p, "importe");
            await ResponderAsync(fromWaId, numero, ok + "*¿Cuánto?* Escribí el importe (ej: 45000).", lineaId);
            return;
        }
        var cant = await _db.CafeRedirigidasPendientesAdjuntos.CountAsync(a => a.PendienteId == p.Id);
        var cuerpo = cant == 0
            ? $"💲 {Money(p.Importe)}\n\n📷 *Mandá la foto del comprobante*, o tocá *Listo* si no hay."
            : $"{ok}¿Mandás otra? Si no, tocá *Listo*.";
        var sid = await _meta.SendButtonsAsync(fromWaId, cuerpo,
            new List<(string, string)> { ("redi:listo", cant == 0 ? "✅ Listo, sin foto" : "✅ Listo"), ("redi:salir", "❌ Cancelar") },
            lineaPhoneId: lineaId);
        await RegistrarAsync(numero, cuerpo, sid, lineaId);
    }

    private async Task<bool> TerminarAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string? lineaId)
    {
        if (string.IsNullOrEmpty(p.Mensaje) || p.Importe <= 0) { await SeguirAsync(fromWaId, numero, p, lineaId); return true; }
        p.Estado = "PENDIENTE"; p.Paso = null; p.ExpiraAt = null;
        p.CreatedAt = DateTime.UtcNow; p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        var cant = await _db.CafeRedirigidasPendientesAdjuntos.CountAsync(a => a.PendienteId == p.Id);
        await ResponderAsync(fromWaId, numero,
            $"✅ *Listo, quedó en la bolsita 💰*\n{Money(p.Importe)} · «{p.Mensaje}»{(cant > 0 ? $" · 📎 {cant}" : "")}\nEn la oficina eligen el cliente y a quién le llegó.", lineaId);
        return true;
    }

    private async Task<bool> CancelarAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string? lineaId)
    {
        var adj = await _db.CafeRedirigidasPendientesAdjuntos.Where(a => a.PendienteId == p.Id).ToListAsync();
        _db.CafeRedirigidasPendientesAdjuntos.RemoveRange(adj);
        _db.CafeRedirigidasPendientes.Remove(p);
        await _db.SaveChangesAsync();
        await ResponderAsync(fromWaId, numero, "👍 Listo, cancelé la redirigida. Cuando quieras, escribí *redi* para empezar de nuevo.", lineaId);
        return true;
    }

    private async Task AgregarAdjuntoAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string? mediaUrl, string? mediaNombre, string? lineaId)
    {
        // El webhook ya bajó el archivo de Meta y lo guardó en /data/whatsapp-uploads: la URL termina
        // en /files/{token}{ext}. Con el token se encuentra el archivo guardado.
        var token = string.IsNullOrEmpty(mediaUrl) ? null : Path.GetFileNameWithoutExtension(mediaUrl.Split('?')[0]);
        var up = token is null ? null : await _db.WhatsAppTwilioUploads.AsNoTracking().FirstOrDefaultAsync(u => u.Token == token);
        if (up is null)
        {
            await ResponderAsync(fromWaId, numero, "⚠️ No pude guardar ese archivo. Probá mandarlo de nuevo.", lineaId);
            return;
        }
        _db.CafeRedirigidasPendientesAdjuntos.Add(new CafeRedirigidaPendienteAdjunto
        {
            PendienteId = p.Id, StoredFilename = up.StoredFilename,
            NombreOriginal = Recortar(mediaNombre ?? up.OriginalFilename, 260),
            MimeType = up.ContentType, Tamano = up.SizeBytes
        });
        await Paso(p, p.Paso ?? "foto");
    }

    // ─────────────── Estado ───────────────

    private async Task<CafeRedirigidaPendiente?> BorradorAsync(string numero)
    {
        var p = await _db.CafeRedirigidasPendientes
            .Where(x => x.EnviadoNumero == numero && x.Estado == "BORRADOR")
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        if (p is null) return null;
        if (p.ExpiraAt < DateTime.UtcNow)
        {
            var adj = await _db.CafeRedirigidasPendientesAdjuntos.Where(a => a.PendienteId == p.Id).ToListAsync();
            _db.CafeRedirigidasPendientesAdjuntos.RemoveRange(adj);
            _db.CafeRedirigidasPendientes.Remove(p);
            await _db.SaveChangesAsync();
            return null;
        }
        return p;
    }

    private async Task Paso(CafeRedirigidaPendiente p, string paso)
    {
        p.Paso = paso;
        p.ExpiraAt = DateTime.UtcNow.AddMinutes(MinutosParaContestar);
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    /// <summary>Misma lista que el PAGO (PagosMovil_WaAutorizados), incluidos los "solo redirigida".
    /// Tolerante por los últimos 10 dígitos, igual que el PAGO.</summary>
    private async Task<PagosMovilWaAutorizado?> BuscarAutorizadoAsync(string numero)
    {
        var activos = await _db.PagosMovilWaAutorizados.AsNoTracking().Where(a => a.Activo).ToListAsync();
        var exacto = activos.FirstOrDefault(a => a.Numero == numero);
        if (exacto is not null) return exacto;
        var inDig = SoloDigitos(numero);
        if (inDig.Length < 10) return null;
        var cola = inDig[^10..];
        return activos.FirstOrDefault(a => { var d = SoloDigitos(a.Numero); return d.Length >= 10 && d[^10..] == cola; });
    }

    private static string SoloDigitos(string s) => new((s ?? "").Where(char.IsDigit).ToArray());

    private static readonly HashSet<string> Cancelar = new(StringComparer.OrdinalIgnoreCase)
    { "cancelar", "cancela", "salir", "chau", "basta" };
    private static bool EsCancelar(string t) => Cancelar.Contains((t ?? "").Trim());

    // ─────────────── Envío / registro en el chat ───────────────

    private async Task ResponderAsync(string fromWaId, string numero, string texto, string? lineaId)
    {
        var sid = await _meta.SendTextAsync(fromWaId, texto, lineaPhoneId: lineaId);
        await RegistrarAsync(numero, texto, sid, lineaId);
    }

    private async Task RegistrarAsync(string numero, string cuerpo, string? sid, string? lineaId)
    {
        _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
        {
            Direccion = "OUTGOING", Numero = numero, Cuerpo = cuerpo, LineaPhoneId = lineaId,
            TwilioMessageSid = sid, Canal = "CLOUD", Procesado = true, CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
    }

    private static string Recortar(string s, int max) => string.IsNullOrEmpty(s) ? s : (s.Length <= max ? s : s[..(max - 1)] + "…");

    private static string Money(decimal v)
        => "$" + v.ToString(v % 1 == 0 ? "#,##0" : "#,##0.00", CultureInfo.InvariantCulture).Replace(",", "#").Replace(".", ",").Replace("#", ".");
}
