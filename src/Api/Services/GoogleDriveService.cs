using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using DriveData = Google.Apis.Drive.v3.Data;

namespace Api.Services;

/// <summary>
/// Sube/lista archivos en Google Drive usando OAuth 2.0 (con refresh token).
/// Diseñado para cuentas gratuitas de gmail (sin Workspace) — las Service Accounts
/// no tienen storage quota en ese caso y devuelven "Service Accounts do not have storage quota".
///
/// Credenciales en la tabla Integrations (provider="google-drive"):
///   - AppSecret = refresh_token (obtenido en el flujo OAuth, se guarda solo después del consent del usuario)
///   - Settings JSON = { "clientId": "...", "clientSecret": "...", "folderId": "..." }
///
/// El access_token se refresca automáticamente cada vez que se necesita (el library
/// Google.Apis lo hace transparente vía UserCredential.RefreshTokenAsync).
///
/// Scope usado: drive.file (solo archivos que la app crea — más restrictivo y seguro).
/// </summary>
public class GoogleDriveService
{
    private readonly IntegrationService _intSvc;

    private static readonly string[] MesesEs = new[]
    {
        "", // index 0 vacío para usar 1..12 directo
        "01-Enero", "02-Febrero", "03-Marzo", "04-Abril",
        "05-Mayo", "06-Junio", "07-Julio", "08-Agosto",
        "09-Septiembre", "10-Octubre", "11-Noviembre", "12-Diciembre"
    };

    public GoogleDriveService(IntegrationService intSvc)
    {
        _intSvc = intSvc;
    }

    /// <summary>
    /// Lee los datos de configuración de Drive desde Integrations.
    /// Devuelve (clientId, clientSecret, refreshToken, rootFolderId).
    /// Lanza InvalidOperationException con mensaje claro si falta algo.
    /// </summary>
    private async Task<(string clientId, string clientSecret, string refreshToken, string rootFolderId)> GetConfigAsync()
    {
        var integration = await _intSvc.GetByProviderAsync("google-drive");
        if (integration is null)
            throw new InvalidOperationException("No hay integración de Google Drive configurada. Andá a Integraciones → Google Drive y configurala.");

        var refreshToken = await _intSvc.GetSecretAsync("google-drive");
        if (string.IsNullOrWhiteSpace(refreshToken))
            throw new InvalidOperationException("Google Drive no está conectado. Andá a Integraciones → Google Drive → Conectar con Google.");

        string? clientId = null, clientSecret = null, folderId = null;
        if (!string.IsNullOrWhiteSpace(integration.Settings))
        {
            try
            {
                using var doc = JsonDocument.Parse(integration.Settings);
                if (doc.RootElement.TryGetProperty("clientId", out var ci)) clientId = ci.GetString();
                if (doc.RootElement.TryGetProperty("clientSecret", out var cs)) clientSecret = cs.GetString();
                if (doc.RootElement.TryGetProperty("folderId", out var f)) folderId = f.GetString();
            }
            catch
            {
                // settings malformado: lo tratamos como ausente
            }
        }

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            throw new InvalidOperationException("Falta Client ID o Client Secret de OAuth en la configuración de Google Drive.");
        if (string.IsNullOrWhiteSpace(folderId))
            throw new InvalidOperationException("Falta el ID de la carpeta raíz de Drive (settings.folderId).");

        return (clientId, clientSecret, refreshToken, folderId);
    }

