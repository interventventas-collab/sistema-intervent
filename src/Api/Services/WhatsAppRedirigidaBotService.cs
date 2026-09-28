using System.Globalization;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Atajos por WhatsApp (línea FRIKAF, 11 2252-5458) para los números autorizados
/// (PagosMovil_WaAutorizados, la misma lista del PAGO, incluidos los "solo redirigida"):
///
/// #redi (2026-09-28, versión en UN mensaje — pedido del dueño: "que con escribir un mensaje con
///   #redi adentro y un adjunto ya se cargue, que solo pida confirmar... y que deje agregar más"):
///     "#redi la carnicería el cano, le llegó a Gabriel, 50000"  (+ foto con ese texto al pie)
///   → resumen con [✅ Confirmar] [➕ Agregar más] [❌ Cancelar]. El importe es el único número del
///   texto; si no hay uno claro, pregunta "¿Cuánto?". Lo que se agregue (texto o fotos) se suma y
///   vuelve el resumen. Confirmar → PENDIENTE en la bolsita; el cliente y quién la recibe se eligen
///   en la PC al volcar. NO toca plata.
///
/// #lista (2026-09-28): botones con las listas de precios generales activas (las mismas del
///   "Adjuntar del servidor"; si se agrega una, aparece sola) → le llega el PDF a ese WhatsApp.
///
/// Sin distinguir mayúsculas. Solo la línea FRIKAF.
/// </summary>
public class WhatsAppRedirigidaBotService
{
    /// <summary>La línea FRIKAF (11 2252-5458). Sin línea (null) = la default, que también es FRIKAF.</summary>
    public const string LineaFrikaf = "1242473442281483";
    private const int MinutosParaContestar = 30;
    private const string UploadsDir = "/data/whatsapp-uploads";

