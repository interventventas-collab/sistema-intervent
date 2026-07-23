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
    private readonly AutoAvisoSender _sender;

    private static readonly NumberFormatInfo MilesNfi = new NumberFormatInfo
    { NumberGroupSeparator = ".", NumberDecimalSeparator = ",", NumberGroupSizes = new[] { 3 } };

    public ResumenFinancieroNotifier(AppDbContext db, AutoAvisoSender sender)
    {
        _db = db;
        _sender = sender;
    }

    /// <summary>Arma el contenido y lo despacha por los canales/personas configurados en el
    /// Centro de Automatizaciones (clave 'resumen-financiero').</summary>
    public async Task<(bool Ok, string Detalle)> EnviarResumenAsync(CancellationToken ct = default)
    {
        var argNow = DateTime.UtcNow.AddHours(-3);
        var (msgTg, msgWa) = await ConstruirMensajesAsync(argNow, ct);
        var plano = msgWa.Replace("*", "");   // versión sin formato para campanita/correo
        return await _sender.EnviarAsync("resumen-financiero",
            new AutoAvisoSender.Contenido($"🌅 Resumen financiero {argNow:dd/MM/yyyy}", msgTg, msgWa, plano), ct);
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
