using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class FileStorageService
{
    private readonly AppDbContext _db;
    private readonly string _root;

    public const string ProviderSettingKey = "files.storage.provider";
    public const string DefaultProvider = "local";

    public FileStorageService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _root = Environment.GetEnvironmentVariable("FILES_ROOT")
                ?? config["FilesRoot"]
                ?? "/data/files";
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public async Task<string> GetProviderAsync()
    {
        var s = await _db.AppSettings.FirstOrDefaultAsync(a => a.Key == ProviderSettingKey);
        return string.IsNullOrEmpty(s?.Value) ? DefaultProvider : s!.Value;
    }

    public async Task SetProviderAsync(string provider)
    {
        var s = await _db.AppSettings.FirstOrDefaultAsync(a => a.Key == ProviderSettingKey);
        if (s is null)
        {
            _db.AppSettings.Add(new Models.AppSetting { Key = ProviderSettingKey, Value = provider, UpdatedAt = DateTime.UtcNow });
        }
        else
        {
            s.Value = provider;
            s.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
    }

    // Resuelve un path relativo a _root validando que no escape con ".."
    public string ResolveSafe(string? relativePath)
    {
        var rel = (relativePath ?? "").Replace('\\', '/').Trim('/');
        if (string.IsNullOrEmpty(rel))
            return Path.GetFullPath(_root);

        if (rel.Split('/').Any(p => p == ".." || p == "."))
            throw new UnauthorizedAccessException("Path invalido");

        var full = Path.GetFullPath(Path.Combine(_root, rel));
        var rootFull = Path.GetFullPath(_root);
        if (!full.StartsWith(rootFull, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Path fuera del directorio permitido");

        return full;
    }

    public string ToRelative(string absolutePath)
    {
        var rootFull = Path.GetFullPath(_root);
        var full = Path.GetFullPath(absolutePath);
        var rel = Path.GetRelativePath(rootFull, full).Replace('\\', '/');
        return rel == "." ? "" : rel;
    }

    public static string SanitizeName(string name)
    {
        var cleaned = new string(name.Where(c => !Path.GetInvalidFileNameChars().Contains(c) && c != '/' && c != '\\').ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(cleaned)) throw new ArgumentException("Nombre invalido");
        if (cleaned == "." || cleaned == "..") throw new ArgumentException("Nombre invalido");
        return cleaned;
    }
}