    private static readonly Regex RxRedi = new(@"#\s*redi\w*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RxLista = new(@"#\s*lista\w*", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // Números del texto: "50000", "50.000", "$ 50.000,50", "12500,5"
    private static readonly Regex RxNumero = new(@"\d[\d.,]*\d|\d", RegexOptions.Compiled);

    private readonly AppDbContext _db;
    private readonly MetaWhatsAppService _meta;
    private readonly Api.Controllers.CafeListasCustomController _listas;
    private readonly ILogger<WhatsAppRedirigidaBotService> _log;

    public WhatsAppRedirigidaBotService(AppDbContext db, MetaWhatsAppService meta,
        Api.Controllers.CafeListasCustomController listas, ILogger<WhatsAppRedirigidaBotService> log)
    {
        _db = db; _meta = meta; _listas = listas; _log = log;
    }

    /// <summary>true = lo atendió este asistente (el webhook no sigue). false = no era para acá.</summary>
    public async Task<bool> TryHandleAsync(string fromWaId, string numero, string? tipo,
        string? idInteractivo, string? cuerpo, string? lineaId, string? mediaUrl, string? mediaNombre, string baseUrl)
    {
        if (lineaId is not null && lineaId != LineaFrikaf) return false;
        try
        {
            var texto = (cuerpo ?? "").Trim();
            var esMedia = tipo is "image" or "document" or "video" or "audio" or "sticker";

            // ── Botones ──
            if (!string.IsNullOrEmpty(idInteractivo))
            {
                if (idInteractivo.StartsWith("lista:", StringComparison.Ordinal))
                    return await MandarListaAsync(fromWaId, numero, idInteractivo[6..], lineaId, baseUrl);
                if (idInteractivo.StartsWith("redi:", StringComparison.Ordinal))
                    return await BotonRediAsync(fromWaId, numero, idInteractivo, lineaId);
                return false;
            }

            // ── #lista ──
            if (tipo == "text" && RxLista.IsMatch(texto))
            {
                var aut = await BuscarAutorizadoAsync(numero);
                if (aut is null) return false;
                await OfrecerListasAsync(fromWaId, numero, lineaId);
                return true;
            }

            // ── #redi (texto, o foto/PDF con #redi al pie) ──
            if (RxRedi.IsMatch(texto) && (tipo == "text" || esMedia))
            {
                var aut = await BuscarAutorizadoAsync(numero);
                if (aut is null)
                {
                    _log.LogInformation("[BotRedi] '{Num}' escribió #redi pero no está autorizado", numero);
                    return false;
                }
                await IniciarAsync(fromWaId, numero, aut.Nombre, Limpio(texto),
                    esMedia ? mediaUrl : null, mediaNombre, lineaId);
                return true;
            }

            // ── Una redirigida a medio cargar ──
            var p = await BorradorAsync(numero);
            if (p is null) return false;
            if (tipo == "text" && EsCancelar(texto)) return await CancelarAsync(fromWaId, numero, p, lineaId);

            if (esMedia)
            {
                await AgregarAdjuntoAsync(fromWaId, numero, p, mediaUrl, mediaNombre, lineaId);
                if (texto.Length > 0) Sumar(p, texto);
            }
            else if (tipo == "text" && texto.Length > 0)
            {
                if (p.Paso == "importe")
                {
                    var monto = MontoParser.Parse(texto);
                    if (monto is null || monto <= 0)
                    {
                        await ResponderAsync(fromWaId, numero, "No entendí el importe. Escribí solo el número, por ejemplo *45000*.", lineaId);
                        return true;
                    }
                    p.Importe = monto.Value;
                }
                else Sumar(p, texto);   // "Agregar más" (o escribió algo más con el resumen a la vista)
            }
            else return true;

            await SeguirAsync(fromWaId, numero, p, lineaId);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[BotRedi] error atendiendo a {Num}", numero);
            try { await ResponderAsync(fromWaId, numero, "Uf, algo falló. Probá de nuevo en un rato.", lineaId); } catch { }
            return true;
        }
    }

    // ═══════════════ #redi ═══════════════

    private async Task IniciarAsync(string fromWaId, string numero, string nombre, string mensaje,
        string? mediaUrl, string? mediaNombre, string? lineaId)
    {
        // Una sola carga por número: si había una a medias, se descarta.
        var viejos = await _db.CafeRedirigidasPendientes.Include(x => x.Adjuntos)
            .Where(x => x.EnviadoNumero == numero && x.Estado == "BORRADOR").ToListAsync();
        foreach (var v in viejos) { _db.CafeRedirigidasPendientesAdjuntos.RemoveRange(v.Adjuntos); _db.CafeRedirigidasPendientes.Remove(v); }

        var p = new CafeRedirigidaPendiente
        {
            Estado = "BORRADOR", Paso = "resumen", ExpiraAt = DateTime.UtcNow.AddMinutes(MinutosParaContestar),
            EnviadoPor = nombre, EnviadoNumero = numero,
            Mensaje = string.IsNullOrWhiteSpace(mensaje) ? null : Recortar(mensaje, 1000),
            Importe = ImporteDelTexto(mensaje) ?? 0m
        };
        _db.CafeRedirigidasPendientes.Add(p);
        await _db.SaveChangesAsync();
        if (mediaUrl is not null) await AgregarAdjuntoAsync(fromWaId, numero, p, mediaUrl, mediaNombre, lineaId);
        await SeguirAsync(fromWaId, numero, p, lineaId);
    }

    /// <summary>Lo que falte (mensaje / importe) o el resumen con los 3 botones.</summary>
    private async Task SeguirAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string? lineaId)
    {
        if (string.IsNullOrEmpty(p.Mensaje))
        {
            await Paso(p, "agregar");
            await ResponderAsync(fromWaId, numero, "🔁 Escribí *quién la mandó y a quién le llegó* (y el importe).", lineaId);
            return;
        }
        if (p.Importe <= 0)
        {
            await Paso(p, "importe");
            await ResponderAsync(fromWaId, numero, "🔁 *¿Cuánto?* Escribí el importe (ej: 45000).", lineaId);
            return;
        }
        await Paso(p, "resumen");
        var cant = await _db.CafeRedirigidasPendientesAdjuntos.CountAsync(a => a.PendienteId == p.Id);
        var cuerpo = $"🔁 *Redirigida*\n«{p.Mensaje}»\n💲 {Money(p.Importe)}{(cant > 0 ? $" · 📎 {cant} {(cant == 1 ? "foto" : "fotos")}" : " · sin foto")}";
        var sid = await _meta.SendButtonsAsync(fromWaId, cuerpo,
            new List<(string, string)> { ("redi:ok", "✅ Confirmar"), ("redi:mas", "➕ Agregar más"), ("redi:salir", "❌ Cancelar") },
            lineaPhoneId: lineaId);
        await RegistrarAsync(numero, cuerpo, sid, lineaId);
    }

