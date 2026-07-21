using System.Globalization;
using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>Arma y manda por Telegram el resumen de "lo que debe cada cliente" (cuenta corriente):
/// una línea por cliente con Cliente + Total, y al final el total general.
/// Lo usan dos lugares: el servicio de fondo diario (DeudoresDiarioService) y el botón
/// "probar aviso ahora" del panel de deudas. Reusa CafeSaldosService (misma fórmula que el panel).</summary>
public class DeudoresDiarioNotifier
{
    private readonly AppDbContext _db;
    private readonly CafeSaldosService _saldos;
    private readonly TelegramService _tg;
    private const int MAX_CHARS = 3500; // límite prudente por mensaje de Telegram (el tope real es ~4096)

    private static readonly NumberFormatInfo MilesNfi = new NumberFormatInfo
    { NumberGroupSeparator = ".", NumberDecimalSeparator = ",", NumberGroupSizes = new[] { 3 } };

    public DeudoresDiarioNotifier(AppDbContext db, CafeSaldosService saldos, TelegramService tg)
    {
        _db = db;
        _saldos = saldos;
        _tg = tg;
    }

    /// <summary>Calcula la deuda por cliente y la manda por Telegram (categoría ALERTAS).
    /// Devuelve si se pudo enviar y un detalle para mostrarle al usuario.</summary>
    public async Task<ResultadoEnvioDeudores> EnviarResumenAsync(CancellationToken ct = default)
    {
        var cuenta = await _db.TelegramAccounts.Where(x => x.Proposito == "AVISOS").OrderBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (cuenta is null || !cuenta.IsActive || string.IsNullOrEmpty(cuenta.BotToken))
            return new ResultadoEnvioDeudores(false, 0, 0, "No hay un bot de Telegram (AVISOS) activo. Vinculalo en Integraciones → Telegram.");
        var hayDestino = await _db.TelegramChats.AnyAsync(c => c.TelegramAccountId == cuenta.Id && c.NotifAlertas, ct);
        if (!hayDestino)
            return new ResultadoEnvioDeudores(false, 0, 0, "Nadie tiene activadas las alertas de Telegram (tilde 'Alertas' por persona).");

        var lista = OrdenarPorCuit((await _saldos.GetSaldosPendientesAsync()).Where(x => x.SaldoPendiente > 0));

        var argNow = DateTime.UtcNow.AddHours(-3);
        var mensajes = ConstruirMensajes(lista, argNow);

        int ok = 0;
        foreach (var m in mensajes)
        {
            // parseMode HTML: habilita el <blockquote expandable> (desplegable) del mensaje.
            var (enviado, _) = await _tg.SendMessageAsync(m, categoria: "ALERTAS", ct: ct, parseMode: "HTML");
            if (enviado) ok++;
        }
        var detalle = ok > 0
            ? $"Resumen enviado por Telegram ({lista.Count} cliente(s), {ok} mensaje(s))."
            : "No se pudo enviar el mensaje de Telegram.";
        return new ResultadoEnvioDeudores(ok > 0, lista.Count, ok, detalle);
    }

    /// <summary>Ordena los deudores de MAYOR a menor, pero manteniendo juntas las cuentas del mismo CUIT:
    /// agrupa por CUIT, ordena los grupos por lo que suman en total (desc) y dentro de cada grupo por saldo (desc).
    /// Los clientes sin CUIT se tratan como grupo propio.</summary>
    public static List<ClienteSaldoPendienteDto> OrdenarPorCuit(IEnumerable<ClienteSaldoPendienteDto> items)
        => items
            .GroupBy(x => string.IsNullOrWhiteSpace(x.Cuit) ? $"__id{x.ClienteId}" : x.Cuit!.Trim())
            .Select(g => new { Grupo = g.OrderByDescending(y => y.SaldoPendiente).ToList(), Total = g.Sum(y => y.SaldoPendiente) })
            .OrderByDescending(g => g.Total)
            .SelectMany(g => g.Grupo)
            .ToList();

    /// <summary>Arma el/los mensaje(s). El detalle cliente-por-cliente va dentro de un
    /// &lt;blockquote expandable&gt; (parse_mode HTML), así el mensaje llega CORTO (fecha + total)
    /// y el detalle se abre tocándolo. Si hay demasiados deudores, parte en varios mensajes.</summary>
    public static List<string> ConstruirMensajes(List<ClienteSaldoPendienteDto> lista, DateTime argNow)
    {
        var fecha = argNow.ToString("dd/MM/yyyy");
        if (lista.Count == 0)
            return new List<string> { $"📋 Deudas al {fecha}\n\n🎉 Hoy no te debe ningún cliente." };

        var total = lista.Sum(x => x.SaldoPendiente);
        var headerBase = $"📋 <b>Deudas al {fecha}</b> — {lista.Count} cliente(s)\n💰 TOTAL: <b>{Money(total)}</b>";
        var lineas = lista.Select(c => $"• {Esc(c.Nombre)}: {Money(c.SaldoPendiente)}").ToList();

        // Partir las líneas en bloques que no superen el límite (dejando margen para header + tags).
        var bloques = new List<List<string>>();
        var actual = new List<string>();
        var lenActual = 0;
        foreach (var l in lineas)
        {
            if (lenActual + l.Length + 1 > MAX_CHARS && actual.Count > 0)
            {
                bloques.Add(actual); actual = new List<string>(); lenActual = 0;
            }
            actual.Add(l); lenActual += l.Length + 1;
        }
        if (actual.Count > 0) bloques.Add(actual);

        var mensajes = new List<string>();
        for (int i = 0; i < bloques.Count; i++)
        {
            var head = headerBase + (bloques.Count > 1 ? $"  ({i + 1}/{bloques.Count})" : "");
            var detalle = "<blockquote expandable>" + string.Join("\n", bloques[i]) + "</blockquote>";
            mensajes.Add(head + "\n" + detalle);
        }
        return mensajes;
    }

    private static string Money(decimal v) => "$" + v.ToString("#,##0", MilesNfi);

    // Escapa lo mínimo para parse_mode HTML de Telegram (los nombres pueden tener &, <, >).
    private static string Esc(string? s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}

/// <summary>Resultado del envío del resumen de deudores.</summary>
public record ResultadoEnvioDeudores(bool Ok, int Clientes, int Mensajes, string Detalle);
