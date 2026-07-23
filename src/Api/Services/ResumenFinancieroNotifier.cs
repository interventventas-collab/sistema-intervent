using System.Globalization;
using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>2026-07-23 (pedido Osmar): arma y manda por Telegram el "resumen financiero de la
/// mañana": saldo Galicia (último movimiento del extracto), saldo Shell Flota y los cheques
/// EMITIDOS por cubrir (con total y detalle desplegable). Lo usan el servicio de fondo diario
/// (ResumenFinancieroDiarioService) y el endpoint de prueba. Mismo molde que DeudoresDiarioNotifier.</summary>
public class ResumenFinancieroNotifier
{
    private readonly AppDbContext _db;
    private readonly TelegramService _tg;
    private readonly WhatsAppOutboundService _wa;
    private readonly ILogger<ResumenFinancieroNotifier> _log;

    /// <summary>Números de WhatsApp que reciben el resumen (separados por coma). Idea de Osmar:
    /// los hermanos escriben a la línea todos los días, así que la ventana de 24 hs está casi
    /// siempre abierta y el mensaje sale GRATIS sin plantillas. Si la ventana está cerrada
    /// (no escribió en 24 hs), ese día no le llega — queda en el log; Telegram es el respaldo.</summary>
    public const string WhatsAppNumerosKey = "resumen.financiero.whatsapp_numeros";

    private static readonly NumberFormatInfo MilesNfi = new NumberFormatInfo
    { NumberGroupSeparator = ".", NumberDecimalSeparator = ",", NumberGroupSizes = new[] { 3 } };

    public ResumenFinancieroNotifier(AppDbContext db, TelegramService tg, WhatsAppOutboundService wa,
        ILogger<ResumenFinancieroNotifier> log)
    {
        _db = db;
        _tg = tg;
        _wa = wa;
        _log = log;
    }

    public async Task<(bool Ok, string Detalle)> EnviarResumenAsync(CancellationToken ct = default)
    {
        var argNow = DateTime.UtcNow.AddHours(-3);
        var (msgTg, msgWa) = await ConstruirMensajesAsync(argNow, ct);
        var partes = new List<string>();
        var okAlguno = false;

        // ── Telegram (a los que tienen el tilde 'Alertas') ──
        var cuenta = await _db.TelegramAccounts.Where(x => x.Proposito == "AVISOS").OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        var hayTg = cuenta is not null && cuenta.IsActive && !string.IsNullOrEmpty(cuenta.BotToken)
            && await _db.TelegramChats.AnyAsync(c => c.TelegramAccountId == cuenta.Id && c.NotifAlertas, ct);
        if (hayTg)
        {
            var (enviado, err) = await _tg.SendMessageAsync(msgTg, categoria: "ALERTAS", ct: ct, parseMode: "HTML");
            okAlguno |= enviado;
            partes.Add(enviado ? "Telegram OK" : $"Telegram falló ({err})");
        }
        else partes.Add("Telegram: sin bot o sin destinatarios");

        // ── WhatsApp (a los números configurados, si su ventana de 24 hs está abierta) ──
        var numerosCfg = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Key == WhatsAppNumerosKey).Select(s => s.Value).FirstOrDefaultAsync(ct);
        var numeros = (numerosCfg ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int waOk = 0;
        foreach (var n in numeros)
        {
            var numero = n.StartsWith("whatsapp:") ? n : "whatsapp:" + n;
            try
            {
                var (sid, canal) = await _wa.SendTextAsync(numero, msgWa);
                if (sid != null)
                {
                    waOk++; okAlguno = true;
                    // Registrar en la bandeja del chat, como cualquier saliente
                    _db.WhatsAppTwilioMensajes.Add(new Models.WhatsAppTwilioMensaje
                    {
                        Direccion = "OUTGOING", Numero = numero, Cuerpo = msgWa,
                        TwilioMessageSid = sid, Canal = canal, Procesado = true, CreatedAt = DateTime.UtcNow
                    });
                    await _db.SaveChangesAsync(ct);
                }
                else _log.LogInformation("[ResumenFinanciero] WhatsApp a {Numero} no salió (¿ventana de 24hs cerrada?)", numero);
            }
            catch (Exception ex) { _log.LogWarning(ex, "[ResumenFinanciero] error mandando WhatsApp a {Numero}", numero); }
        }
        if (numeros.Length > 0) partes.Add($"WhatsApp: {waOk} de {numeros.Length}");

        return (okAlguno, string.Join(" · ", partes));
    }

