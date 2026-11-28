using System.Globalization;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 18/11/2026: "#gasto" por WhatsApp. Cuando un número autorizado escribe "#gasto lo que sea" (con o sin
/// foto del ticket) se guarda en Gastos_WhatsApp como PENDIENTE. NO se contesta nada por WhatsApp
/// (desde oct/2026 Meta cobra las respuestas). Si manda la foto en un mensaje aparte enseguida después,
/// se pega al último gasto pendiente de ese número (hasta 10 minutos).
///
/// Quién puede: la lista "gasto.numeros" de AppSettings (se edita en Compras → Gastos por WhatsApp).
/// Si está vacía, valen los números de las personas activas de Alertas (Auto_Personas).
/// </summary>
public static class GastoWhatsAppService
{
    public const string KeyNumeros = "gasto.numeros";
    // 28/11/2026: "gas", "gasto", "gastos", con o sin "#" adelante. Al principio y como palabra sola.
    private static readonly Regex Disparador = new(@"^\s*(?:#\s*)?(?:gastos?|gas)(?![\p{L}\p{N}])[\s:,.-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // 28/11/2026: "red", "redi", "redirigida(s)", con o sin "#". Va a la bolsita de redirigidas.
    private static readonly Regex DisparadorRedi = new(@"^\s*(?:#\s*)?(?:redirigidas?|redi|red)(?![\p{L}\p{N}])[\s:,.-]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Últimos 10 dígitos: así "+54 9 11 2081-2933", "541120812933" y "1120812933" son el mismo.</summary>
    public static string Clave(string? numero)
    {
        var d = new string((numero ?? "").Where(char.IsDigit).ToArray());
        return d.Length > 10 ? d[^10..] : d;
    }

    public static async Task<List<string>> NumerosConfiguradosAsync(AppDbContext db)
    {
        var raw = (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == KeyNumeros))?.Value;
        return (raw ?? "").Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => Clave(x).Length >= 8).ToList();
    }

    /// <summary>Números habilitados (claves de 10 dígitos) + de dónde salen.</summary>
    public static async Task<(HashSet<string> Claves, bool DeAlertas)> HabilitadosAsync(AppDbContext db)
    {
        var conf = await NumerosConfiguradosAsync(db);
        if (conf.Count > 0) return (conf.Select(Clave).ToHashSet(), false);
        var personas = await db.AutoPersonas.AsNoTracking()
            .Where(p => p.Activo && p.WhatsAppNumero != null && p.WhatsAppNumero != "")
            .Select(p => p.WhatsAppNumero!).ToListAsync();
        return (personas.Select(Clave).Where(c => c.Length >= 8).ToHashSet(), true);
    }

    public static bool EsGasto(string? cuerpo) => Disparo(cuerpo, null) == "GASTO";

    /// <summary>28/11/2026: qué palabra arranca el mensaje. "GASTO" (gas / gasto / gastos, con o sin #),
    /// "REDI" (red / redi / redirigida, con o sin #) o null. Tiene que ir AL PRINCIPIO y como palabra
    /// sola: "gaseosas" o "redondeá" no cuentan. En la línea FRIKAF "redi" lo sigue atendiendo el
    /// asistente de siempre (WhatsAppRedirigidaBotService, con botones), así que acá no se toma.</summary>
    public static string? Disparo(string? cuerpo, string? lineaId)
    {
        if (string.IsNullOrWhiteSpace(cuerpo)) return null;
        if (Disparador.IsMatch(cuerpo)) return "GASTO";
        if (lineaId != WhatsAppRedirigidaBotService.LineaFrikaf && DisparadorRedi.IsMatch(cuerpo)) return "REDI";
        return null;
    }

    private static string SinPalabra(string cuerpo, string tipo)
        => (tipo == "REDI" ? DisparadorRedi : Disparador).Replace(cuerpo, "", 1).Trim();

    private const int Minutos = 10;

    /// <summary>Se llama con CADA mensaje entrante (ya guardado). Devuelve true si lo tomó como gasto /
    /// redirigida (o como foto de uno); en ese caso no hay que correr ningún otro robot.
    ///
    /// 28/11/2026: el comprobante se pega de tres maneras (al reenviar en WhatsApp no se puede escribir
    /// en el mismo mensaje):
    ///   1. "Responder" al comprobante con "gas"/"red" → se pega ESE mensaje (aunque sea viejo).
    ///   2. "gas"/"red" primero → lo que mande en los 10 minutos siguientes se pega.
    ///   3. "gas"/"red" después → lo que mandó en los 10 minutos anteriores se pega.
    /// "red" no va a Compras: queda en la bolsita 💰 de redirigidas para cargar (Cafe_RedirigidasPendientes).</summary>
    public static async Task<bool> TryRegistrarAsync(AppDbContext db, ILogger log, WhatsAppTwilioMensaje msg)
    {
        var tipo = Disparo(msg.Cuerpo, msg.LineaPhoneId);
        var tieneMedia = !string.IsNullOrEmpty(msg.MediaUrl);
        // Texto suelto sin palabra: solo se pega fuera de la línea FRIKAF (ahí lo usa el asistente de #redi).
        var tieneTexto = !string.IsNullOrWhiteSpace(msg.Cuerpo) && msg.LineaPhoneId != WhatsAppRedirigidaBotService.LineaFrikaf;
        if (tipo is null && !tieneMedia && !tieneTexto) return false;

        var (habilitados, _) = await HabilitadosAsync(db);
        var clave = Clave(msg.Numero);
        if (!habilitados.Contains(clave))
        {
            if (tipo is not null) log.LogInformation("[#gasto] {Numero} no está habilitado: se ignora ({Tipo})", msg.Numero, tipo);
            return false;
        }

        var ahora = DateTime.UtcNow;
        // Lo último que mandó este número por esta línea (para "antes" y "después").
        var previos = await db.WhatsAppTwilioMensajes.AsNoTracking()
            .Where(m => m.Numero == msg.Numero && m.LineaPhoneId == msg.LineaPhoneId && m.Direccion == "INCOMING"
                        && m.Id < msg.Id && m.CreatedAt >= ahora.AddMinutes(-2 * Minutos) && m.AnuladoAt == null)
            .OrderBy(m => m.Id).ToListAsync();
        var ultimoDisparo = previos.LastOrDefault(m => Disparo(m.Cuerpo, m.LineaPhoneId) is not null);

        if (tipo is null)
        {
            // Mensaje suelto: se pega al gasto/redirigida que pidió en los 10 minutos anteriores.
            if (ultimoDisparo is null || ultimoDisparo.CreatedAt < ahora.AddMinutes(-Minutos)) return false;
            var tipoPrev = Disparo(ultimoDisparo.Cuerpo, ultimoDisparo.LineaPhoneId)!;
            bool pegado;
            if (tipoPrev == "GASTO")
            {
                var g = await db.GastosWhatsApp.FirstOrDefaultAsync(x => x.MensajeId == ultimoDisparo.Id && x.Estado == "PENDIENTE");
                pegado = g is not null && await PegarAGastoAsync(db, g, msg);
                if (pegado) log.LogInformation("[#gasto] mensaje {Msg} pegado al gasto {Id}", msg.Id, g!.Id);
            }
            else
            {
                var r = await RediDelDisparoAsync(db, ultimoDisparo);
                pegado = r is not null && await PegarARediAsync(db, r, msg);
                if (pegado) log.LogInformation("[red] mensaje {Msg} pegado a la redirigida {Id}", msg.Id, r!.Id);
            }
            if (pegado) await db.SaveChangesAsync();
            // Si era una foto/PDF no corre ningún otro robot (como antes); un texto sigue su camino.
            return pegado && tieneMedia;
        }

        // ── Llegó la palabra ──
        var fuentes = new List<WhatsAppTwilioMensaje>();
        if (!string.IsNullOrEmpty(msg.ReplyToSid))
        {
            // 1. Respondió a un mensaje puntual: ese es el comprobante.
            var citado = await db.WhatsAppTwilioMensajes.AsNoTracking()
                .FirstOrDefaultAsync(m => m.TwilioMessageSid == msg.ReplyToSid);
            if (citado is not null) fuentes.Add(citado);
        }
        else if (!tieneMedia)
        {
            // 3. La palabra vino sola, después de reenviar: lo que mandó en los 10 minutos anteriores.
            // Lo que llegó dentro de los 10 minutos de una palabra anterior ya se pegó a esa: no se repite.
            var desde = ahora.AddMinutes(-Minutos);
            if (ultimoDisparo is not null && ultimoDisparo.CreatedAt.AddMinutes(Minutos) > desde)
                desde = ultimoDisparo.CreatedAt.AddMinutes(Minutos);
            fuentes.AddRange(previos.Where(m => m.CreatedAt >= desde
                && (ultimoDisparo is null || m.Id > ultimoDisparo.Id)
                && Disparo(m.Cuerpo, m.LineaPhoneId) is null
                && (!string.IsNullOrEmpty(m.MediaUrl) || !string.IsNullOrWhiteSpace(m.Cuerpo))));
        }

        var texto = SinPalabra(msg.Cuerpo!, tipo);
        if (texto.Length > 1000) texto = texto[..1000];

        if (tipo == "GASTO")
        {
            var gasto = new GastoWhatsApp
            {
                MensajeId = msg.Id,
                Numero = msg.Numero,
                NombrePerfil = msg.NombrePerfil,
                Texto = texto,
                MediaUrl = msg.MediaUrl,
                MediaFilename = msg.MediaFilename,
                Estado = "PENDIENTE",
                CreatedAt = ahora,
            };
            db.GastosWhatsApp.Add(gasto);
            await db.SaveChangesAsync();
            foreach (var f in fuentes) await PegarAGastoAsync(db, gasto, f);
            gasto.Importe = LeerImporte(gasto.Texto);
            gasto.Medio = LeerMedio(gasto.Texto);
            await db.SaveChangesAsync();
            log.LogInformation("[#gasto] nuevo gasto {Id} de {Numero} ({N} pegados): {Texto}", gasto.Id, msg.Numero, fuentes.Count, texto);
        }
        else
        {
            var red = new CafeRedirigidaPendiente
            {
                Estado = "PENDIENTE",
                EnviadoPor = Recortar(await NombreAsync(db, clave) ?? msg.NombrePerfil ?? msg.Numero, 80),
                EnviadoNumero = msg.Numero,
                Mensaje = string.IsNullOrWhiteSpace(texto) ? null : texto,
                CreatedAt = ahora, UpdatedAt = ahora,
            };
            db.CafeRedirigidasPendientes.Add(red);
            await db.SaveChangesAsync();
            if (tieneMedia) await PegarARediAsync(db, red, new WhatsAppTwilioMensaje { MediaUrl = msg.MediaUrl, MediaFilename = msg.MediaFilename });
            foreach (var f in fuentes) await PegarARediAsync(db, red, f);
            red.Importe = LeerImporte(red.Mensaje ?? "") ?? 0m;
            await db.SaveChangesAsync();
            log.LogInformation("[red] nueva redirigida {Id} de {Numero} ({N} pegados): {Texto}", red.Id, msg.Numero, fuentes.Count, texto);
        }
        return true;
    }

    /// <summary>Foto/PDF → adjunto (el primero va a MediaUrl); texto → se suma al detalle.</summary>
    private static async Task<bool> PegarAGastoAsync(AppDbContext db, GastoWhatsApp g, WhatsAppTwilioMensaje m)
    {
        var algo = false;
        if (!string.IsNullOrEmpty(m.MediaUrl))
        {
            if (g.MediaUrl is null) { g.MediaUrl = m.MediaUrl; g.MediaFilename = m.MediaFilename; }
            else if (g.MediaUrl != m.MediaUrl
                     && !await db.GastosWhatsAppAdjuntos.AnyAsync(a => a.GastoId == g.Id && a.MediaUrl == m.MediaUrl))
                db.GastosWhatsAppAdjuntos.Add(new GastoWhatsAppAdjunto
                {
                    GastoId = g.Id, MensajeId = m.Id == 0 ? null : m.Id, MediaUrl = m.MediaUrl!, MediaFilename = m.MediaFilename,
                });
            algo = true;
        }
        var t = (m.Cuerpo ?? "").Trim();
        if (t.Length > 0 && !t.StartsWith("[") && !g.Texto.Contains(t))
        {
            g.Texto = Recortar(string.IsNullOrEmpty(g.Texto) ? t : g.Texto + "\n" + t, 1000);
            if (g.Importe is null) g.Importe = LeerImporte(g.Texto);
            g.Medio ??= LeerMedio(g.Texto);
            algo = true;
        }
        return algo;
    }

    private static async Task<bool> PegarARediAsync(AppDbContext db, CafeRedirigidaPendiente r, WhatsAppTwilioMensaje m)
    {
        var algo = false;
        if (!string.IsNullOrEmpty(m.MediaUrl))
        {
            // El webhook ya guardó el archivo: la URL termina en /files/{token}{ext}.
            var token = Path.GetFileNameWithoutExtension(m.MediaUrl.Split('?')[0]);
            var up = await db.WhatsAppTwilioUploads.AsNoTracking().FirstOrDefaultAsync(u => u.Token == token);
            if (up is not null && !await db.CafeRedirigidasPendientesAdjuntos.AnyAsync(a => a.PendienteId == r.Id && a.StoredFilename == up.StoredFilename))
            {
                db.CafeRedirigidasPendientesAdjuntos.Add(new CafeRedirigidaPendienteAdjunto
                {
                    PendienteId = r.Id, StoredFilename = up.StoredFilename,
                    NombreOriginal = Recortar(m.MediaFilename ?? up.OriginalFilename, 260),
                    MimeType = up.ContentType, Tamano = up.SizeBytes,
                });
                algo = true;
            }
        }
        var t = (m.Cuerpo ?? "").Trim();
        if (t.Length > 0 && !t.StartsWith("[") && !(r.Mensaje ?? "").Contains(t))
        {
            r.Mensaje = Recortar(string.IsNullOrEmpty(r.Mensaje) ? t : r.Mensaje + "\n" + t, 1000);
            if (r.Importe == 0) r.Importe = LeerImporte(r.Mensaje) ?? 0m;
            algo = true;
        }
        if (algo) r.UpdatedAt = DateTime.UtcNow;
        return algo;
    }

    /// <summary>La redirigida que armó un "red": del mismo número, creada en ese momento y sin volcar.</summary>
    private static Task<CafeRedirigidaPendiente?> RediDelDisparoAsync(AppDbContext db, WhatsAppTwilioMensaje disparo)
    {
        var desde = disparo.CreatedAt.AddSeconds(-5);
        var hasta = disparo.CreatedAt.AddMinutes(2);
        return db.CafeRedirigidasPendientes
            .Where(r => r.EnviadoNumero == disparo.Numero && r.Estado == "PENDIENTE" && r.CreatedAt >= desde && r.CreatedAt <= hasta)
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
    }

    private static async Task<string?> NombreAsync(AppDbContext db, string clave)
    {
        var ps = await db.AutoPersonas.AsNoTracking().Where(p => p.WhatsAppNumero != null && p.WhatsAppNumero != "")
            .Select(p => new { p.Nombre, p.WhatsAppNumero }).ToListAsync();
        return ps.FirstOrDefault(p => Clave(p.WhatsAppNumero) == clave)?.Nombre;
    }

    private static string Recortar(string s, int max) => s.Length > max ? s[..max] : s;

    // "25000", "25.000", "$25.000,50", "25000.5", "25 mil", "25k"
    private static readonly Regex Numero = new(@"\$?\s*(\d{1,3}(?:\.\d{3})+(?:,\d{1,2})?|\d+(?:[.,]\d{1,2})?)\s*(mil\b|k\b|lucas?\b)?", RegexOptions.IgnoreCase);

    /// <summary>El número más grande del texto (los gastos suelen tener uno solo; si hay "2 cafés 3500",
    /// el importe es el grande). Menos de $10 no se toma (cantidades).</summary>
    public static decimal? LeerImporte(string texto)
    {
        decimal? mejor = null;
        foreach (Match m in Numero.Matches(texto))
        {
            var s = m.Groups[1].Value;
            decimal v;
            if (s.Contains('.') && Regex.IsMatch(s, @"^\d{1,3}(\.\d{3})+(,\d{1,2})?$"))
                v = decimal.Parse(s.Replace(".", "").Replace(",", "."), CultureInfo.InvariantCulture);
            else
                v = decimal.Parse(s.Replace(",", "."), CultureInfo.InvariantCulture);
            if (m.Groups[2].Success) v *= 1000;
            if (v < 10) continue;
            if (mejor is null || v > mejor) mejor = v;
        }
        return mejor;
    }

    public static string? LeerMedio(string texto)
    {
        var t = texto.ToLowerInvariant();
        if (Regex.IsMatch(t, @"\b(efectivo|efvo|efect|cash|billete)")) return "EFECTIVO";
        if (Regex.IsMatch(t, @"\b(mp|mercado\s*pago|mercadopago)\b")) return "MERCADOPAGO";
        if (Regex.IsMatch(t, @"\b(transf|transferencia|galicia|banco|cbu|alias)")) return "TRANSFERENCIA";
        return null;
    }
}
