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

    private static readonly NumberFormatInfo MilesNfi = new NumberFormatInfo
    { NumberGroupSeparator = ".", NumberDecimalSeparator = ",", NumberGroupSizes = new[] { 3 } };

    public ResumenFinancieroNotifier(AppDbContext db, TelegramService tg)
    {
        _db = db;
        _tg = tg;
    }

    public async Task<(bool Ok, string Detalle)> EnviarResumenAsync(CancellationToken ct = default)
    {
        var cuenta = await _db.TelegramAccounts.Where(x => x.Proposito == "AVISOS").OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (cuenta is null || !cuenta.IsActive || string.IsNullOrEmpty(cuenta.BotToken))
            return (false, "No hay un bot de Telegram (AVISOS) activo. Vinculalo en Integraciones → Telegram.");
        var hayDestino = await _db.TelegramChats.AnyAsync(c => c.TelegramAccountId == cuenta.Id && c.NotifAlertas, ct);
        if (!hayDestino)
            return (false, "Nadie tiene activado el tilde 'Alertas' de Telegram.");

        var argNow = DateTime.UtcNow.AddHours(-3);
        var msg = await ConstruirMensajeAsync(argNow, ct);
        var (enviado, err) = await _tg.SendMessageAsync(msg, categoria: "ALERTAS", ct: ct, parseMode: "HTML");
        return enviado ? (true, "Resumen financiero enviado por Telegram.")
                       : (false, $"No se pudo enviar: {err}");
    }

    private async Task<string> ConstruirMensajeAsync(DateTime argNow, CancellationToken ct)
    {
        // 🏦 Galicia: el saldo del último movimiento del extracto importado (igual que el dashboard)
        var ultMov = await _db.CafeExtractoMovimientos.AsNoTracking()
            .OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .Select(m => new { m.Saldo, m.Fecha })
            .FirstOrDefaultAsync(ct);
        var galicia = ultMov is null
            ? "sin datos del extracto"
            : $"<b>{Money(ultMov.Saldo)}</b> (extracto al {ultMov.Fecha:dd/MM})";

        // ⛽ Shell Flota: último saldo que dejó el robot (es texto tal cual lo muestra Shell)
        var shell = await _db.ShellAccounts.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Id)
            .Select(s => new { s.Alias, s.LastSaldo, s.LastSaldoAt })
            .FirstOrDefaultAsync(ct);
        var shellTxt = shell is null || string.IsNullOrWhiteSpace(shell.LastSaldo)
            ? "sin datos todavía"
            : $"<b>{Esc(shell.LastSaldo)}</b>{(shell.LastSaldoAt.HasValue ? $" (al {shell.LastSaldoAt.Value.AddHours(-3):dd/MM HH:mm})" : "")}";

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

        string chequesTxt;
        if (cheques.Count == 0)
        {
            chequesTxt = "🧾 Cheques por cubrir: <b>ninguno</b> 🎉";
        }
        else
        {
            var total = cheques.Sum(c => c.Importe);
            var lineas = cheques.Select(c =>
            {
                var marca = c.FechaPago!.Value.Date < hoy ? " ⚠️ VENCIDO"
                          : c.FechaPago!.Value.Date == hoy ? " 🔴 HOY" : "";
                return $"• {c.FechaPago:dd/MM} — {Money(c.Importe)} — {Esc(c.ContraparteNombre ?? "—")} (Nº {Esc(c.Numero)}){marca}";
            });
            chequesTxt = $"🧾 Cheques por cubrir: <b>{cheques.Count}</b> — total <b>{Money(total)}</b>\n"
                       + "<blockquote expandable>" + string.Join("\n", lineas) + "</blockquote>";
        }

        return $"🌅 <b>Buen día — Resumen financiero {argNow:dd/MM/yyyy}</b>\n\n"
             + $"🏦 Galicia: {galicia}\n"
             + $"⛽ Shell Flota: {shellTxt}\n"
             + chequesTxt;
    }

    private static string Money(decimal v) => "$" + v.ToString("#,##0", MilesNfi);
    private static string Esc(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
