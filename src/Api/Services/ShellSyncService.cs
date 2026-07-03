using System.Text.Json;

namespace Api.Services;

/// <summary>
/// Orquesta la lectura del saldo de Shell Flota: toma las credenciales de Shell y
/// la conexión de mail (integración email-smtp, para leer el token OTP), dispara el
/// robot, espera el resultado y guarda el saldo. Lo usan el botón manual y el job.
/// </summary>
public class ShellSyncService
{
    private readonly ShellAccountService _accounts;
    private readonly ShellScrapingService _scraping;
    private readonly IntegrationService _integrations;
    private readonly ILogger<ShellSyncService> _logger;

    public ShellSyncService(ShellAccountService accounts, ShellScrapingService scraping,
        IntegrationService integrations, ILogger<ShellSyncService> logger)
    {
        _accounts = accounts;
        _scraping = scraping;
        _integrations = integrations;
        _logger = logger;
    }

    public record SyncResult(bool Ok, string? Saldo, string? Error);

    /// <summary>Casilla de Gmail + clave de app (desde la integración email-smtp).</summary>
    private async Task<(string? user, string? pass)> GetGmailCredsAsync()
    {
        var integ = await _integrations.GetByProviderAsync("email-smtp");
        var pass = await _integrations.GetSecretAsync("email-smtp");
        if (integ is null || string.IsNullOrEmpty(pass)) return (null, null);

        string? user = null;
        if (!string.IsNullOrWhiteSpace(integ.Settings))
        {
            try
            {
                using var doc = JsonDocument.Parse(integ.Settings);
                var root = doc.RootElement;
                if (root.TryGetProperty("username", out var u) && !string.IsNullOrWhiteSpace(u.GetString())) user = u.GetString();
                else if (root.TryGetProperty("fromAddress", out var f)) user = f.GetString();
            }
            catch { }
        }
        return (user, pass);
    }

    public async Task<SyncResult> SincronizarAsync()
    {
        var dto = await _accounts.GetAsync();
        if (dto is null || !dto.HasPassword)
            return new SyncResult(false, null, "No hay usuario/clave de Shell cargados");
        var password = await _accounts.GetPasswordAsync();
        if (string.IsNullOrEmpty(password))
            return new SyncResult(false, null, "No se pudo leer la clave de Shell");

        var (gmailUser, gmailPass) = await GetGmailCredsAsync();
        if (string.IsNullOrEmpty(gmailUser) || string.IsNullOrEmpty(gmailPass))
            return await Fallar(dto.LastSaldo, "No hay conexión de mail configurada (Integraciones → Email). Se necesita para leer el token de Shell.");

        var (ok, error) = await _scraping.StartSaldoAsync(dto.Usuario, password, gmailUser, gmailPass);
        if (!ok) return await Fallar(dto.LastSaldo, error);

        ShellTestResultDto? result = null;
        for (int i = 0; i < 90; i++) // ~135s: el mail puede tardar
        {
            await Task.Delay(1500);
            var st = await _scraping.GetStatusAsync();
            if (!st.Running) { result = st.Result; break; }
        }
        if (result is null) return await Fallar(dto.LastSaldo, "El robot tardó demasiado. Probá de nuevo.");
        if (!result.Ok || string.IsNullOrEmpty(result.Saldo))
            return await Fallar(dto.LastSaldo, result.Error ?? "No se pudo leer el saldo.");

        await _accounts.GuardarSaldoAsync(result.Saldo, true, null);
        return new SyncResult(true, result.Saldo, null);
    }

    private async Task<SyncResult> Fallar(string? saldoPrevio, string? error)
    {
        try { await _accounts.GuardarSaldoAsync(null, false, error); } catch { }
        return new SyncResult(false, saldoPrevio, error);
    }
}