    private async Task<bool> BotonRediAsync(string fromWaId, string numero, string id, string? lineaId)
    {
        var p = await BorradorAsync(numero);
        if (p is null)
        {
            await ResponderAsync(fromWaId, numero, "Se venció la carga anterior. Mandá de nuevo el mensaje con *#redi*.", lineaId);
            return true;
        }
        switch (id)
        {
            case "redi:salir":
                return await CancelarAsync(fromWaId, numero, p, lineaId);
            case "redi:mas":
                await Paso(p, "agregar");
                await ResponderAsync(fromWaId, numero, "➕ Escribí lo que quieras agregar o mandá otra foto.", lineaId);
                return true;
            case "redi:ok":
                if (string.IsNullOrEmpty(p.Mensaje) || p.Importe <= 0) { await SeguirAsync(fromWaId, numero, p, lineaId); return true; }
                p.Estado = "PENDIENTE"; p.Paso = null; p.ExpiraAt = null;
                p.CreatedAt = DateTime.UtcNow; p.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                await ResponderAsync(fromWaId, numero, "✅ *Listo, quedó en la bolsita 💰*\nEn la oficina eligen el cliente y a quién le llegó.", lineaId);
                return true;
        }
        return true;
    }

    /// <summary>Suma texto al mensaje. Si todavía no había importe y el texto trae uno claro, lo toma.</summary>
    private static void Sumar(CafeRedirigidaPendiente p, string texto)
    {
        texto = Limpio(texto);
        if (texto.Length == 0) return;
        p.Mensaje = Recortar(string.IsNullOrEmpty(p.Mensaje) ? texto : $"{p.Mensaje} · {texto}", 1000);
        if (p.Importe <= 0 && ImporteDelTexto(texto) is decimal m) p.Importe = m;
    }

    /// <summary>El texto sin el "#redi" y sin espacios de más.</summary>
    private static string Limpio(string texto)
        => Regex.Replace(RxRedi.Replace(texto, " "), @"\s+", " ").Trim(' ', ',', '.', '-', ':');

    /// <summary>El importe del texto: el ÚNICO número de $100 para arriba. Si hay ninguno o varios
    /// ("2 cajas, 50000" da uno; "50000 y 30000" da dos), null → se pregunta "¿Cuánto?".</summary>
    private static decimal? ImporteDelTexto(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;
        var montos = RxNumero.Matches(texto)
            .Select(m => MontoParser.Parse(m.Value))
            .Where(v => v is >= 100m)
            .Select(v => v!.Value).Distinct().ToList();
        return montos.Count == 1 ? montos[0] : null;
    }

    private async Task<bool> CancelarAsync(string fromWaId, string numero, CafeRedirigidaPendiente p, string? lineaId)
    {
        var adj = await _db.CafeRedirigidasPendientesAdjuntos.Where(a => a.PendienteId == p.Id).ToListAsync();
        _db.CafeRedirigidasPendientesAdjuntos.RemoveRange(adj);
        _db.CafeRedirigidasPendientes.Remove(p);
        await _db.SaveChangesAsync();
        await ResponderAsync(fromWaId, numero, "👍 Cancelada. Cuando quieras, mandá el mensaje con *#redi*.", lineaId);
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
        await _db.SaveChangesAsync();
    }

