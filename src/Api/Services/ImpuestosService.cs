using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Data;
using Api.Models;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using MimeKit;

namespace Api.Services;

/// <summary>
/// 17/11/2026 — Impuestos / VEP (ver <see cref="ImpuestoVep"/>).
/// 1) Lee los mails de la contadora (IMAP, SOLO LECTURA) y saca los VEP: del PDF adjunto
///    (número, impuesto, período, vencimiento, importe) o de los montos escritos en el texto.
/// 2) Cruza con el extracto del Galicia: débito "IMP.AFIP" por el mismo importe = PAGADO.
///    Los débitos IMP.AFIP sin VEP conocido quedan anotados como "Pago AFIP" (Origen BANCO).
/// </summary>
public class ImpuestosService
{
    private readonly AppDbContext _db;
    private readonly ILogger<ImpuestosService> _log;

    /// <summary>Desde cuándo se arma la historia (primera corrida).</summary>
    public static readonly DateTime Inicio = new(2026, 5, 1);
    public const string RemitenteDefault = "cuentacontadora@gmail.com";
    public const string KeyUltima = "impuestos.ultima", KeyResultado = "impuestos.resultado";
    private static readonly CultureInfo Ar = CultureInfo.GetCultureInfo("es-AR");

    public ImpuestosService(AppDbContext db, ILogger<ImpuestosService> log) { _db = db; _log = log; }

    public static DateTime HoyAr => DateTime.UtcNow.AddHours(-3).Date;

    public record VepLeido(string Concepto, string Entidad, decimal Importe, string? Detalle = null,
        string? NroVep = null, string? Cuit = null, string? Periodo = null, DateTime? Vence = null,
        byte[]? Pdf = null, string? PdfNombre = null);

    public record ResultadoRevision(int MailsLeidos, int Nuevos, int Pagados, int DelBanco, string? Error);

    // ─────────────────────────── Revisar todo ───────────────────────────

    public async Task<ResultadoRevision> RevisarAsync(CancellationToken ct = default)
    {
        int mails = 0, nuevos = 0; string? error = null;
        try { (mails, nuevos) = await LeerMailsAsync(ct); }
        catch (Exception ex) { error = "No se pudo leer el mail: " + ex.Message; _log.LogWarning(ex, "[Impuestos] mail"); }
        var (pagados, delBanco) = await CruzarConBancoAsync(ct);
        // 18/11/2026: la tarjeta Impuestos muestra "revisado hh:mm" y el resultado.
        try
        {
            await ComprasAfipRobot.GuardarAsync(_db, KeyUltima, DateTime.UtcNow.ToString("o"));
            await ComprasAfipRobot.GuardarAsync(_db, KeyResultado, error is not null ? "⚠ " + error
                : $"{mails} mail{(mails == 1 ? "" : "s")} leído{(mails == 1 ? "" : "s")} · {nuevos} VEP nuevo{(nuevos == 1 ? "" : "s")} · {pagados} pagado{(pagados == 1 ? "" : "s")} con el banco");
        }
        catch { }
        return new ResultadoRevision(mails, nuevos, pagados, delBanco, error);
    }

    // ─────────────────────────── Guardar ───────────────────────────