    /// <summary>
    /// Crea un DriveService autenticado con OAuth + refresh_token.
    /// El library refresca el access_token solo cuando se hace la primera request.
    /// </summary>
    private static DriveService BuildClient(string clientId, string clientSecret, string refreshToken)
    {
        var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets
            {
                ClientId = clientId,
                ClientSecret = clientSecret
            },
            Scopes = new[] { DriveService.ScopeConstants.DriveFile }
        });

        var tokenResponse = new TokenResponse
        {
            RefreshToken = refreshToken
            // No seteamos AccessToken — el library hace el refresh la primera vez.
        };

        var credential = new UserCredential(flow, "palanica-user", tokenResponse);

        return new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Palanica Sistema"
        });
    }

    /// <summary>
    /// Test de conexión: sube un archivo dummy a la carpeta raíz y lo borra.
    /// Devuelve true si todo OK. Lanza excepción con detalle si falla.
    /// </summary>
    public async Task<bool> TestConnectionAsync()
    {
        var (clientId, clientSecret, refreshToken, rootFolderId) = await GetConfigAsync();
        var service = BuildClient(clientId, clientSecret, refreshToken);

        var fileName = $"test-conexion-{DateTime.UtcNow:yyyyMMddHHmmss}.txt";
        var content = System.Text.Encoding.UTF8.GetBytes("Test conexión Palanica Sistema → Google Drive. OK.");

        var meta = new DriveData.File
        {
            Name = fileName,
            Parents = new[] { rootFolderId }
        };

        string? createdId = null;
        try
        {
            using var stream = new MemoryStream(content);
            var req = service.Files.Create(meta, stream, "text/plain");
            req.Fields = "id";
            var uploadResult = await req.UploadAsync();

            // Capturar el resultado real del upload para dar mensaje útil al usuario.
            if (uploadResult.Status != Google.Apis.Upload.UploadStatus.Completed)
            {
                var inner = uploadResult.Exception;
                var msg = inner?.Message ?? $"Upload terminó en estado {uploadResult.Status}";
                throw new InvalidOperationException($"Drive rechazó la subida: {msg}");
            }

            createdId = req.ResponseBody?.Id;
            if (string.IsNullOrEmpty(createdId))
                throw new InvalidOperationException("La subida se reportó completa pero no devolvió ID de archivo. Verificá que el Client ID/Secret y la carpeta sean correctos.");
            return true;
        }
        finally
        {
            // Mejor esfuerzo para limpiar el test, no rompemos si esto falla
            if (!string.IsNullOrEmpty(createdId))
            {
                try { await service.Files.Delete(createdId).ExecuteAsync(); } catch { }
            }
        }
    }

    /// <summary>
    /// Sube un archivo a Drive directamente en la carpeta raíz (estructura plana).
    /// Decisión 2026-05-28: el negocio imprime los comprobantes desde Drive y necesita
    /// verlos todos juntos sin tener que entrar a sub-niveles año/mes. El orden cronológico
    /// se mantiene porque el nombre del archivo ya incluye la fecha (CAFE-2026-MMDD-XXXX.pdf).
    /// Devuelve el ID del archivo + webViewLink para abrirlo en el browser.
    /// </summary>
    public async Task<(string fileId, string webViewLink)> UploadFileAsync(
        string fileName, byte[] content, string mimeType = "application/pdf")
    {
        var (clientId, clientSecret, refreshToken, rootFolderId) = await GetConfigAsync();
        var service = BuildClient(clientId, clientSecret, refreshToken);

        var meta = new DriveData.File
        {
            Name = fileName,
            Parents = new[] { rootFolderId }
        };

        using var stream = new MemoryStream(content);
        var req = service.Files.Create(meta, stream, mimeType);
        req.Fields = "id, webViewLink";
        await req.UploadAsync();

        var file = req.ResponseBody
            ?? throw new InvalidOperationException("Drive no devolvió metadata del archivo subido.");

        return (file.Id, file.WebViewLink ?? $"https://drive.google.com/file/d/{file.Id}/view");
    }

    /// <summary>
    /// Busca una subcarpeta por nombre dentro de parentFolderId. Si no existe, la crea.
    /// Con el scope drive.file la app solo ve carpetas/archivos que ella misma creó —
    /// por eso la PRIMERA llamada para un parentFolderId que el usuario creó manualmente
    /// (la carpeta raíz) puede no listar nada; en ese caso simplemente creamos la subcarpeta.
    /// </summary>
    private static async Task<string> GetOrCreateSubfolderAsync(DriveService service, string parentFolderId, string folderName)
    {
        // Escapamos comillas simples en el nombre por seguridad de la query Drive
        var safeName = folderName.Replace("'", "\\'");
        var q = $"name = '{safeName}' and '{parentFolderId}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";

        var listReq = service.Files.List();
        listReq.Q = q;
        listReq.Fields = "files(id, name)";
        listReq.PageSize = 10;
        var result = await listReq.ExecuteAsync();
        var existing = result.Files?.FirstOrDefault();
        if (existing != null) return existing.Id;

        var folderMeta = new DriveData.File
        {
            Name = folderName,
            MimeType = "application/vnd.google-apps.folder",
            Parents = new[] { parentFolderId }
        };
        var createReq = service.Files.Create(folderMeta);
        createReq.Fields = "id";
        var newFolder = await createReq.ExecuteAsync();
        return newFolder.Id;
    }
}
