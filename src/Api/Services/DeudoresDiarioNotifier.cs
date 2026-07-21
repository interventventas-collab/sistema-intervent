using System.Globalization;
using System.Text;
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

        var lista = (await _saldos.GetSaldosPendientesAsync())
            .Where(x => x.SaldoPendiente > 0)
            .OrderByDescending(x => x.SaldoPendiente)
            .ToList();

        var argNow = DateTime.UtcNow.AddHours(-3);
        var mensajes = ConstruirMensajes(lista, argNow);

        int ok = 0;
        foreach (var m in mensajes)
        {
            var (enviado, _) = await _tg.SendMessageAsync(m, categoria: "ALERTAS", ct: ct);
            if (enviado) ok++;
        }
        var detalle = ok > 0
            ? $"Resumen enviado por Telegram ({lista.Count} cliente(s), {ok} mensaje(s))."
            : "No se pudo enviar el mensaje de Telegram.";
        return new ResultadoEnvioDeudores(ok > 0, lista.Count, ok, detalle);
    }

    /// <summary>Arma el/los texto(s) del mensaje. Si hay muchos deudores, parte en varios
    /// mensajes para no pasarse del límite de Telegram.</summary>
    public static List<string> ConstruirMensajes(List<ClienteSaldoPendienteDto> lista, DateTime argNow)
    {
        var fecha = argNow.ToString("dd/MM/yyyy");
        if (lista.Count == 0)
            return new List<string> { $"📋 Deudas al {fecha}\n\n🎉 Hoy no te debe ningún cliente." };

        var total = lista.Sum(x => x.SaldoPendiente);
        var header = $"📋 Deudas al {fecha}  ({lista.Count} cliente(s))\n\n";
        var footer = $"\n─────────────\n💰 TOTAL: {Money(total)}";

        var mensajes = new List<string>();
        var sb = new StringBuilder(header);
        foreach (var c in lista)
        {
            var linea = $"• {c.Nombre}: {Money(c.SaldoPendiente)}\n";
            if (sb.Length + linea.Length > MAX_CHARS)
            {
                mensajes.Add(sb.ToString().TrimEnd());
                sb = new StringBuilder();
            }
            sb.Append(linea);
        }
        // El total va pegado al último bloque (o solo, si justo no entraba).
        if (sb.Length + footer.Length > MAX_CHARS) { mensajes.Add(sb.ToString().TrimEnd()); sb = new StringBuilder(); }
        sb.Append(footer);
        mensajes.Add(sb.ToString().Trim('\n'));

        if (mensajes.Count > 1)
            for (int i = 0; i < mensajes.Count; i++)
                mensajes[i] = $"({i + 1}/{mensajes.Count}) " + mensajes[i];
        return mensajes;
    }

    private static string Money(decimal v) => "$" + v.ToString("#,##0", MilesNfi);
}

/// <summary>Resultado del envío del resumen de deudores.</summary>
public record ResultadoEnvioDeudores(bool Ok, int Clientes, int Mensajes, string Detalle);
