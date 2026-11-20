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

    // El robot corre una operación a la vez y, después de dejar el resultado, tarda unos
    // segundos en cerrar el navegador. Antes de arrancar otra (ej: cheques justo después
    // de movimientos) esperamos a que quede libre.
    private async Task EsperarRobotLibreAsync()
    {
        for (int i = 0; i < 30; i++)
        {
            var st = await _scraping.GetStatusAsync();
            if (!st.Running) return;
            await Task.Delay(1000);
        }
    }

    /// <summary>2026-11-18: arma usuario/clave de la cuenta pedida (null = principal) y a qué
    /// "cuenta" se importa (null = Palanica, si no el Id de la otra sociedad).</summary>
    private async Task<(string? usuario, string? password, int? cuentaImport, string? error)> CredencialesAsync(int? accountId)
    {
        var principalId = await _accounts.PrincipalIdAsync();
        var acc = accountId is null
            ? await _accounts.GetEntityAsync()
            : await _accounts.GetEntityAsync(accountId.Value);
        if (acc is null || string.IsNullOrEmpty(acc.Password))
            return (null, null, null, "No hay usuario/clave cargados");
        int? cuentaImport = acc.Id == principalId ? null : acc.Id;
        return (acc.Usuario, acc.Password, cuentaImport, null);
    }

    public async Task<SyncResult> SincronizarAsync(int? accountId = null)
    {
        var (usuario, password, cuentaImport, credErr) = await CredencialesAsync(accountId);
        if (credErr is not null || usuario is null || password is null)
            return new SyncResult(false, 0, 0, credErr ?? "No hay usuario/clave cargados", null);

        await EsperarRobotLibreAsync();
        var (ok, error) = await _scraping.StartMovimientosAsync(usuario, password);
        if (!ok) return new SyncResult(false, 0, 0, error, null);

        // Esperar al robot (~8 min máx: si el banco está lento, el robot le tiene paciencia y reintenta).
        // Cortamos apenas hay resultado, sin esperar a que el robot cierre el navegador.
        GaliciaTestResultDto? result = null;
        for (int i = 0; i < 320; i++)
        {
            await Task.Delay(1500);
            var st = await _scraping.GetStatusAsync();
            if (!st.Running || st.Result is not null) { result = st.Result; break; }
        }
        if (result is null)
            return new SyncResult(false, 0, 0, "El banco está muy lento: el robot esperó varios minutos y no terminó. Probá de nuevo más tarde.", null);
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
            var res = await _import.ImportCsvTextAsync(texto, $"Galicia{(cuentaImport is null ? "" : $"-C{cuentaImport}")}-auto-{DateTime.Now:yyyyMMdd-HHmm}.csv", cuentaImport);
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
    public async Task<ChequesSyncResult> SincronizarChequesAsync(int? accountId = null)
    {
        var (usuario, password, cuentaImport, credErr) = await CredencialesAsync(accountId);
        if (credErr is not null || usuario is null || password is null)
            return new ChequesSyncResult(false, 0, 0, 0, credErr ?? "No hay usuario/clave cargados", null);

        await EsperarRobotLibreAsync();
        var (ok, error) = await _scraping.StartChequesAsync(usuario, password);
        if (!ok) return new ChequesSyncResult(false, 0, 0, 0, error, null);

        // Esperar al robot. Baja 3 archivos y puede reintentar si el banco está lento (~9 min máx,
        // por debajo de los 10 min que nginx deja abierto un pedido).
        // Cortamos apenas hay resultado, sin esperar a que el robot cierre el navegador.
        GaliciaTestResultDto? result = null;
        for (int i = 0; i < 360; i++)
        {
            await Task.Delay(1500);
            var st = await _scraping.GetStatusAsync();
            if (!st.Running || st.Result is not null) { result = st.Result; break; }
        }
        if (result is null)
            return new ChequesSyncResult(false, 0, 0, 0, "El banco está muy lento: el robot esperó varios minutos y no terminó. Probá de nuevo más tarde.", null);

        var algo = result.ChequesRecibidosB64 ?? result.ChequesEmitidosB64 ?? result.ChequesEndosadosB64;
        if (result.Ok && result.ChequesVacios == true && result.NeedsToken != true)
            return new ChequesSyncResult(true, 0, 0, 0, null, null);
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
                var res = await _chequesImport.ImportBytesAsync(bytes, $"Galicia{(cuentaImport is null ? "" : $"-C{cuentaImport}")}-{nombre}-{DateTime.Now:yyyyMMdd-HHmm}.xls", cuentaImport);
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
