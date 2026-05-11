using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Api.Services;

/// <summary>
/// Manejo de certificados .pfx de ARCA Webservices. El archivo vive en disco
/// (FileStorageService) bajo "Certificados ARCA/&lt;CUIT&gt;/". En la DB queda
/// solo el path relativo + metadata (alias, password, environment, vencimiento).
/// </summary>
public class ArcaWebserviceAccountService
{
    private readonly AppDbContext _db;
    private readonly FileStorageService _files;
    private readonly ILogger<ArcaWebserviceAccountService> _logger;

    private const long MaxPfxBytes = 10L * 1024 * 1024; // 10 MB
    private const string CertsRootFolder = "Certificados ARCA";

    public ArcaWebserviceAccountService(AppDbContext db, FileStorageService files, ILogger<ArcaWebserviceAccountService> logger)
    {
        _db = db;
        _files = files;
        _logger = logger;
    }

    private static ArcaWebserviceAccountDto Map(ArcaWebserviceAccount a) => new(
        a.Id,
        a.Cuit,
        string.IsNullOrEmpty(a.Alias) ? null : a.Alias,
        a.FileName,
        a.FilePath,
        a.Password,
        a.Environment,
        a.ExpiresAt,
        a.IsActive,
        a.CreatedAt,
        a.UpdatedAt
    );

