using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Servicio de la bóveda de contraseñas.
///
/// SEGURIDAD:
///  - La contraseña maestra se hashea con BCrypt (factor 12). El hash se usa solo para
///    verificar que el usuario sabe la maestra. Es irreversible.
///  - La clave AES-256 se DERIVA de la maestra con PBKDF2 + SHA-256 (600.000 iteraciones)
///    usando un salt aleatorio guardado en la DB. NO se almacena la clave en disco.
///  - Los campos sensibles (usuario / password / notas) se cifran con AES-256-GCM
///    (cifrado autenticado). El blob guardado tiene el formato base64(IV ‖ Tag ‖ Ciphertext).
///  - Cuando el usuario "desbloquea", la clave queda en memoria del proceso atada a un token
///    de sesión, con expiración deslizante (auto-lock). Si el proceso reinicia, la clave
///    se pierde — el usuario tiene que volver a tipear la maestra.
/// </summary>
public class VaultService
{
    private readonly AppDbContext _db;
    private readonly ILogger<VaultService> _logger;

    // Cache token -> (key, expiresAt). expiresAt se actualiza con cada acceso.
    private static readonly ConcurrentDictionary<string, VaultSession> _sessions = new();

    private const int VAULT_ID = 1;
    private const int PBKDF2_ITERATIONS = 600_000;
    private const int KEY_BYTES = 32;          // AES-256
    private const int SALT_BYTES = 32;
    private const int GCM_NONCE_BYTES = 12;     // estándar GCM
    private const int GCM_TAG_BYTES = 16;

    public VaultService(AppDbContext db, ILogger<VaultService> logger)
    {
        _db = db;
        _logger = logger;
    }

    // ============================================================
    // STATUS / SETUP / UNLOCK / LOCK
    // ============================================================

    public async Task<VaultStatusDto> GetStatusAsync(string? token)
    {
        var s = await _db.VaultSettings.FindAsync(VAULT_ID);
        if (s is null) return new VaultStatusDto(false, false, 5);
        var unlocked = !string.IsNullOrEmpty(token) && _sessions.ContainsKey(token!) && !IsExpired(token!);
        return new VaultStatusDto(true, unlocked, s.AutoLockMinutes);
    }

    public async Task<bool> InitializeIfMissingAsync(string defaultPassword, int autoLockMinutes = 5)
    {
        var s = await _db.VaultSettings.FindAsync(VAULT_ID);
        if (s is not null) return false; // ya estaba

        var saltBytes = RandomNumberGenerator.GetBytes(SALT_BYTES);
        s = new VaultSetting
        {
            Id = VAULT_ID,
            MasterPasswordHash = BCrypt.Net.BCrypt.HashPassword(defaultPassword, workFactor: 12),
            KdfSalt = Convert.ToBase64String(saltBytes),
            AutoLockMinutes = autoLockMinutes,
            CreatedAt = DateTime.UtcNow
        };
        _db.VaultSettings.Add(s);
        await _db.SaveChangesAsync();
        _logger.LogInformation("Bóveda inicializada con la contraseña por defecto. Cambiala desde la UI cuanto antes.");
        return true;
    }

