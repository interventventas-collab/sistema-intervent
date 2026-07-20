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
    private readonly ChequesBancoImportService _chequesImport;
    private readonly ILogger<GaliciaSyncService> _logger;

    public GaliciaSyncService(GaliciaAccountService accounts, GaliciaScrapingService scraping,
        ExtractoBancoImportService import, ChequesBancoImportService chequesImport,
        ILogger<GaliciaSyncService> logger)
    {
        _accounts = accounts;
        _scraping = scraping;
        _import = import;
        _chequesImport = chequesImport;
        _logger = logger;
    }

    public record SyncResult(bool Ok, int Nuevos, int SinCambios, string? Error, List<string>? Detalles);
    public record ChequesSyncResult(bool Ok, int Nuevos, int Actualizados, int SinCambios, string? Error, List<string>? Detalles);

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

    /// <summary>
    /// Sincroniza los cheques: el robot entra, baja los 3 listados (.XLS) de
    /// Recibidos/Emitidos/Endosados y los importa a Cafe_ChequesBanco (dedup por ID del banco).
    /// Es el mismo flujo que los movimientos, pero para la sección Cheques.
    /// </summary>
    public async Task<ChequesSyncResult> SincronizarChequesAsync()
    {
        var dto = await _accounts.GetAsync();
        if (dto is null || !dto.HasPassword)
            return new ChequesSyncResult(false, 0, 0, 0, "No hay usuario/clave cargados", null);
        var password = await _accounts.GetPasswordAsync();
        if (string.IsNullOrEmpty(password))
            return new ChequesSyncResult(false, 0, 0, 0, "No se pudo leer la clave", null);

        var (ok, error) = await _scraping.StartChequesAsync(dto.Usuario, password);
        if (!ok) return new ChequesSyncResult(false, 0, 0, 0, error, null);

        // Esperar al robot. Baja 3 archivos, así que damos más margen (~140s).
        GaliciaTestResultDto? result = null;
        for (int i = 0; i < 95; i++)
        {
            await Task.Delay(1500);
            var st = await _scraping.GetStatusAsync();
            if (!st.Running) { result = st.Result; break; }
        }
        if (result is null)
            return new ChequesSyncResult(false, 0, 0, 0, "El robot tardó demasiado. Probá de nuevo.", null);

        var algo = result.ChequesRecibidosB64 ?? result.ChequesEmitidosB64 ?? result.ChequesEndosadosB64;
        if (!result.Ok || string.IsNullOrEmpty(algo))
        {
            var msg = result.NeedsToken == true
                ? "El banco pidió un código de seguridad (token). No se pudo bajar automático."
                : (result.Error ?? "No se pudieron descargar los cheques.");
            return new ChequesSyncResult(false, 0, 0, 0, msg, null);
        }

        int nuevos = 0, actualizados = 0, sinCambios = 0;
        var detalles = new List<string>();
        // Cualquier error parcial que reportó el robot (ej: un listado que no pudo bajar).
        if (result.ChequesErrores is { Count: > 0 }) detalles.AddRange(result.ChequesErrores);

        async Task ImportarUno(string? b64, string nombre)
        {
            if (string.IsNullOrEmpty(b64)) return;
            try
            {
                var bytes = Convert.FromBase64String(b64);
                var res = await _chequesImport.ImportBytesAsync(bytes, $"Galicia-{nombre}-{DateTime.Now:yyyyMMdd-HHmm}.xls");
                nuevos += res.Nuevos;
                actualizados += res.Actualizados;
                sinCambios += res.SinCambios;
                if (res.Errores is { Count: > 0 }) detalles.AddRange(res.Errores.Select(e => $"{nombre}: {e}"));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error importando cheques {Nombre} de Galicia", nombre);
                detalles.Add($"{nombre}: falló al importar ({ex.Message})");
            }
        }

        await ImportarUno(result.ChequesRecibidosB64, "recibidos");
        await ImportarUno(result.ChequesEmitidosB64, "emitidos");
        await ImportarUno(result.ChequesEndosadosB64, "endosados");

        return new ChequesSyncResult(true, nuevos, actualizados, sinCambios, null, detalles.Count > 0 ? detalles : null);
    }
}
