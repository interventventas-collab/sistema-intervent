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

        // 🧾 Cheques por cubrir: EMITIDOS Aceptado/Disponible con fecha de pago de HOY en adelante.
        // Se separa lo que vence HOY (para que diga claramente "hoy ninguno por cubrir") de los
        // PRÓXIMOS (se muestran los 5 que siguen). Los vencidos ya no se listan.
        var hoy = argNow.Date;
        var cheques = await _db.CafeChequesBanco.AsNoTracking()
            .Where(c => c.Tipo == "EMITIDO"
                && (c.Estado == "Aceptado" || c.Estado == "Disponible")
                && c.FechaPago.HasValue
                && c.FechaPago.Value >= hoy)
            .OrderBy(c => c.FechaPago).ThenBy(c => c.Id)
            .Select(c => new { c.FechaPago, c.Importe, c.ContraparteNombre, c.Numero })
            .ToListAsync(ct);

        const int MaxProximos = 5;
        var deHoy = cheques.Where(c => c.FechaPago!.Value.Date == hoy).ToList();
        var proximosTodos = cheques.Where(c => c.FechaPago!.Value.Date > hoy).ToList();
        var proximos = proximosTodos.Take(MaxProximos).ToList();

        // --- Lo que vence HOY ---
        string hoyTg, hoyWa;
        if (deHoy.Count == 0)
        {
            hoyTg = "🧾 Cheques por cubrir hoy: <b>ninguno</b> 🎉";
            hoyWa = "🧾 Cheques por cubrir hoy: *ninguno* 🎉";
        }
        else
        {
            var totalHoy = deHoy.Sum(c => c.Importe);
            var lhTg = deHoy.Select(c => $"• {c.FechaPago:dd/MM} — {Money(c.Importe)} — {Esc(c.ContraparteNombre ?? "—")} (Nº {Esc(c.Numero)})");
            var lhWa = deHoy.Select(c => $"• {c.FechaPago:dd/MM} — {Money(c.Importe)} — {c.ContraparteNombre ?? "—"} (Nº {c.Numero})");
            hoyTg = $"🧾 Cheques por cubrir hoy: <b>{deHoy.Count}</b> — total <b>{Money(totalHoy)}</b> 🔴\n" + string.Join("\n", lhTg);
            hoyWa = $"🧾 Cheques por cubrir hoy: *{deHoy.Count}* — total *{Money(totalHoy)}* 🔴\n" + string.Join("\n", lhWa);
        }

        // --- Los próximos (hasta 5) ---
        string proxTg, proxWa;
        if (proximos.Count == 0)
        {
            proxTg = "📅 Próximos: <b>no hay cheques a la vista</b>";
            proxWa = "📅 Próximos: *no hay cheques a la vista*";
        }
        else
        {
            var lpTg = proximos.Select(c => $"• {c.FechaPago:dd/MM} — {Money(c.Importe)} — {Esc(c.ContraparteNombre ?? "—")} (Nº {Esc(c.Numero)})");
            var lpWa = proximos.Select(c => $"• {c.FechaPago:dd/MM} — {Money(c.Importe)} — {c.ContraparteNombre ?? "—"} (Nº {c.Numero})");
            var masTg = proximosTodos.Count > MaxProximos ? $"\n… y {proximosTodos.Count - MaxProximos} más (verlos en el sistema)" : "";
            proxTg = "📅 Próximos:\n" + string.Join("\n", lpTg) + masTg;
            proxWa = "📅 Próximos:\n" + string.Join("\n", lpWa) + masTg;
        }

        var chequesTg = hoyTg + "\n" + proxTg;
        var chequesWa = hoyWa + "\n" + proxWa;

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
