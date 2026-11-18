using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 18/11/2026 — Trae de ARCA ("Mis Comprobantes", con la clave fiscal de PALANICA) las facturas
/// recibidas y emitidas de los últimos 30 días y las importa al Libro IVA. Pedido del dueño:
/// "una vez por día a las 7 de la mañana, que siempre se vea la última vez que lo trajo y que me
/// permita traer ahora". ARCA no tiene web service para las recibidas y las muestra recién al día
/// siguiente, así que más seguido no suma. Antes lo hacía ContadoraAutoBackfillService cada ~20 h.
/// </summary>
public class ComprasAfipRobot
{
    public const string CuitPalanica = "30717212149";
    public const string KeyUltima = "afip.compras.ultima";       // UTC "o" de la última vez que trajo bien
    public const string KeyResultado = "afip.compras.resultado"; // texto del último intento (bien o mal)
    public const string KeyIntento = "afip.compras.intento";     // UTC del último intento
    public const string KeyVistas = "afip.compras.vistas_hasta"; // UTC: lo que entró después es "nuevo"
    public const int HoraAr = 7;

    private static readonly SemaphoreSlim _lock = new(1, 1);
    public static bool Corriendo { get; private set; }

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ComprasAfipRobot> _log;
    public ComprasAfipRobot(IServiceScopeFactory f, ILogger<ComprasAfipRobot> log) { _scopeFactory = f; _log = log; }

    public static async Task<string?> LeerAsync(AppDbContext db, string key)
        => (await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == key))?.Value;

    public static async Task GuardarAsync(AppDbContext db, string key, string valor)
    {
        var s = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key);
        if (s is null) db.AppSettings.Add(new AppSetting { Key = key, Value = valor, UpdatedAt = DateTime.UtcNow });
        else { s.Value = valor; s.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
    }

    public static DateTime? ParseUtc(string? s)
        => DateTime.TryParse(s, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d) ? d.ToUniversalTime() : null;

    /// <summary>"Traer ahora": arranca en segundo plano (tarda 1–3 minutos). false si ya está corriendo.</summary>
    public bool Iniciar(string quien)
    {
        if (Corriendo) return false;
        _ = Task.Run(() => TraerAsync(quien, CancellationToken.None));
        return true;
    }

    public async Task<(bool ok, string mensaje)> TraerAsync(string quien, CancellationToken ct)
    {
        if (!await _lock.WaitAsync(0, ct)) return (false, "Ya se están trayendo");
        Corriendo = true;
        string msg;
        var ok = false;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await GuardarAsync(db, KeyIntento, DateTime.UtcNow.ToString("o"));
            (ok, msg) = await BajarEImportarAsync(scope.ServiceProvider, ct);
            if (ok) await GuardarAsync(db, KeyUltima, DateTime.UtcNow.ToString("o"));
            await GuardarAsync(db, KeyResultado, (ok ? "" : "⚠ ") + msg + (string.IsNullOrWhiteSpace(quien) ? "" : $" · {quien}"));
            _log.LogInformation("[AFIP compras] ({Quien}) {Ok}: {Msg}", quien, ok, msg);
        }
        catch (Exception ex)
        {
            msg = "Error: " + ex.Message;
            _log.LogError(ex, "[AFIP compras] error");
            try
            {
                using var scope = _scopeFactory.CreateScope();
                await GuardarAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), KeyResultado, "⚠ " + msg);
            }
            catch { }
        }
        finally { Corriendo = false; _lock.Release(); }
        return (ok, msg);
    }

    private static async Task<(bool, string)> BajarEImportarAsync(IServiceProvider sp, CancellationToken ct)
    {
        var cuentas = sp.GetRequiredService<ArcaAccountService>();
        var scraper = sp.GetRequiredService<ArcaScrapingService>();
        var contadora = sp.GetRequiredService<ContadoraService>();

        var pal = (await cuentas.GetAllAsync()).FirstOrDefault(a => a.IsActive && a.HasPassword
            && new string(a.Cuit.Where(char.IsDigit).ToArray()) == CuitPalanica);
        if (pal is null) return (false, "No hay cuenta de ARCA de Palanica con clave cargada");
        var st = await scraper.GetStatusAsync();
        if (st.Running) return (false, "ARCA está ocupado con otra bajada, probá en un rato");
        var pass = await cuentas.GetPasswordAsync(pal.Id);
        if (string.IsNullOrEmpty(pass)) return (false, "Falta la clave de ARCA de Palanica");

        var (okIni, err) = await scraper.StartComprobantesAsync(pal.Cuit, pal.CuitLogin, pass, new RangoFechasRequest { Tipo = "30dias" });
        if (!okIni) return (false, "No se pudo entrar a ARCA: " + err);
        for (int i = 0; i < 80 && !ct.IsCancellationRequested; i++)   // ~4 min
        {
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { break; }
            if (!(await scraper.GetStatusAsync()).Running) break;
        }
        if ((await scraper.GetStatusAsync()).Running) return (false, "ARCA tardó demasiado, probá de nuevo");
        var imp = await contadora.ImportarUltimoScrapeAfipAsync();
        return (true, imp.Mensaje ?? "Listo");
    }
}

/// <summary>18/11/2026 — Una vez por día desde las 7 (hora argentina). Si falla, reintenta cada 2 horas.</summary>
public class ComprasAfipDiarioService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ComprasAfipRobot _robot;
    private readonly ILogger<ComprasAfipDiarioService> _log;
    public ComprasAfipDiarioService(IServiceScopeFactory f, ComprasAfipRobot robot, ILogger<ComprasAfipDiarioService> log)
    { _scopeFactory = f; _robot = robot; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken); } catch { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var ahoraAr = DateTime.UtcNow.AddHours(-3);
                if (ahoraAr.Hour >= ComprasAfipRobot.HoraAr && !ComprasAfipRobot.Corriendo)
                {
                    using var scope = _scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    var ultima = ComprasAfipRobot.ParseUtc(await ComprasAfipRobot.LeerAsync(db, ComprasAfipRobot.KeyUltima));
                    var intento = ComprasAfipRobot.ParseUtc(await ComprasAfipRobot.LeerAsync(db, ComprasAfipRobot.KeyIntento));
                    var yaHoy = ultima.HasValue && ultima.Value.AddHours(-3).Date >= ahoraAr.Date;
                    var reciente = intento.HasValue && DateTime.UtcNow - intento.Value < TimeSpan.FromHours(2);
                    if (!yaHoy && !reciente) await _robot.TraerAsync("automático 7 hs", stoppingToken);
                }
            }
            catch (Exception ex) { _log.LogWarning(ex, "[AFIP compras] error en el ciclo (no crítico)"); }
            try { await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); } catch { break; }
        }
    }
}
