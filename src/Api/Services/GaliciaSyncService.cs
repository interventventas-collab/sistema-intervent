namespace Api.Services;

/// <summary>
/// Orquesta la sincronización de movimientos de Galicia: dispara el robot (login +
/// descarga CSV), espera el resultado e importa al Extracto de Banco. Lo usan tanto
/// el botón manual (GaliciaController) como el job automático (GaliciaAutoSyncBackgroundService).
/// </summary>
public class GaliciaSyncService
{
    private readonly GaliciaAccountService _accounts;
    private readonly GaliciaScrapingService _scraping;
    private readonly ExtractoBancoImportService _import;
    private readonly ILogger<GaliciaSyncService> _logger;

    public GaliciaSyncService(GaliciaAccountService accounts, GaliciaScrapingService scraping,
        ExtractoBancoImportService import, ILogger<GaliciaSyncService> logger)
    {
        _accounts = accounts;
        _scraping = scraping;
        _import = import;
        _logger = logger;
    }

    public record SyncResult(bool Ok, int Nuevos, int SinCambios, string? Error, List<string>? Detalles);

    public async Task<SyncResult> SincronizarAsync()
    {
        var dto = await _accounts.GetAsync();
        if (dto is null || !dto.HasPassword)
            return new SyncResult(false, 0, 0, "No hay usuario/clave cargados", null);
        var password = await _accounts.GetPasswordAsync();
        if (string.IsNullOrEmpty(password))
            return new SyncResult(false, 0, 0, "No se pudo leer la clave", null);

        var (ok, error) = await _scraping.StartMovimientosAsync(dto.Usuario, password);
        if (!ok) return new SyncResult(false, 0, 0, error, null);

        // Esperar al robot (~85s máx).
        GaliciaTestResultDto? result = null;
        for (int i = 0; i < 57; i++)
        {
            await Task.Delay(1500);
            var st = await _scraping.GetStatusAsync();
            if (!st.Running) { result = st.Result; break; }
        }
        if (result is null)
            return new SyncResult(false, 0, 0, "El robot tardó demasiado. Probá de nuevo.", null);
        if (!result.Ok || string.IsNullOrEmpty(result.CsvBase64))
        {
            var msg = result.NeedsToken == true
                ? "El banco pidió un código de seguridad (token). No se pudo bajar automático."
                : (result.Error ?? "No se pudo descargar el extracto.");
            return new SyncResult(false, 0, 0, msg, null);
        }

        try
        {
            var bytes = Convert.FromBase64String(result.CsvBase64);
            var texto = System.Text.Encoding.UTF8.GetString(bytes);
            var res = await _import.ImportCsvTextAsync(texto, $"Galicia-auto-{DateTime.Now:yyyyMMdd-HHmm}.csv");
            return new SyncResult(true, res.Nuevos, res.SinCambios, null, res.Errores);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error importando CSV de Galicia");
            return new SyncResult(false, 0, 0, "Bajó el archivo pero falló al importarlo: " + ex.Message, null);
        }
    }
}