    /// <summary>Saca guiones y deja solo dígitos. Devuelve null si no quedan 11.</summary>
    public static string? NormalizeCuit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length == 11 ? digits : null;
    }

    private static string NormalizeEnvironment(string? raw)
    {
        var v = (raw ?? "").Trim().ToLowerInvariant();
        return v == "homologation" ? "homologation" : "production";
    }

    public async Task<List<ArcaWebserviceAccountDto>> GetAllAsync()
    {
        var list = await _db.ArcaWebserviceAccounts
            .OrderBy(a => a.Cuit).ThenBy(a => a.Alias).ThenBy(a => a.Id)
            .ToListAsync();
        return list.Select(Map).ToList();
    }

    public async Task<ArcaWebserviceAccountDto?> GetByIdAsync(int id)
    {
        var a = await _db.ArcaWebserviceAccounts.FindAsync(id);
        return a is null ? null : Map(a);
    }

    public async Task<(bool ok, string? error, ArcaWebserviceAccountDto? dto)> CreateAsync(
        string cuitRaw, string? alias, string? password, string? environment,
        string originalFileName, byte[] fileBytes)
    {
        // ---- Validaciones ----
        var cuit = NormalizeCuit(cuitRaw);
        if (cuit is null) return (false, "El CUIT debe tener 11 dígitos", null);

        if (fileBytes is null || fileBytes.Length == 0)
            return (false, "El archivo está vacío", null);
        if (fileBytes.Length > MaxPfxBytes)
            return (false, $"El archivo supera el máximo de 10 MB", null);

        if (string.IsNullOrWhiteSpace(originalFileName) ||
            !originalFileName.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
            return (false, "El archivo debe terminar en .pfx", null);

        var env = NormalizeEnvironment(environment);

        // ---- Sanitizar nombre + resolver path único en disco ----
        string safeName;
        try { safeName = FileStorageService.SanitizeName(originalFileName); }
        catch { return (false, "Nombre de archivo inválido", null); }

        var folderRel = $"{CertsRootFolder}/{cuit}";
        var folderAbs = _files.ResolveSafe(folderRel);
        Directory.CreateDirectory(folderAbs);

        // Si ya existe ese filename → sumar sufijo (2), (3), etc.
        var (finalName, finalAbs) = ResolveUniqueFile(folderAbs, safeName);
        var relPath = $"{folderRel}/{finalName}";

        // ---- Validar duplicado en DB (Cuit + FileName) ----
        var dup = await _db.ArcaWebserviceAccounts.FirstOrDefaultAsync(a =>
            a.Cuit == cuit && a.FileName == finalName);
        if (dup is not null)
            return (false, "Ya existe un certificado con ese nombre para este CUIT", null);

        // ---- Escribir archivo en disco ----
        try
        {
            await File.WriteAllBytesAsync(finalAbs, fileBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar el .pfx en disco");
            return (false, "No se pudo guardar el archivo en disco", null);
        }

        // ---- Leer fecha de vencimiento (best-effort) ----
        DateTime? expires = TryReadCertExpiry(fileBytes, password);

        var entity = new ArcaWebserviceAccount
        {
            Cuit = cuit,
            Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
            FileName = finalName,
            FilePath = relPath,
            Password = string.IsNullOrEmpty(password) ? null : password,
            Environment = env,
            ExpiresAt = expires,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.ArcaWebserviceAccounts.Add(entity);
        await _db.SaveChangesAsync();
        return (true, null, Map(entity));
    }

    public async Task<(bool ok, string? error, ArcaWebserviceAccountDto? dto)> UpdateAsync(
        int id, UpdateArcaWebserviceAccountRequest req)
    {
        var entity = await _db.ArcaWebserviceAccounts.FindAsync(id);
        if (entity is null) return (false, "Certificado no encontrado", null);

        if (req.Alias is not null)
            entity.Alias = string.IsNullOrWhiteSpace(req.Alias) ? null : req.Alias.Trim();

        // Password: si viene null, no tocar. Si viene "" → blanquear.
        if (req.Password is not null)
            entity.Password = string.IsNullOrEmpty(req.Password) ? null : req.Password;

        if (req.Environment is not null)
            entity.Environment = NormalizeEnvironment(req.Environment);

        if (req.IsActive.HasValue)
            entity.IsActive = req.IsActive.Value;

        // Si cambió la password, re-leer vencimiento
        if (req.Password is not null)
        {
            try
            {
                var abs = _files.ResolveSafe(entity.FilePath);
                if (File.Exists(abs))
                {
                    var bytes = await File.ReadAllBytesAsync(abs);
                    entity.ExpiresAt = TryReadCertExpiry(bytes, entity.Password);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "No se pudo re-leer vencimiento del .pfx tras cambio de password");
            }
        }

        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (true, null, Map(entity));
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var entity = await _db.ArcaWebserviceAccounts.FindAsync(id);
        if (entity is null) return false;

        // Borrar archivo en disco (best-effort, no fallar si no existe)
        try
        {
            var abs = _files.ResolveSafe(entity.FilePath);
            if (File.Exists(abs)) File.Delete(abs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo borrar el .pfx del disco: {Path}", entity.FilePath);
        }

        _db.ArcaWebserviceAccounts.Remove(entity);
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Si "cert.pfx" ya existe en el folder, busca cert (2).pfx, cert (3).pfx, etc.
    /// </summary>
    private static (string finalName, string finalAbs) ResolveUniqueFile(string folderAbs, string desiredName)
    {
        var ext = Path.GetExtension(desiredName); // ".pfx"
        var stem = Path.GetFileNameWithoutExtension(desiredName);
        var abs = Path.Combine(folderAbs, desiredName);
        if (!File.Exists(abs)) return (desiredName, abs);

        for (var n = 2; n < 1000; n++)
        {
            var candidate = $"{stem} ({n}){ext}";
            var candAbs = Path.Combine(folderAbs, candidate);
            if (!File.Exists(candAbs)) return (candidate, candAbs);
        }
        // Fallback ridículo: timestamp
        var fallback = $"{stem}-{DateTime.UtcNow.Ticks}{ext}";
        return (fallback, Path.Combine(folderAbs, fallback));
    }

    // ============================================================
    // Wizard de generación de certificado (clave privada + CSR + .pfx)
    // ============================================================

    /// <summary>
    /// Paso 1 del wizard: genera RSA 2048 + CSR firmado con SHA256/PKCS1 con
    /// el subject `C=AR, O={alias}, CN={alias}, OID.2.5.4.5=CUIT {cuit}`.
    /// Guarda la key + CSR en ArcaCsrRequests para que el usuario pueda ir
    /// a ARCA, descargar el .crt y volver más tarde a finalizar.
    /// </summary>
    public async Task<(bool ok, string? error, GenerateCsrResponseDto? dto)> GenerateCsrAsync(string cuitRaw, string? aliasRaw)
    {
        var cuit = NormalizeCuit(cuitRaw);
        if (cuit is null) return (false, "El CUIT debe tener 11 dígitos", null);
        var alias = (aliasRaw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(alias)) return (false, "El alias es obligatorio", null);

        try
        {
            using var rsa = RSA.Create(2048);
            // RFC 4514: escapamos caracteres reservados en el componente DN para
            // que parsee bien aunque el alias tenga comas, signos =, etc.
            var aliasEsc = EscapeDnComponent(alias);
            var subjectStr = $"C=AR, O={aliasEsc}, CN={aliasEsc}, OID.2.5.4.5=CUIT {cuit}";
            var dn = new X500DistinguishedName(subjectStr);
            var req = new CertificateRequest(dn, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            var csrPem = req.CreateSigningRequestPem();
            var privateKeyPem = rsa.ExportPkcs8PrivateKeyPem();

            var entity = new ArcaCsrRequest
            {
                Cuit = cuit,
                Alias = alias,
                PrivateKeyPem = privateKeyPem,
                CsrPem = csrPem,
                CreatedAt = DateTime.UtcNow,
            };
            _db.ArcaCsrRequests.Add(entity);
            await _db.SaveChangesAsync();

            var fileName = $"{cuit}_{FileStorageService.SanitizeName(alias)}.csr";
            var dto = new GenerateCsrResponseDto(entity.Id, fileName, csrPem, dn.Name);
            return (true, null, dto);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generando CSR");
            return (false, "No se pudo generar el CSR: " + ex.Message, null);
        }
    }

    /// <summary>Para el endpoint de descarga del .csr.</summary>
    public async Task<(byte[] bytes, string fileName)?> GetCsrDownloadAsync(int id)
    {
        var entity = await _db.ArcaCsrRequests.FindAsync(id);
        if (entity is null) return null;
        var fileName = $"{entity.Cuit}_{FileStorageService.SanitizeName(entity.Alias)}.csr";
        return (Encoding.UTF8.GetBytes(entity.CsrPem), fileName);
    }

    /// <summary>
    /// Paso 3 del wizard: combina la clave privada del pedido con el .crt
    /// devuelto por ARCA, genera el .pfx, lo guarda en disco y crea el
    /// registro en ArcaWebserviceAccounts. Después borra el pedido de CSR.
    /// </summary>
    public async Task<(bool ok, string? error, ArcaWebserviceAccountDto? dto)> FinalizeCsrAsync(
        int csrId, byte[] crtBytes, string? password, string? environment, string? aliasOverride)
    {
        var entity = await _db.ArcaCsrRequests.FindAsync(csrId);
        if (entity is null) return (false, "Pedido de CSR no encontrado (puede que haya sido finalizado o eliminado)", null);

        if (crtBytes is null || crtBytes.Length == 0)
            return (false, "Falta el archivo .crt", null);

        // ---- Parsear el .crt (PEM o DER) ----
        X509Certificate2 cert;
        try
        {
            var asString = Encoding.UTF8.GetString(crtBytes);
            if (asString.Contains("-----BEGIN CERTIFICATE-----"))
                cert = X509Certificate2.CreateFromPem(asString);
            else
                cert = new X509Certificate2(crtBytes);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error parseando .crt");
            return (false, "No se pudo leer el archivo .crt — ¿es un certificado válido?", null);
        }

        // ---- Combinar con la clave privada y exportar como .pfx ----
        byte[] pfxBytes;
        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(entity.PrivateKeyPem);
            using var certWithKey = cert.CopyWithPrivateKey(rsa);
            pfxBytes = certWithKey.Export(X509ContentType.Pfx, password ?? "");
        }
        catch (Exception ex)
        {
            cert.Dispose();
            _logger.LogWarning(ex, "Error combinando clave privada con .crt");
            return (false, "La clave privada no corresponde al certificado. ¿Subiste el .crt que ARCA devolvió para este pedido?", null);
        }
        finally
        {
            cert.Dispose();
        }

        // ---- Validar parseo del .pfx + leer vencimiento ----
        DateTime? expiresAt = null;
        try
        {
            using var validate = new X509Certificate2(pfxBytes, password ?? "", X509KeyStorageFlags.EphemeralKeySet);
            expiresAt = validate.NotAfter;
        }
        catch (Exception ex)
        {
            // Best-effort: no fallar si no se puede re-validar, el .pfx ya fue exportado
            _logger.LogDebug(ex, "No se pudo re-validar el .pfx generado, dejo ExpiresAt en null");
        }

        // ---- Guardar el .pfx en disco ----
        var aliasFinal = string.IsNullOrWhiteSpace(aliasOverride) ? entity.Alias : aliasOverride.Trim();
        string safeName;
        try { safeName = FileStorageService.SanitizeName($"{aliasFinal}.pfx"); }
        catch { return (false, "Alias inválido para usar como nombre de archivo", null); }

        var folderRel = $"{CertsRootFolder}/{entity.Cuit}";
        var folderAbs = _files.ResolveSafe(folderRel);
        Directory.CreateDirectory(folderAbs);

        var (finalName, finalAbs) = ResolveUniqueFile(folderAbs, safeName);
        var relPath = $"{folderRel}/{finalName}";

        try
        {
            await File.WriteAllBytesAsync(finalAbs, pfxBytes);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo guardar el .pfx generado");
            return (false, "No se pudo guardar el .pfx en disco: " + ex.Message, null);
        }

        // ---- Crear el registro en ArcaWebserviceAccounts + borrar el pedido ----
        var account = new ArcaWebserviceAccount
        {
            Cuit = entity.Cuit,
            Alias = aliasFinal,
            FileName = finalName,
            FilePath = relPath,
            Password = string.IsNullOrEmpty(password) ? null : password,
            Environment = NormalizeEnvironment(environment),
            ExpiresAt = expiresAt,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
        };
        _db.ArcaWebserviceAccounts.Add(account);

        // La clave privada ya no se necesita más — quedó embebida en el .pfx
        _db.ArcaCsrRequests.Remove(entity);

        await _db.SaveChangesAsync();
        return (true, null, Map(account));
    }

    /// <summary>
    /// Escapa caracteres reservados de un componente DN (RFC 4514). Sin esto,
    /// un alias con "," o "=" rompe el parseo de X500DistinguishedName.
    /// </summary>
    private static string EscapeDnComponent(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' || c == ',' || c == '=' || c == '+' ||
                c == '<' || c == '>' || c == '#' || c == ';' || c == '"')
            {
                sb.Append('\\').Append(c);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Intenta abrir el .pfx con la password dada. Si falla, prueba con vacía.
    /// Devuelve cert.NotAfter o null si no se pudo parsear.
    /// </summary>
    private DateTime? TryReadCertExpiry(byte[] bytes, string? password)
    {
        // Intento 1: con la password recibida (puede ser null o "")
        var pw = password ?? "";
        try
        {
            using var cert = new X509Certificate2(bytes, pw, X509KeyStorageFlags.EphemeralKeySet);
            return cert.NotAfter;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Intento 1 de leer .pfx falló (con pass dada)");
        }

        // Intento 2: con password vacía (si la dada no era vacía)
        if (!string.IsNullOrEmpty(pw))
        {
            try
            {
                using var cert = new X509Certificate2(bytes, "", X509KeyStorageFlags.EphemeralKeySet);
                return cert.NotAfter;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Intento 2 de leer .pfx falló (con pass vacía)");
            }
        }

        return null; // best-effort: no rompemos la carga
    }
}