    private async Task<(string Telegram, string WhatsApp)> ConstruirMensajesAsync(DateTime argNow, CancellationToken ct)
    {
        // 🏦 Galicia: el saldo del último movimiento del extracto importado (igual que el dashboard)
        var ultMov = await _db.CafeExtractoMovimientos.AsNoTracking()
            .OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .Select(m => new { m.Saldo, m.Fecha })
            .FirstOrDefaultAsync(ct);
        var galiciaTg = ultMov is null
            ? "sin datos del extracto"
            : $"<b>{Money(ultMov.Saldo)}</b> (extracto al {ultMov.Fecha:dd/MM})";
        var galiciaWa = ultMov is null
            ? "sin datos del extracto"
            : $"*{Money(ultMov.Saldo)}* (extracto al {ultMov.Fecha:dd/MM})";

        // ⛽ Shell Flota: último saldo que dejó el robot (es texto tal cual lo muestra Shell)
        var shell = await _db.ShellAccounts.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Id)
            .Select(s => new { s.Alias, s.LastSaldo, s.LastSaldoAt })
            .FirstOrDefaultAsync(ct);
        var shellFecha = shell?.LastSaldoAt is not null ? $" (al {shell.LastSaldoAt.Value.AddHours(-3):dd/MM HH:mm})" : "";
        var shellTg = shell is null || string.IsNullOrWhiteSpace(shell.LastSaldo)
            ? "sin datos todavía"
            : $"<b>{Esc(shell.LastSaldo)}</b>{shellFecha}";
        var shellWa = shell is null || string.IsNullOrWhiteSpace(shell.LastSaldo)
            ? "sin datos todavía"
            : $"*{shell.LastSaldo}*{shellFecha}";

        // 🧾 Cheques por cubrir: EMITIDOS Aceptado/Disponible con fecha de pago (misma regla que
        // la card del dashboard). Los vencidos/de hoy van marcados.
        var hoy = argNow.Date;
        var cheques = await _db.CafeChequesBanco.AsNoTracking()
            .Where(c => c.Tipo == "EMITIDO"
                && (c.Estado == "Aceptado" || c.Estado == "Disponible")
                && c.FechaPago.HasValue)
            .OrderBy(c => c.FechaPago).ThenBy(c => c.Id)
            .Select(c => new { c.FechaPago, c.Importe, c.ContraparteNombre, c.Numero })
            .ToListAsync(ct);

        string chequesTg, chequesWa;
        if (cheques.Count == 0)
        {
            chequesTg = "🧾 Cheques por cubrir: <b>ninguno</b> 🎉";
            chequesWa = "🧾 Cheques por cubrir: *ninguno* 🎉";
        }
        else
        {
            var total = cheques.Sum(c => c.Importe);
            string Marca(DateTime f) => f.Date < hoy ? " ⚠️ VENCIDO" : f.Date == hoy ? " 🔴 HOY" : "";
            var lineasTg = cheques.Select(c => $"• {c.FechaPago:dd/MM} — {Money(c.Importe)} — {Esc(c.ContraparteNombre ?? "—")} (Nº {Esc(c.Numero)}){Marca(c.FechaPago!.Value)}");
            var lineasWa = cheques.Select(c => $"• {c.FechaPago:dd/MM} — {Money(c.Importe)} — {c.ContraparteNombre ?? "—"} (Nº {c.Numero}){Marca(c.FechaPago!.Value)}").ToList();
            chequesTg = $"🧾 Cheques por cubrir: <b>{cheques.Count}</b> — total <b>{Money(total)}</b>\n"
                      + "<blockquote expandable>" + string.Join("\n", lineasTg) + "</blockquote>";
            // WhatsApp no tiene desplegable: lista directa, con tope prudente
            var listaWa = lineasWa.Count <= 15 ? string.Join("\n", lineasWa)
                        : string.Join("\n", lineasWa.Take(15)) + $"\n… y {lineasWa.Count - 15} más (verlos en el sistema)";
            chequesWa = $"🧾 Cheques por cubrir: *{cheques.Count}* — total *{Money(total)}*\n" + listaWa;
        }

        var tg = $"🌅 <b>Buen día — Resumen financiero {argNow:dd/MM/yyyy}</b>\n\n"
               + $"🏦 Galicia: {galiciaTg}\n"
               + $"⛽ Shell Flota: {shellTg}\n"
               + chequesTg;
        var wa = $"🌅 *Buen día — Resumen financiero {argNow:dd/MM/yyyy}*\n\n"
               + $"🏦 Galicia: {galiciaWa}\n"
               + $"⛽ Shell Flota: {shellWa}\n"
               + chequesWa;
        return (tg, wa);
    }

    private static string Money(decimal v) => "$" + v.ToString("#,##0", MilesNfi);
    private static string Esc(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