    // ═══════════════ #lista ═══════════════

    private async Task OfrecerListasAsync(string fromWaId, string numero, string? lineaId)
    {
        // Las generales activas (las mismas del "Adjuntar del servidor"): si se agrega una, aparece sola.
        var listas = await _db.CafeListasPreciosCustom.AsNoTracking()
            .Where(l => l.IsActive && l.ClienteId == null).OrderBy(l => l.Nombre)
            .Select(l => new { l.Id, l.Nombre }).ToListAsync();
        if (listas.Count == 0) { await ResponderAsync(fromWaId, numero, "No hay listas de precios activas.", lineaId); return; }
        const string cuerpo = "💲 *¿Qué lista de precios te mando?*";
        string? sid;
        if (listas.Count <= 3)
            sid = await _meta.SendButtonsAsync(fromWaId, cuerpo,
                listas.Select(l => ($"lista:{l.Id}", Recortar(l.Nombre, 20))).ToList(), lineaPhoneId: lineaId);
        else
            sid = await _meta.SendListAsync(fromWaId, cuerpo, "Ver listas",
                listas.Take(10).Select(l => ($"lista:{l.Id}", Recortar(l.Nombre, 24), (string?)null)).ToList(), lineaPhoneId: lineaId);
        await RegistrarAsync(numero, cuerpo + " [" + string.Join(" / ", listas.Select(l => l.Nombre)) + "]", sid, lineaId);
    }

    private async Task<bool> MandarListaAsync(string fromWaId, string numero, string idTxt, string? lineaId, string baseUrl)
    {
        if (!int.TryParse(idTxt, out var listaId)) return true;
        if (await BuscarAutorizadoAsync(numero) is null) return true;
        var lista = await _db.CafeListasPreciosCustom.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listaId && l.IsActive);
        if (lista is null) { await ResponderAsync(fromWaId, numero, "Esa lista ya no está activa. Escribí *#lista* de nuevo.", lineaId); return true; }

        var (bytes, filename) = await _listas.GenerarPdfBytesAsync(listaId);
        if (bytes is null) { await ResponderAsync(fromWaId, numero, "⚠️ No pude armar el PDF de esa lista.", lineaId); return true; }

        Directory.CreateDirectory(UploadsDir);
        var token = Guid.NewGuid().ToString("N");
        await File.WriteAllBytesAsync(Path.Combine(UploadsDir, token + ".pdf"), bytes);
        _db.WhatsAppTwilioUploads.Add(new WhatsAppTwilioUpload
        {
            Token = token, OriginalFilename = filename, StoredFilename = token + ".pdf",
            ContentType = "application/pdf", SizeBytes = bytes.Length, NumeroDestino = numero,
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddHours(24)
        });
        await _db.SaveChangesAsync();

        var mediaUrl = $"{baseUrl}/api/whatsapp/twilio/files/{token}.pdf";
        var caption = $"💲 Lista de precios {lista.Nombre}";
        var sid = await _meta.SendMediaAsync(fromWaId, mediaUrl, caption, isDocument: true, filename: filename, lineaPhoneId: lineaId);
        _db.WhatsAppTwilioMensajes.Add(new WhatsAppTwilioMensaje
        {
            Direccion = "OUTGOING", Numero = numero, Cuerpo = caption, MediaUrl = mediaUrl, MediaFilename = filename,
            LineaPhoneId = lineaId, TwilioMessageSid = sid, Canal = "CLOUD", Procesado = true, CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return true;
    }

    // ═══════════════ Estado / utilidades ═══════════════

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
    { "cancelar", "cancela", "salir" };
    private static bool EsCancelar(string t) => Cancelar.Contains((t ?? "").Trim());

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
