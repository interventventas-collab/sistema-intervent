using Api.Data;
using Api.Models;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class BackupService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly ILogger<BackupService> _logger;

    // Path visto desde la API (mapea al volume compartido con SQL Server).
    public const string ApiBackupsPath = "/data/backups";

    // Path visto por SQL Server (mismo volume, distinto mount point dentro del container de SQL).
    public const string SqlBackupsPath = "/var/opt/mssql/backup";

    public const string RetentionSettingKey = "BackupRetentionDays";
    public const int DefaultRetentionDays = 7;

    public BackupService(AppDbContext db, IConfiguration config, ILogger<BackupService> logger)
    {
        _db = db;
        _config = config;
        _logger = logger;
        Directory.CreateDirectory(ApiBackupsPath);
    }

    // ---- Retencion ----

    public async Task<int> GetRetentionDaysAsync()
    {
        var s = await _db.AppSettings.FirstOrDefaultAsync(a => a.Key == RetentionSettingKey);
        if (s is null || !int.TryParse(s.Value, out var days) || days <= 0)
            return DefaultRetentionDays;
        return days;
    }

    public async Task SetRetentionDaysAsync(int days)
    {
        if (days < 1) days = 1;
        if (days > 365) days = 365;

        var s = await _db.AppSettings.FirstOrDefaultAsync(a => a.Key == RetentionSettingKey);
        if (s is null)
            _db.AppSettings.Add(new AppSetting { Key = RetentionSettingKey, Value = days.ToString(), UpdatedAt = DateTime.UtcNow });
        else
        {
            s.Value = days.ToString();
            s.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    // ---- Listar ----

    public async Task<List<BackupFile>> ListAsync()
    {
        var all = await _db.BackupFiles
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync();

        // Reconciliacion: un backup puede quedar marcado "InProgress" si la DB se restauro desde si misma
        // (el UPDATE a "Completed" queda fuera del backup). Si el archivo existe en disco, actualizamos estado.
        var stale = all
            .Where(b => b.Status == "InProgress" && b.CreatedAt < DateTime.UtcNow.AddMinutes(-2))
            .ToList();

        bool dirty = false;
        foreach (var b in stale)
        {
            var path = Path.Combine(ApiBackupsPath, b.FileName);
            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                b.SizeBytes = info.Length;
                b.Status = "Completed";
                dirty = true;
            }
        }
        if (dirty) await _db.SaveChangesAsync();
        return all;
    }

    // ---- Crear ----

    public async Task<BackupFile> CreateBackupAsync(string backupType, int? userId, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ApiBackupsPath);

        var fileName = $"AIml_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
        var record = new BackupFile
        {
            FileName = fileName,
            BackupType = backupType,
            Status = "InProgress",
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };
        _db.BackupFiles.Add(record);
        await _db.SaveChangesAsync(ct);

        try
        {
            var sqlPath = $"{SqlBackupsPath}/{fileName}";
            var cs = _config.GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("ConnectionString no configurada");

            using var conn = new SqlConnection(cs);
            await conn.OpenAsync(ct);
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 600; // 10 min
            cmd.CommandText = $"BACKUP DATABASE [AIml] TO DISK = @path WITH INIT, FORMAT, NAME = @name";
            cmd.Parameters.AddWithValue("@path", sqlPath);
            cmd.Parameters.AddWithValue("@name", $"AIml backup {DateTime.UtcNow:u}");
            await cmd.ExecuteNonQueryAsync(ct);

            var apiPath = Path.Combine(ApiBackupsPath, fileName);
            // Esperar unos segundos a que el archivo este disponible (el volume es el mismo pero el FS puede tardar)
            for (int i = 0; i < 10 && !File.Exists(apiPath); i++)
                await Task.Delay(500, ct);

            if (!File.Exists(apiPath))
                throw new InvalidOperationException($"El backup no aparecio en {apiPath} despues de ejecutar BACKUP DATABASE.");

            var info = new FileInfo(apiPath);
            record.SizeBytes = info.Length;
            record.Status = "Completed";
            await _db.SaveChangesAsync(ct);
            return record;
        }
        catch (Exception ex)
        {
            record.Status = "Failed";
            record.ErrorMessage = ex.Message;
            await _db.SaveChangesAsync(ct);
            _logger.LogError(ex, "Error creando backup {FileName}", fileName);
            throw;
        }
    }

    // ---- Subir .bak externo ----

    public async Task<BackupFile> UploadBackupAsync(Stream content, string originalFileName, int? userId, CancellationToken ct = default)
    {
        Directory.CreateDirectory(ApiBackupsPath);

        if (!originalFileName.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Solo se aceptan archivos .bak");

        var safeName = SanitizeFileName(originalFileName);
        // Si ya existe uno con el mismo nombre, anteponer timestamp
        var destPath = Path.Combine(ApiBackupsPath, safeName);
        if (File.Exists(destPath))
        {
            var stem = Path.GetFileNameWithoutExtension(safeName);
            safeName = $"{stem}_{DateTime.Now:yyyyMMdd_HHmmss}.bak";
            destPath = Path.Combine(ApiBackupsPath, safeName);
        }

        using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(fs, ct);
        }

        var info = new FileInfo(destPath);
        var record = new BackupFile
        {
            FileName = safeName,
            SizeBytes = info.Length,
            BackupType = "Subido",
            Status = "Completed",
            CreatedByUserId = userId,
            CreatedAt = DateTime.UtcNow
        };
        _db.BackupFiles.Add(record);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    // ---- Descargar ----

    public (Stream stream, string fileName)? OpenDownload(string fileName)
    {
        var safeName = SanitizeFileName(fileName);
        var path = Path.Combine(ApiBackupsPath, safeName);
        if (!File.Exists(path)) return null;
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return (fs, safeName);
    }

    // ---- Restaurar ----

    public async Task RestoreAsync(string fileName, CancellationToken ct = default)
    {
        var safeName = SanitizeFileName(fileName);
        var apiPath = Path.Combine(ApiBackupsPath, safeName);
        if (!File.Exists(apiPath))
            throw new FileNotFoundException($"No se encontro el archivo {safeName}");

        var sqlPath = $"{SqlBackupsPath}/{safeName}";
        var cs = _config.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionString no configurada");

        // RESTORE debe correr en la base master y requiere acceso exclusivo a AIml.
        var masterCs = new SqlConnectionStringBuilder(cs) { InitialCatalog = "master" }.ToString();

        using var conn = new SqlConnection(masterCs);
        await conn.OpenAsync(ct);

        // Forzar desconexion de usuarios de AIml.
        using (var kick = conn.CreateCommand())
        {
            kick.CommandTimeout = 60;
            kick.CommandText = "IF DB_ID('AIml') IS NOT NULL ALTER DATABASE [AIml] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;";
            await kick.ExecuteNonQueryAsync(ct);
        }

        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = 1200; // 20 min
            cmd.CommandText = "RESTORE DATABASE [AIml] FROM DISK = @path WITH REPLACE, RECOVERY";
            cmd.Parameters.AddWithValue("@path", sqlPath);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            using var multi = conn.CreateCommand();
            multi.CommandTimeout = 60;
            multi.CommandText = "IF DB_ID('AIml') IS NOT NULL ALTER DATABASE [AIml] SET MULTI_USER;";
            try { await multi.ExecuteNonQueryAsync(ct); } catch { /* best effort */ }
        }
    }

    // ---- Eliminar ----

    public async Task<bool> DeleteAsync(int id)
    {
        var record = await _db.BackupFiles.FindAsync(id);
        if (record is null) return false;

        var path = Path.Combine(ApiBackupsPath, record.FileName);
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignorar */ }

        _db.BackupFiles.Remove(record);
        await _db.SaveChangesAsync();
        return true;
    }

    // ---- Limpieza por retencion ----

    public async Task<int> CleanupOldAsync(CancellationToken ct = default)
    {
        var days = await GetRetentionDaysAsync();
        var cutoff = DateTime.UtcNow.AddDays(-days);

        var old = await _db.BackupFiles
            .Where(b => b.CreatedAt < cutoff && b.Status == "Completed")
            .ToListAsync(ct);

        foreach (var b in old)
        {
            var path = Path.Combine(ApiBackupsPath, b.FileName);
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignorar */ }
            _db.BackupFiles.Remove(b);
        }
        await _db.SaveChangesAsync(ct);
        return old.Count;
    }

    // ---- Helpers ----

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Nombre de archivo vacio");

        var justName = Path.GetFileName(name);
        if (string.IsNullOrEmpty(justName) || justName.Contains(".."))
            throw new ArgumentException("Nombre invalido");
        foreach (var c in Path.GetInvalidFileNameChars())
            if (justName.Contains(c)) throw new ArgumentException("Nombre invalido");

        return justName;
    }
}
