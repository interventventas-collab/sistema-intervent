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
    private readonly AutoAvisoSender _sender;
    private const int MAX_CHARS = 3500; // límite prudente por mensaje de Telegram (el tope real es ~4096)

    private static readonly NumberFormatInfo MilesNfi = new NumberFormatInfo
    { NumberGroupSeparator = ".", NumberDecimalSeparator = ",", NumberGroupSizes = new[] { 3 } };

    public DeudoresDiarioNotifier(AppDbContext db, CafeSaldosService saldos, AutoAvisoSender sender)
    {
        _db = db;
        _saldos = saldos;
        _sender = sender;
    }

    /// <summary>Calcula la deuda por cliente y la manda por Telegram (categoría ALERTAS).
    /// Devuelve si se pudo enviar y un detalle para mostrarle al usuario.</summary>
    public async Task<ResultadoEnvioDeudores> EnviarResumenAsync(CancellationToken ct = default)
    {
        var lista = OrdenarPorCuit((await _saldos.GetSaldosPendientesAsync()).Where(x => x.SaldoPendiente > 0));

        var argNow = DateTime.UtcNow.AddHours(-3);
        var mensajes = ConstruirMensajes(lista, argNow);

        // 2026-07-23 (Centro de Automatizaciones): ya no manda solo — arma el contenido y el
        // despachador lo reparte por los canales/personas configurados (clave 'deudas-diario').
        var fecha = argNow.ToString("dd/MM/yyyy");
        var total = lista.Sum(x => x.SaldoPendiente);
        string waTexto;
        if (lista.Count == 0)
        {
            waTexto = $"📋 *Deudas al {fecha}*\n\n🎉 Hoy no te debe ningún cliente.";
        }
        else
        {
            var lineasWa = lista.Select(c => $"• {c.Nombre}: {Money(c.SaldoPendiente)}").ToList();
            var listaWa = lineasWa.Count <= 40 ? string.Join("\n", lineasWa)
                        : string.Join("\n", lineasWa.Take(40)) + $"\n… y {lineasWa.Count - 40} más (verlos en el sistema)";
            waTexto = $"📋 *Deudas al {fecha}* — {lista.Count} cliente(s)\n💰 TOTAL: *{Money(total)}*\n{listaWa}";
        }
        var (ok, detalle) = await _sender.EnviarAsync("deudas-diario",
            new AutoAvisoSender.Contenido($"📋 Deudas al {fecha} — TOTAL {Money(total)}",
                mensajes[0], waTexto, waTexto.Replace("*", ""),
                mensajes.Count > 1 ? mensajes.Skip(1).ToList() : null), ct);
        return new ResultadoEnvioDeudores(ok, lista.Count, mensajes.Count, detalle);
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
