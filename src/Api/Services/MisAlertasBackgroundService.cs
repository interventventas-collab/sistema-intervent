using System.Globalization;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Api.Services;

/// <summary>
/// Robot del motor de alertas configurables. Cada 5 minutos recorre las reglas activas de
/// "Mis Alertas" y evalua si se cumple la condicion de cada una. Cuando una se cumple por
/// primera vez, la marca como "disparada" (aparece en la campanita). Cuando la condicion deja
/// de cumplirse, la resetea, asi la proxima vez que se cumpla vuelve a avisar.
///
/// Paso 1: solo el canal campanita esta activo. WhatsApp/Correo quedan para el Paso 2.
///
/// Mismo andamiaje que ShellAutoSyncBackgroundService (BackgroundService + while + Task.Delay
/// + scope por tick). Hora Argentina = UTC-3.
/// </summary>
public class MisAlertasBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<MisAlertasBackgroundService> _logger;
    private static readonly TimeSpan Period = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(1);
    private const int ARG_OFFSET_HOURS = -3;

    public MisAlertasBackgroundService(IServiceScopeFactory scopeFactory, ILogger<MisAlertasBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(FirstDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await TickAsync(); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Alertas] error en el ciclo (no critico)"); }
            try { await Task.Delay(Period, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task TickAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var reglas = await db.MisAlertas.Where(a => a.Activa).ToListAsync();
        if (reglas.Count == 0) return;

        var argNow = DateTime.UtcNow.AddHours(ARG_OFFSET_HOURS);

        // Datos compartidos que se leen una sola vez por tick.
        decimal? shellSaldo = ParseMonto(
            (await db.Set<ShellAccount>().OrderByDescending(s => s.Id).FirstOrDefaultAsync())?.LastSaldo);

        decimal? bancoSaldo = (await db.CafeExtractoMovimientos
            .OrderByDescending(m => m.Fecha).ThenByDescending(m => m.Id)
            .FirstOrDefaultAsync())?.Saldo;

        var changed = false;

        foreach (var a in reglas)
        {
            var (met, detalle) = await EvaluarAsync(db, a, argNow, shellSaldo, bancoSaldo);

            if (met && !a.EstaDisparada)
            {
                a.EstaDisparada = true;
                a.Vista = false;
                a.DisparadaAt = DateTime.UtcNow;
                a.UltimoDetalle = detalle;
                a.UpdatedAt = DateTime.UtcNow;
                changed = true;
            }
            else if (met && a.EstaDisparada)
            {
                // Sigue disparada: solo refrescamos el detalle sin resetear "Vista".
                if (a.UltimoDetalle != detalle) { a.UltimoDetalle = detalle; a.UpdatedAt = DateTime.UtcNow; changed = true; }
            }
            else if (!met && a.EstaDisparada)
            {
                a.EstaDisparada = false;
                a.Vista = false;
                a.UltimoDetalle = null;
                a.UpdatedAt = DateTime.UtcNow;
                changed = true;
            }
        }

        if (changed) await db.SaveChangesAsync();
    }

    private async Task<(bool met, string? detalle)> EvaluarAsync(
        AppDbContext db, MisAlerta a, DateTime argNow, decimal? shellSaldo, decimal? bancoSaldo)
    {
        switch (a.Tipo)
        {
            case "SHELL_BAJO":
            {
                if (shellSaldo is null || a.Umbral is null) return (false, null);
                if (shellSaldo.Value < a.Umbral.Value)
                    return (true, $"Shell Flota: {Money(shellSaldo.Value)}");
                return (false, null);
            }
            case "BANCO_BAJO":
            {
                if (bancoSaldo is null || a.Umbral is null) return (false, null);
                if (bancoSaldo.Value < a.Umbral.Value)
                    return (true, $"Banco Galicia: {Money(bancoSaldo.Value)}");
                return (false, null);
            }
            case "CHEQUE_VENCE":
            {
                var dias = (int)(a.Umbral ?? 0);
                if (dias < 0) dias = 0;
                var hoy = argNow.Date;
                var limite = hoy.AddDays(dias);
                var q = db.Set<CafeChequeBanco>().Where(c =>
                    c.Tipo == "EMITIDO" && c.Estado != "Pagado" &&
                    c.FechaPago != null && c.FechaPago >= hoy && c.FechaPago <= limite);
                var cant = await q.CountAsync();
                if (cant == 0) return (false, null);
                var total = await q.SumAsync(c => c.Importe);
                var prox = await q.MinAsync(c => c.FechaPago);
                var plural = cant == 1 ? "cheque" : "cheques";
                return (true, $"{cant} {plural} por {Money(total)} (el más próximo {prox:dd/MM})");
            }
            case "FECHA_MES":
            {
                var dia = (int)(a.Umbral ?? 0);
                if (dia < 1) return (false, null);
                var diasEnMes = DateTime.DaysInMonth(argNow.Year, argNow.Month);
                var diaEfectivo = Math.Min(dia, diasEnMes); // si pusiste 31 y el mes tiene 30, dispara el 30.
                if (argNow.Day == diaEfectivo)
                    return (true, $"Hoy {argNow:dd/MM}");
                return (false, null);
            }
            default:
                return (false, null);
        }
    }

    /// <summary>Convierte el saldo de Shell (texto scrapeado, formato argentino "$ 1.234,56")
    /// a decimal. Devuelve null si no se puede interpretar.</summary>
    private static decimal? ParseMonto(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Dejar solo digitos, coma, punto y signo menos.
        var s = new string(raw.Where(c => char.IsDigit(c) || c == ',' || c == '.' || c == '-').ToArray());
        if (string.IsNullOrEmpty(s)) return null;

        // Robusto ante formato inglés ("196,988.75") y argentino ("196.988,75"):
        // el separador que aparece MÁS a la derecha es el decimal; el otro son miles.
        int lastDot = s.LastIndexOf('.');
        int lastComma = s.LastIndexOf(',');
        if (lastDot >= 0 && lastComma >= 0)
        {
            if (lastDot > lastComma) s = s.Replace(",", "");          // inglés: 196,988.75 -> 196988.75
            else s = s.Replace(".", "").Replace(",", ".");            // argentino: 196.988,75 -> 196988.75
        }
        else if (lastComma >= 0)
        {
            // Solo coma: decimal si tiene 1-2 dígitos detrás; si no, son miles.
            var dec = s.Length - lastComma - 1;
            s = (dec >= 1 && dec <= 2) ? s.Replace(",", ".") : s.Replace(",", "");
        }
        else if (lastDot >= 0)
        {
            // Solo punto: decimal si tiene 1-2 dígitos detrás; si no, son miles.
            var dec = s.Length - lastDot - 1;
            if (!(dec >= 1 && dec <= 2)) s = s.Replace(".", "");
        }

        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string Money(decimal v)
        => "$" + v.ToString("N0", CultureInfo.GetCultureInfo("es-AR"));
}