    public async Task<VaultStatusDto> SetupAsync(string password, int autoLockMinutes)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 6)
            throw new ArgumentException("La contraseña maestra debe tener al menos 6 caracteres.");

        var existing = await _db.VaultSettings.FindAsync(VAULT_ID);
        if (existing is not null)
            throw new InvalidOperationException("La bóveda ya está inicializada. Usá 'cambiar contraseña maestra' para modificarla.");

        var saltBytes = RandomNumberGenerator.GetBytes(SALT_BYTES);
        var s = new VaultSetting
        {
            Id = VAULT_ID,
            MasterPasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: 12),
            KdfSalt = Convert.ToBase64String(saltBytes),
            AutoLockMinutes = Math.Clamp(autoLockMinutes, 1, 60),
            CreatedAt = DateTime.UtcNow
        };
        _db.VaultSettings.Add(s);
        await _db.SaveChangesAsync();
        return new VaultStatusDto(true, false, s.AutoLockMinutes);
    }

    public async Task<VaultUnlockResponse?> UnlockAsync(string password)
    {
        var s = await _db.VaultSettings.FindAsync(VAULT_ID);
        if (s is null) throw new InvalidOperationException("La bóveda no está inicializada todavía.");
        if (string.IsNullOrEmpty(password)) return null;

        // 1. Verificar la maestra con BCrypt (rate-limit de hecho por el factor 12).
        if (!BCrypt.Net.BCrypt.Verify(password, s.MasterPasswordHash))
            return null;

        // 2. Derivar la clave AES-256 con PBKDF2.
        var salt = Convert.FromBase64String(s.KdfSalt);
        var key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            PBKDF2_ITERATIONS,
            HashAlgorithmName.SHA256,
            KEY_BYTES);

        // 3. Generar token de sesión y guardar la clave en memoria con expiración deslizante.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var session = new VaultSession(key, DateTime.UtcNow.AddMinutes(s.AutoLockMinutes), s.AutoLockMinutes);
        _sessions[token] = session;
        return new VaultUnlockResponse(token, s.AutoLockMinutes);
    }

    public void Lock(string token)
    {
        if (string.IsNullOrEmpty(token)) return;
        if (_sessions.TryRemove(token, out var session))
        {
            // Borrar la clave de memoria.
            CryptographicOperations.ZeroMemory(session.Key);
        }
    }

    /// <summary>Devuelve la clave si la sesión es válida y refresca su expiración.</summary>
    private byte[]? GetKeyForToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        if (!_sessions.TryGetValue(token, out var session)) return null;
        if (DateTime.UtcNow > session.ExpiresAt)
        {
            // Expiró: limpiar y devolver null.
            _sessions.TryRemove(token, out _);
            CryptographicOperations.ZeroMemory(session.Key);
            return null;
        }
        // Renovar (sliding window).
        session.ExpiresAt = DateTime.UtcNow.AddMinutes(session.AutoLockMinutes);
        return session.Key;
    }

    private bool IsExpired(string token)
        => !_sessions.TryGetValue(token, out var session) || DateTime.UtcNow > session.ExpiresAt;

    // ============================================================
    // ENTRIES
    // ============================================================

    public async Task<List<VaultEntryDto>> ListEntriesAsync(string token)
    {
        var key = GetKeyForToken(token) ?? throw new UnauthorizedAccessException("Bóveda bloqueada.");
        var entries = await _db.VaultEntries.OrderBy(e => e.Servicio).ToListAsync();
        var result = new List<VaultEntryDto>(entries.Count);
        foreach (var e in entries)
        {
            try
            {
                result.Add(ToDto(e, key));
            }
            catch (CryptographicException)
            {
                _logger.LogWarning("Entry {Id} no se pudo desencriptar (clave incorrecta).", e.Id);
            }
        }
        return result;
    }

    public async Task<List<string>> ListCategoriasAsync(string token)
    {
        _ = GetKeyForToken(token) ?? throw new UnauthorizedAccessException("Bóveda bloqueada.");
        return await _db.VaultEntries
            .Where(e => e.Categoria != null && e.Categoria != "")
            .Select(e => e.Categoria!)
            .Distinct()
            .OrderBy(c => c)
            .ToListAsync();
    }

    public async Task<VaultEntryDto> CreateEntryAsync(string token, VaultUpsertEntryRequest req)
    {
        var key = GetKeyForToken(token) ?? throw new UnauthorizedAccessException("Bóveda bloqueada.");
        if (string.IsNullOrWhiteSpace(req.Servicio)) throw new ArgumentException("El servicio es obligatorio.");

        var e = new VaultEntry
        {
            Servicio = req.Servicio.Trim(),
            Categoria = NormCategoria(req.Categoria),
            UsuarioEnc = Encrypt(req.Usuario ?? "", key),
            OtroEnc = EncOpt(req.Otro, key),
            PasswordEnc = Encrypt(req.Password ?? "", key),
            PinEnc = EncOpt(req.Pin, key),
            MailEnc = EncOpt(req.Mail, key),
            EnlaceEnc = EncOpt(req.Enlace, key),
            NotasEnc = EncOpt(req.Notas, key),
            CreatedAt = DateTime.UtcNow
        };
        _db.VaultEntries.Add(e);
        await _db.SaveChangesAsync();
        return ToDto(e, key);
    }

    public async Task<VaultEntryDto?> UpdateEntryAsync(string token, int id, VaultUpsertEntryRequest req)
    {
        var key = GetKeyForToken(token) ?? throw new UnauthorizedAccessException("Bóveda bloqueada.");
        var e = await _db.VaultEntries.FindAsync(id);
        if (e is null) return null;
        if (string.IsNullOrWhiteSpace(req.Servicio)) throw new ArgumentException("El servicio es obligatorio.");

        e.Servicio = req.Servicio.Trim();
        e.Categoria = NormCategoria(req.Categoria);
        e.UsuarioEnc = Encrypt(req.Usuario ?? "", key);
        e.OtroEnc = EncOpt(req.Otro, key);
        e.PasswordEnc = Encrypt(req.Password ?? "", key);
        e.PinEnc = EncOpt(req.Pin, key);
        e.MailEnc = EncOpt(req.Mail, key);
        e.EnlaceEnc = EncOpt(req.Enlace, key);
        e.NotasEnc = EncOpt(req.Notas, key);
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return ToDto(e, key);
    }

    private static string? EncOpt(string? plain, byte[] key)
        => string.IsNullOrEmpty(plain) ? null : Encrypt(plain, key);

    private static string? DecOpt(string? blob, byte[] key)
        => string.IsNullOrEmpty(blob) ? null : Decrypt(blob, key);

    private static string? NormCategoria(string? c)
        => string.IsNullOrWhiteSpace(c) ? null : c.Trim();

    private static VaultEntryDto ToDto(VaultEntry e, byte[] key) => new(
        e.Id,
        e.Servicio,
        e.Categoria,
        Decrypt(e.UsuarioEnc, key),
        DecOpt(e.OtroEnc, key),
        Decrypt(e.PasswordEnc, key),
        DecOpt(e.PinEnc, key),
        DecOpt(e.MailEnc, key),
        DecOpt(e.EnlaceEnc, key),
        DecOpt(e.NotasEnc, key),
        e.CreatedAt,
        e.UpdatedAt);

    public async Task<bool> DeleteEntryAsync(string token, int id)
    {
        _ = GetKeyForToken(token) ?? throw new UnauthorizedAccessException("Bóveda bloqueada.");
        var e = await _db.VaultEntries.FindAsync(id);
        if (e is null) return false;
        _db.VaultEntries.Remove(e);
        await _db.SaveChangesAsync();
        return true;
    }

    // ============================================================
    // IMPORTACIÓN MASIVA POR EXCEL
    // Columnas esperadas (case-insensitive, orden libre):
    //   nombre, categoria, usuario, otro, clave, pin, mail, enlace, comentarios
    // - "nombre" es obligatorio; el resto es opcional.
    // - Si ya existe una entry con el MISMO nombre (case-insensitive), se ACTUALIZA.
    // - Las categorías no se crean en una tabla aparte (es solo texto en la entry).
    // ============================================================
    public async Task<VaultImportResultDto> ImportFromExcelAsync(string token, Stream excelStream)
    {
        var key = GetKeyForToken(token) ?? throw new UnauthorizedAccessException("Bóveda bloqueada.");

        using var wb = new ClosedXML.Excel.XLWorkbook(excelStream);
        var ws = wb.Worksheets.FirstOrDefault() ?? throw new ArgumentException("El Excel no tiene hojas.");

        // Detectar columnas por nombre en la fila 1
        var lastCol = ws.LastColumnUsed()?.ColumnNumber() ?? 0;
        var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int c = 1; c <= lastCol; c++)
        {
            var h = (ws.Cell(1, c).GetString() ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(h)) cols[h] = c;
        }

        if (!cols.ContainsKey("nombre"))
            throw new ArgumentException("Falta la columna obligatoria 'nombre' en el Excel.");

        string? Cell(int row, string colName)
        {
            if (!cols.TryGetValue(colName, out var c)) return null;
            var v = ws.Cell(row, c).GetString();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        var existentes = await _db.VaultEntries.ToListAsync();
        var byName = existentes.ToDictionary(e => e.Servicio.ToLowerInvariant(), e => e);

        var creadas = 0; var actualizadas = 0; var saltadas = 0;
        var errores = new List<string>();

        var lastRow = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (int r = 2; r <= lastRow; r++)
        {
            try
            {
                var nombre = Cell(r, "nombre");
                if (string.IsNullOrWhiteSpace(nombre)) { saltadas++; continue; }

                var categoria = Cell(r, "categoria");
                var usuario = Cell(r, "usuario") ?? "";
                var otro = Cell(r, "otro");
                var clave = Cell(r, "clave") ?? "";
                var pin = Cell(r, "pin");
                var mail = Cell(r, "mail");
                var enlace = Cell(r, "enlace");
                var comentarios = Cell(r, "comentarios");

                if (byName.TryGetValue(nombre.ToLowerInvariant(), out var existing))
                {
                    existing.Servicio = nombre;
                    existing.Categoria = NormCategoria(categoria);
                    existing.UsuarioEnc = Encrypt(usuario, key);
                    existing.OtroEnc = EncOpt(otro, key);
                    existing.PasswordEnc = Encrypt(clave, key);
                    existing.PinEnc = EncOpt(pin, key);
                    existing.MailEnc = EncOpt(mail, key);
                    existing.EnlaceEnc = EncOpt(enlace, key);
                    existing.NotasEnc = EncOpt(comentarios, key);
                    existing.UpdatedAt = DateTime.UtcNow;
                    actualizadas++;
                }
                else
                {
                    var nueva = new VaultEntry
                    {
                        Servicio = nombre,
                        Categoria = NormCategoria(categoria),
                        UsuarioEnc = Encrypt(usuario, key),
                        OtroEnc = EncOpt(otro, key),
                        PasswordEnc = Encrypt(clave, key),
                        PinEnc = EncOpt(pin, key),
                        MailEnc = EncOpt(mail, key),
                        EnlaceEnc = EncOpt(enlace, key),
                        NotasEnc = EncOpt(comentarios, key),
                        CreatedAt = DateTime.UtcNow
                    };
                    _db.VaultEntries.Add(nueva);
                    byName[nombre.ToLowerInvariant()] = nueva;
                    creadas++;
                }
            }
            catch (Exception ex)
            {
                errores.Add($"Fila {r}: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync();
        return new VaultImportResultDto(creadas, actualizadas, saltadas, errores);
    }

    public static byte[] BuildTemplateExcel()
    {
        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.AddWorksheet("Bóveda");

        // Encabezados
        string[] headers = { "nombre", "categoria", "usuario", "otro", "clave", "pin", "mail", "enlace", "comentarios" };
        for (int i = 0; i < headers.Length; i++) ws.Cell(1, i + 1).Value = headers[i];

        var hdr = ws.Range(1, 1, 1, headers.Length);
        hdr.Style.Font.Bold = true;
        hdr.Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;

        // Fila de ejemplo
        ws.Cell(2, 1).Value = "ARCA OSMAR";
        ws.Cell(2, 2).Value = "ARCA";
        ws.Cell(2, 3).Value = "20123456789";
        ws.Cell(2, 4).Value = "";
        ws.Cell(2, 5).Value = "MiClave123!";
        ws.Cell(2, 6).Value = "";
        ws.Cell(2, 7).Value = "osmar@ejemplo.com";
        ws.Cell(2, 8).Value = "https://www.afip.gob.ar";
        ws.Cell(2, 9).Value = "Persona física, CUIT del titular";

        // Hoja de instrucciones
        var help = wb.AddWorksheet("Instrucciones");
        help.Cell(1, 1).Value = "Cómo usar esta plantilla";
        help.Cell(1, 1).Style.Font.Bold = true;
        help.Cell(1, 1).Style.Font.FontSize = 14;
        var tips = new[]
        {
            "1. Completá una fila por cada clave que quieras guardar.",
            "2. La única columna OBLIGATORIA es 'nombre'. Las demás son opcionales (dejá vacío si no aplica).",
            "3. 'categoria' agrupa las claves (ej: ARCA, Bancos, MercadoLibre). Si la categoría no existía, queda creada.",
            "4. 'otro' es un campo libre para datos extra (ej: número de adherente, código de cliente).",
            "5. Si subís el Excel dos veces con el mismo 'nombre', la entrada se ACTUALIZA (no se duplica).",
            "6. Borrá la fila de ejemplo de la hoja 'Bóveda' antes de subir.",
            "7. Los datos viajan encriptados con AES-256-GCM. La maestra no se guarda en disco."
        };
        for (int i = 0; i < tips.Length; i++) help.Cell(i + 3, 1).Value = tips[i];
        help.Columns().AdjustToContents();

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    // ============================================================
    // CAMBIAR MAESTRA: re-deriva la clave con la NUEVA password
    //                  y re-cifra TODAS las entries.
    // ============================================================
    public async Task ChangeMasterAsync(string token, string oldPassword, string newPassword)
    {
        var key = GetKeyForToken(token) ?? throw new UnauthorizedAccessException("Bóveda bloqueada.");
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < 6)
            throw new ArgumentException("La nueva contraseña maestra debe tener al menos 6 caracteres.");

        var s = await _db.VaultSettings.FindAsync(VAULT_ID)
                ?? throw new InvalidOperationException("Bóveda no inicializada.");
        if (!BCrypt.Net.BCrypt.Verify(oldPassword, s.MasterPasswordHash))
            throw new ArgumentException("La contraseña maestra actual no coincide.");

        // Generar nuevo salt + nueva clave derivada de la NUEVA password.
        var newSalt = RandomNumberGenerator.GetBytes(SALT_BYTES);
        var newKey = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(newPassword),
            newSalt,
            PBKDF2_ITERATIONS,
            HashAlgorithmName.SHA256,
            KEY_BYTES);

        // Re-cifrar todas las entries: desencriptar con la clave vieja y volver a cifrar con la nueva.
        var entries = await _db.VaultEntries.ToListAsync();
        foreach (var e in entries)
        {
            var usu = Decrypt(e.UsuarioEnc, key);
            var otro = DecOpt(e.OtroEnc, key);
            var pwd = Decrypt(e.PasswordEnc, key);
            var pin = DecOpt(e.PinEnc, key);
            var mail = DecOpt(e.MailEnc, key);
            var enlace = DecOpt(e.EnlaceEnc, key);
            var not = DecOpt(e.NotasEnc, key);

            e.UsuarioEnc = Encrypt(usu, newKey);
            e.OtroEnc = EncOpt(otro, newKey);
            e.PasswordEnc = Encrypt(pwd, newKey);
            e.PinEnc = EncOpt(pin, newKey);
            e.MailEnc = EncOpt(mail, newKey);
            e.EnlaceEnc = EncOpt(enlace, newKey);
            e.NotasEnc = EncOpt(not, newKey);
            e.UpdatedAt = DateTime.UtcNow;
        }

        s.MasterPasswordHash = BCrypt.Net.BCrypt.HashPassword(newPassword, workFactor: 12);
        s.KdfSalt = Convert.ToBase64String(newSalt);
        s.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        // Invalidar todas las sesiones — al cambiar la maestra el usuario debe re-loguear.
        foreach (var t in _sessions.Keys.ToList())
        {
            if (_sessions.TryRemove(t, out var sess))
                CryptographicOperations.ZeroMemory(sess.Key);
        }
        CryptographicOperations.ZeroMemory(newKey);
    }

    public async Task UpdateAutoLockAsync(string token, int minutes)
    {
        _ = GetKeyForToken(token) ?? throw new UnauthorizedAccessException("Bóveda bloqueada.");
        var s = await _db.VaultSettings.FindAsync(VAULT_ID)
                ?? throw new InvalidOperationException("Bóveda no inicializada.");
        s.AutoLockMinutes = Math.Clamp(minutes, 1, 60);
        s.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    // ============================================================
    // GENERADOR DE PASSWORDS SEGURAS
    // ============================================================
    public static string GenerateSecurePassword(int length, bool includeSymbols, bool includeNumbers, bool includeUppercase)
    {
        if (length < 4) length = 4;
        if (length > 128) length = 128;

        const string lower = "abcdefghijkmnpqrstuvwxyz";   // sin l, o
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";   // sin I, O
        const string nums = "23456789";                    // sin 0, 1
        const string syms = "!@#$%^&*()-_=+[]{};:,.?";

        var pool = new StringBuilder(lower);
        if (includeUppercase) pool.Append(upper);
        if (includeNumbers) pool.Append(nums);
        if (includeSymbols) pool.Append(syms);
        var poolStr = pool.ToString();

        var sb = new StringBuilder(length);
        var bytes = RandomNumberGenerator.GetBytes(length * 4);
        for (int i = 0; i < length; i++)
        {
            // Rejection-free: tomamos 4 bytes y mod por el tamaño del pool.
            // El tamaño del pool (≤80) es muy chico vs uint, el sesgo es despreciable.
            var idx = BitConverter.ToUInt32(bytes, i * 4) % (uint)poolStr.Length;
            sb.Append(poolStr[(int)idx]);
        }
        return sb.ToString();
    }

    // ============================================================
    // CRYPTO HELPERS — AES-256-GCM
    // Formato del blob: base64( IV(12) ‖ Tag(16) ‖ Ciphertext(N) )
    // ============================================================

    private static string Encrypt(string plaintext, byte[] key)
    {
        if (string.IsNullOrEmpty(plaintext)) plaintext = "";
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var iv = RandomNumberGenerator.GetBytes(GCM_NONCE_BYTES);
        var tag = new byte[GCM_TAG_BYTES];
        var cipher = new byte[plainBytes.Length];
        using var aes = new AesGcm(key, GCM_TAG_BYTES);
        aes.Encrypt(iv, plainBytes, cipher, tag);
        var combined = new byte[iv.Length + tag.Length + cipher.Length];
        Buffer.BlockCopy(iv, 0, combined, 0, iv.Length);
        Buffer.BlockCopy(tag, 0, combined, iv.Length, tag.Length);
        Buffer.BlockCopy(cipher, 0, combined, iv.Length + tag.Length, cipher.Length);
        return Convert.ToBase64String(combined);
    }

    private static string Decrypt(string blob, byte[] key)
    {
        if (string.IsNullOrEmpty(blob)) return "";
        var combined = Convert.FromBase64String(blob);
        if (combined.Length < GCM_NONCE_BYTES + GCM_TAG_BYTES)
            throw new CryptographicException("Blob corrupto.");
        var iv = new byte[GCM_NONCE_BYTES];
        var tag = new byte[GCM_TAG_BYTES];
        var cipher = new byte[combined.Length - GCM_NONCE_BYTES - GCM_TAG_BYTES];
        Buffer.BlockCopy(combined, 0, iv, 0, GCM_NONCE_BYTES);
        Buffer.BlockCopy(combined, GCM_NONCE_BYTES, tag, 0, GCM_TAG_BYTES);
        Buffer.BlockCopy(combined, GCM_NONCE_BYTES + GCM_TAG_BYTES, cipher, 0, cipher.Length);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, GCM_TAG_BYTES);
        aes.Decrypt(iv, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    private class VaultSession
    {
        public byte[] Key { get; }
        public DateTime ExpiresAt { get; set; }
        public int AutoLockMinutes { get; }

        public VaultSession(byte[] key, DateTime expiresAt, int autoLockMinutes)
        {
            Key = key;
            ExpiresAt = expiresAt;
            AutoLockMinutes = autoLockMinutes;
        }
    }
}