    /// <summary>Guarda los VEP leídos sin duplicar. Si ya había un "Pago AFIP" del banco por el
    /// mismo importe (se pagó antes de cargar el VEP), lo completa en vez de crear otro.</summary>
    public async Task<int> GuardarAsync(IEnumerable<VepLeido> veps, string origen, string? messageId, string? asunto, DateTime fechaUtc, CancellationToken ct = default)
    {
        int nuevos = 0;
        foreach (var v in veps)
        {
            if (v.Importe <= 0) continue;
            if (!string.IsNullOrEmpty(v.NroVep) && await _db.ImpuestosVeps.AnyAsync(x => x.NroVep == v.NroVep, ct)) continue;
            if (!string.IsNullOrEmpty(messageId) && await _db.ImpuestosVeps.AnyAsync(x => x.MailMessageId == messageId && x.Importe == v.Importe, ct)) continue;

            var desde = fechaUtc.AddDays(-45); var hasta = fechaUtc.AddDays(45);
            var delBanco = await _db.ImpuestosVeps.FirstOrDefaultAsync(x => x.Origen == "BANCO" && x.Importe == v.Importe
                && x.PagadoFecha >= desde && x.PagadoFecha <= hasta, ct);
            var e = delBanco ?? new ImpuestoVep { Origen = origen, CreatedAt = DateTime.UtcNow };
            e.Concepto = v.Concepto; e.Entidad = v.Entidad; e.Importe = v.Importe;
            e.Detalle = Corta(v.Detalle, 300); e.NroVep = v.NroVep; e.Cuit = v.Cuit; e.Periodo = v.Periodo; e.Vence = v.Vence;
            e.Pdf = v.Pdf ?? e.Pdf; e.PdfNombre = Corta(v.PdfNombre, 200) ?? e.PdfNombre;
            e.MailMessageId = Corta(messageId, 300); e.MailAsunto = Corta(asunto, 200); e.MailFecha = fechaUtc;
            if (delBanco is not null) { e.Origen = origen; e.UpdatedAt = DateTime.UtcNow; }
            else { _db.ImpuestosVeps.Add(e); nuevos++; }
            await _db.SaveChangesAsync(ct);
        }
        return nuevos;
    }

    // ─────────────────────────── Cruce con el banco ───────────────────────────

    private static readonly Regex RxCuitBanco = new(@"IMP\.AFIP\s*(\d{11})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<(int pagados, int delBanco)> CruzarConBancoAsync(CancellationToken ct = default)
    {
        var usados = (await _db.ImpuestosVeps.Where(x => x.ExtractoMovimientoId != null)
            .Select(x => x.ExtractoMovimientoId!.Value).ToListAsync(ct)).ToHashSet();
        var movs = await _db.CafeExtractoMovimientos.AsNoTracking()
            .Where(m => m.Fecha >= Inicio && m.Debitos > 0 && m.LeyendaAdicional1 != null && m.LeyendaAdicional1.StartsWith("IMP.AFIP"))
            .OrderBy(m => m.Fecha).ThenBy(m => m.Id).ToListAsync(ct);
        movs = movs.Where(m => !usados.Contains(m.Id)).ToList();
        if (movs.Count == 0) return (0, 0);

        var pendientes = await _db.ImpuestosVeps.Where(x => x.Estado == "PENDIENTE").OrderBy(x => x.MailFecha).ToListAsync(ct);
        var alias = await AliasPorCuitAsync(ct);
        int pagados = 0, delBanco = 0;
        foreach (var m in movs)
        {
            var mFecha = m.Fecha.Date;
            var vep = pendientes.FirstOrDefault(p => p.Importe == m.Debitos
                && (p.MailFecha is null || (mFecha >= p.MailFecha.Value.AddHours(-3).Date.AddDays(-3) && mFecha <= p.MailFecha.Value.AddHours(-3).Date.AddDays(60))));
            if (vep is not null)
            {
                vep.Estado = "PAGADO"; vep.PagadoFecha = mFecha; vep.PagadoComo = "Galicia";
                vep.ExtractoMovimientoId = m.Id; vep.UpdatedAt = DateTime.UtcNow;
                pendientes.Remove(vep); pagados++;
            }
            else
            {
                var cuit = RxCuitBanco.Match(m.LeyendaAdicional1 ?? "") is { Success: true } mc ? mc.Groups[1].Value : null;
                _db.ImpuestosVeps.Add(new ImpuestoVep
                {
                    Origen = "BANCO", Concepto = "Pago AFIP", Entidad = NombreEntidad(cuit, alias), Cuit = cuit,
                    Detalle = "El banco solo dice \"IMP.AFIP\": tocá ✏ para ponerle qué impuesto fue",
                    Importe = m.Debitos, Estado = "PAGADO", PagadoFecha = mFecha, PagadoComo = "Galicia",
                    ExtractoMovimientoId = m.Id, CreatedAt = DateTime.UtcNow,
                });
                delBanco++;
            }
        }
        await _db.SaveChangesAsync(ct);
        return (pagados, delBanco);
    }

    // ─────────────────────────── Mail ───────────────────────────

    private static readonly Regex RxMailInteresa = new(
        @"\bveps?\b|pagos?\s+(del|de\s+este|de\s+la)\s+(mes|semana)|pagos?\s+afip|volante\s+electr",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public async Task<(int mails, int nuevos)> LeerMailsAsync(CancellationToken ct = default)
    {
        var (host, port, user, pass) = await ResolverCredsCorreoAsync(ct);
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(pass)) return (0, 0);
        var remitentes = await RemitentesAsync(ct);

        var hayMails = await _db.ImpuestosVeps.AnyAsync(x => x.Origen == "MAIL", ct);
        var desde = hayMails ? DateTime.UtcNow.AddDays(-60) : Inicio;
        if (desde < Inicio) desde = Inicio;

        using var client = new ImapClient();
        await client.ConnectAsync(string.IsNullOrWhiteSpace(host) ? "imap.gmail.com" : host, port <= 0 ? 993 : port, SecureSocketOptions.SslOnConnect, ct);
        await client.AuthenticateAsync(user, pass, ct);
        // "Todos" (All Mail) si existe: así se leen también los mails archivados. Si no, la bandeja.
        IMailFolder carpeta = client.Inbox;
        try { var all = client.GetFolder(SpecialFolder.All); if (all is not null) carpeta = all; } catch { }
        await carpeta.OpenAsync(FolderAccess.ReadOnly, ct);

        int leidos = 0, nuevos = 0;
        foreach (var rem in remitentes)
        {
            var uids = await carpeta.SearchAsync(SearchQuery.FromContains(rem).And(SearchQuery.DeliveredAfter(desde)), ct);
            foreach (var uid in uids)
            {
                if (ct.IsCancellationRequested) break;
                MimeMessage msg;
                try { msg = await carpeta.GetMessageAsync(uid, ct); }
                catch (Exception ex) { _log.LogWarning(ex, "[Impuestos] no pude bajar un mail"); continue; }
                var mid = (msg.MessageId ?? $"uid-{uid.Id}").Trim().Trim('<', '>');
                if (await _db.ImpuestosVeps.AnyAsync(x => x.MailMessageId == mid, ct)) continue; // ya procesado

                var veps = await LeerMensajeAsync(msg, ct);
                leidos++;
                if (veps.Count == 0) continue;
                nuevos += await GuardarAsync(veps, "MAIL", mid, msg.Subject, msg.Date.UtcDateTime, ct);
            }
        }
        await client.DisconnectAsync(true, ct);
        if (nuevos > 0) _log.LogInformation("[Impuestos] {Nuevos} VEP nuevos de {Mails} mails", nuevos, leidos);
        return (leidos, nuevos);
    }

    /// <summary>Saca los VEP de un mail: PDFs adjuntos primero; después los montos del texto que no
    /// estén ya en un PDF. Solo si el mail habla de VEP / pagos del mes (no recibos de sueldo).</summary>
    public async Task<List<VepLeido>> LeerMensajeAsync(MimeMessage msg, CancellationToken ct)
    {
        var res = new List<VepLeido>();
        var alias = await AliasPorCuitAsync(ct);
        foreach (var att in msg.Attachments.OfType<MimePart>())
        {
            var nombre = att.FileName ?? "";
            if (!nombre.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && att.ContentType.MimeType != "application/pdf") continue;
            using var ms = new MemoryStream();
            await att.Content.DecodeToAsync(ms, ct);
            var bytes = ms.ToArray();
            var v = await LeerPdfAsync(bytes, nombre, alias);
            if (v is not null) res.Add(v);
        }
        var texto = msg.TextBody ?? HtmlATexto(msg.HtmlBody);
        var asunto = msg.Subject ?? "";
        if (res.Count > 0 || RxMailInteresa.IsMatch(asunto) || RxMailInteresa.IsMatch(texto ?? ""))
        {
            foreach (var t in LeerTexto(texto, asunto))
                if (!res.Any(r => r.Importe == t.Importe)) res.Add(t);
        }
        return res;
    }

    // ─────────────────────────── PDF del VEP ───────────────────────────

    public async Task<VepLeido?> LeerPdfAsync(byte[] pdf, string? nombre, Dictionary<string, string>? alias = null)
    {
        var txt = await PdfATextoAsync(pdf);
        if (string.IsNullOrWhiteSpace(txt) || !Regex.IsMatch(txt, @"Nro\.?\s*VEP|Volante\s+Electr", RegexOptions.IgnoreCase)) return null;
        alias ??= await AliasPorCuitAsync(default);
        string? Campo(string etiqueta)
        {
            var m = Regex.Match(txt, etiqueta + @"\s*:\s*(.+)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : null;
        }
        var nro = Regex.Match(txt, @"Nro\.?\s*VEP\s*:\s*(\d+)", RegexOptions.IgnoreCase) is { Success: true } mn ? mn.Groups[1].Value : null;
        var cuit = Campo("CUIT")?.Replace("-", "").Trim();
        if (cuit is not null) cuit = new string(cuit.TakeWhile(char.IsDigit).ToArray());
        var tipo = Campo("Tipo de Pago");
        var desc = Campo("Descripci[oó]n Reducida");
        var periodo = Campo("Per[ií]odo");
        DateTime? vence = null;
        if (Regex.Match(txt, @"D[ií]a de Expiraci[oó]n\s*:\s*(\d{4}-\d{2}-\d{2})", RegexOptions.IgnoreCase) is { Success: true } mv
            && DateTime.TryParseExact(mv.Groups[1].Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dv))
            vence = dv;
        decimal importe = 0;
        if (Regex.Match(txt, @"Importe total a pagar\s*\$?\s*([\d\.]+,\d{2})", RegexOptions.IgnoreCase) is { Success: true } mi)
            importe = ParseMonto(mi.Groups[1].Value);
        if (importe <= 0) return null;
        var concepto = Normalizar(tipo) ?? Normalizar(desc) ?? Normalizar(nombre) ?? (tipo ?? "Otro");
        var detalle = string.Join(" · ", new[] { tipo, desc }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return new VepLeido(Corta(concepto, 80)!, NombreEntidad(cuit, alias), importe, detalle, nro, cuit, periodo, vence, pdf, nombre);
    }

    private static async Task<string?> PdfATextoAsync(byte[] pdf)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"vep-{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllBytesAsync(tmp, pdf);
            var psi = new ProcessStartInfo("pdftotext", $"-layout \"{tmp}\" -")
            { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(20000)) { try { p.Kill(); } catch { } return null; }
            return await outTask;
        }
        catch { return null; }
        finally { try { File.Delete(tmp); } catch { } }
    }

    // ─────────────────────────── Texto escrito ───────────────────────────

    // "$ 63.987,11" · "$1.819.934,23" · "$ 330.000" · "$ 20 mil"
    private static readonly Regex RxMonto = new(@"\$\s*(\d{1,3}(?:\.\d{3})+(?:,\d{1,2})?|\d+(?:,\d{1,2})?)(\s*mil\b)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Montos escritos en el mail o en un WhatsApp pegado: "IVA $ 2.351.384,04".
    /// El impuesto sale de lo escrito antes del monto, o del renglón, o de los renglones de arriba.</summary>
    public static List<VepLeido> LeerTexto(string? texto, string? asunto)
    {
        var res = new List<VepLeido>();
        if (string.IsNullOrWhiteSpace(texto)) return res;
        // Gmail corta los renglones largos: "otro por $\n327.778,66" → juntar el monto con su "$".
        texto = Regex.Replace(texto.Replace("\r", ""), @"\$[ \t]*\n[ \t>]*(?=\d)", "$ ");
        var renglones = texto.Split('\n')
            .Select(l => l.Trim())
            .TakeWhile(l => !Regex.IsMatch(l, @"^(El\s.+escribi[oó]:|-{3,}\s*Forwarded|De:\s)", RegexOptions.IgnoreCase))
            .Where(l => !l.StartsWith(">"))
            .ToList();
        // De quién es: lo dice el renglón ("vep gaby ..."), o el asunto, o el mail si habla de la SH.
        // Los nombres de personas NO se toman del mail entero (un renglón de Gabriel no hace todo de Gabriel).
        var entidadMail = EntidadDeTexto(asunto)
            ?? (Regex.IsMatch(texto, @"\bsh\b|sociedad de hecho", RegexOptions.IgnoreCase) ? "SH" : null) ?? "SRL";
        for (int i = 0; i < renglones.Count; i++)
        {
            var l = renglones[i];
            int desdeCol = 0;
            foreach (Match m in RxMonto.Matches(l))
            {
                var monto = ParseMonto(m.Groups[1].Value);
                if (m.Groups[2].Success) monto *= 1000;
                var antes = l.Substring(desdeCol, m.Index - desdeCol);
                desdeCol = m.Index + m.Length;
                if (monto < 1000) continue; // números sueltos, no son pagos
                var arriba = string.Join(" ", renglones.Skip(Math.Max(0, i - 2)).Take(Math.Min(i, 2)));
                var concepto = Normalizar(antes) ?? Normalizar(l) ?? Normalizar(arriba) ?? Normalizar(asunto) ?? "Otro";
                var entidad = EntidadDeTexto(antes) ?? EntidadDeTexto(l) ?? entidadMail;
                res.Add(new VepLeido(concepto, entidad, monto, Corta(l, 300)));
            }
        }
        return res;
    }

    public static string? Normalizar(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = s.ToLowerInvariant();
        if (Regex.IsMatch(t, @"ingresos\s+brutos|\biibb\b|\bi\.?i\.?b\.?b\b|convenio multilateral")) return "Ingresos brutos";
        if (Regex.IsMatch(t, @"\biva\b")) return "IVA";
        if (Regex.IsMatch(t, @"aporte|seguridad social|\b931\b|cargas sociales|empleador|obra social|sindicato|\bart\b")) return "Aportes empleados";
        if (Regex.IsMatch(t, @"ganancia|gcias|anticipo")) return "Ganancias";
        if (Regex.IsMatch(t, @"bienes personales")) return "Bienes personales";
        if (Regex.IsMatch(t, @"monotributo|\bmono\b")) return "Monotributo";
        if (Regex.IsMatch(t, @"honorario")) return "Honorarios contadora";
        if (Regex.IsMatch(t, @"autonomo|autónomo")) return "Autónomos";
        return null;
    }

    private static string? EntidadDeTexto(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var t = " " + s.ToLowerInvariant() + " ";
        if (Regex.IsMatch(t, @"\bgaby\b|\bgabi\b|\bgabriel\b")) return "Gabriel";
        if (Regex.IsMatch(t, @"\bgerman\b|\bgermán\b")) return "German";
        if (Regex.IsMatch(t, @"\bchicho\b")) return "Chicho";
        if (Regex.IsMatch(t, @"\bnacho\b")) return "Nacho";
        if (Regex.IsMatch(t, @"\bsh\b|sociedad de hecho")) return "SH";
        if (Regex.IsMatch(t, @"\bsrl\b")) return "SRL";
        return null;
    }

    // ─────────────────────────── Ayudas ───────────────────────────

    public async Task<Dictionary<string, string>> AliasPorCuitAsync(CancellationToken ct)
    {
        try { return await _db.ArcaAccounts.AsNoTracking().Where(a => a.Alias != null).ToDictionaryAsync(a => a.Cuit, a => a.Alias!, ct); }
        catch { return new(); }
    }

    public static string NombreEntidad(string? cuit, Dictionary<string, string> alias)
    {
        if (string.IsNullOrWhiteSpace(cuit)) return "SRL";
        if (cuit == "30717212149") return "SRL";
        if (cuit == "30715919253") return "SH";
        if (alias.TryGetValue(cuit, out var a) && !string.IsNullOrWhiteSpace(a))
        {
            var ti = CultureInfo.GetCultureInfo("es-AR").TextInfo;
            return ti.ToTitleCase(a.ToLowerInvariant());
        }
        return cuit;
    }

    private static decimal ParseMonto(string s)
    {
        var limpio = s.Replace(".", "").Replace(",", ".");
        return decimal.TryParse(limpio, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? Math.Round(v, 2) : 0;
    }

    private static string HtmlATexto(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return "";
        var t = Regex.Replace(html, @"<(br|/div|/p|/li)\b[^>]*>", "\n", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "<[^>]+>", "");
        return System.Net.WebUtility.HtmlDecode(t);
    }

    private static string? Corta(string? s, int max) => s is null ? null : (s.Length <= max ? s : s[..max]);

    private async Task<List<string>> RemitentesAsync(CancellationToken ct)
    {
        var v = await _db.AppSettings.Where(s => s.Key == "impuestos.mail.remitentes").Select(s => s.Value).FirstOrDefaultAsync(ct);
        var l = (v ?? RemitenteDefault).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
        return l.Count > 0 ? l : new() { RemitenteDefault };
    }

    /// <summary>Misma casilla que usan los otros robots de mail (alertas.imap.* o integración email-smtp).</summary>
    private async Task<(string? host, int port, string? user, string? pass)> ResolverCredsCorreoAsync(CancellationToken ct)
    {
        var cfg = await _db.AppSettings.Where(s => s.Key.StartsWith("alertas.imap.")).ToDictionaryAsync(s => s.Key, s => s.Value, ct);
        cfg.TryGetValue("alertas.imap.user", out var user);
        cfg.TryGetValue("alertas.imap.pass", out var pass);
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
        {
            cfg.TryGetValue("alertas.imap.host", out var h);
            cfg.TryGetValue("alertas.imap.port", out var pStr);
            int.TryParse(pStr, out var pr);
            return (h, pr, user, pass);
        }
        var integ = await _db.Set<Integration>().FirstOrDefaultAsync(x => x.Provider == "email-smtp", ct);
        if (integ is null || string.IsNullOrWhiteSpace(integ.AppSecret)) return (null, 0, null, null);
        string? iUser = null, iHost = null; int iPort = 993;
        if (!string.IsNullOrWhiteSpace(integ.Settings))
        {
            try
            {
                using var doc = JsonDocument.Parse(integ.Settings);
                var root = doc.RootElement;
                if (root.TryGetProperty("username", out var u) && !string.IsNullOrWhiteSpace(u.GetString())) iUser = u.GetString();
                else if (root.TryGetProperty("fromAddress", out var f)) iUser = f.GetString();
                if (root.TryGetProperty("imapHost", out var ih)) iHost = ih.GetString();
                if (root.TryGetProperty("imapPort", out var ip) && ip.TryGetInt32(out var ipv)) iPort = ipv;
            }
            catch { }
        }
        return (iHost, iPort, iUser, integ.AppSecret);
    }
}

/// <summary>17/11/2026 — Cada 30 minutos lee los mails de la contadora y cruza con el banco.</summary>
public class ImpuestosBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ImpuestosBackgroundService> _log;
    public ImpuestosBackgroundService(IServiceScopeFactory f, ILogger<ImpuestosBackgroundService> log) { _scopeFactory = f; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(3), stoppingToken); } catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var svc = scope.ServiceProvider.GetRequiredService<ImpuestosService>();
                await svc.RevisarAsync(stoppingToken);
            }
            catch (Exception ex) { _log.LogWarning(ex, "[Impuestos] error en el ciclo (no critico)"); }
            try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
}
