using System.Text.Json;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/files")]
[Authorize]
public class FilesController : ControllerBase
{
    private readonly FileStorageService _storage;
    private readonly AuditLogService _audit;
    private readonly AppDbContext _db;

    public FilesController(FileStorageService storage, AuditLogService audit, AppDbContext db)
    {
        _storage = storage;
        _audit = audit;
        _db = db;
    }

    private string? CurrentUser => User?.Identity?.Name;

    public record FileEntryDto(string Name, string Path, bool IsFolder, long Size, DateTime ModifiedAt,
        string? Color = null, string? IconEmoji = null);
    public record ListResponse(string Path, string Provider, List<FileEntryDto> Entries);

    [HttpGet("list")]
    public async Task<IActionResult> List([FromQuery] string? path)
    {
        Response.Headers["Cache-Control"] = "no-store";
        string full;
        try { full = _storage.ResolveSafe(path); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Path invalido" }); }

        if (!Directory.Exists(full))
            return NotFound(new { error = "Carpeta no encontrada" });

        var entries = new List<FileEntryDto>();
        var rawEntries = new List<(string name, string relPath, bool isFolder, long size, DateTime modified)>();
        foreach (var dir in Directory.EnumerateDirectories(full))
        {
            var info = new DirectoryInfo(dir);
            rawEntries.Add((info.Name, _storage.ToRelative(dir), true, 0, info.LastWriteTime));
        }
        foreach (var file in Directory.EnumerateFiles(full))
        {
            var info = new FileInfo(file);
            rawEntries.Add((info.Name, _storage.ToRelative(file), false, info.Length, info.LastWriteTime));
        }

        // Cargar metadata (color + icono) de todos los paths de un saque.
        var paths = rawEntries.Select(e => e.relPath).ToList();
        var metas = await _db.FileMetadata.Where(m => paths.Contains(m.Path)).ToDictionaryAsync(m => m.Path);

        foreach (var e in rawEntries)
        {
            metas.TryGetValue(e.relPath, out var meta);
            entries.Add(new FileEntryDto(e.name, e.relPath, e.isFolder, e.size, e.modified, meta?.Color, meta?.IconEmoji));
        }
        var provider = await _storage.GetProviderAsync();
        return Ok(new ListResponse(_storage.ToRelative(full), provider, entries));
    }

    public class SetMetaRequest
    {
        public string Path { get; set; } = "";
        public string? Color { get; set; }
        public string? IconEmoji { get; set; }
    }

    /// <summary>Setea color y/o emoji custom para un archivo o carpeta. Si Color/IconEmoji es null o "",
    /// limpia ese campo. Si ambos quedan vacios y existia un registro, lo borra.</summary>
    [HttpPost("meta")]
    public async Task<IActionResult> SetMeta([FromBody] SetMetaRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Path)) return BadRequest(new { error = "path requerido" });
        // Validar que el path realmente existe en el storage
        string full;
        try { full = _storage.ResolveSafe(req.Path); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Path invalido" }); }
        if (!Directory.Exists(full) && !System.IO.File.Exists(full))
            return NotFound(new { error = "No existe" });

        var color = string.IsNullOrWhiteSpace(req.Color) ? null : req.Color.Trim().ToLowerInvariant();
        var icon = string.IsNullOrWhiteSpace(req.IconEmoji) ? null : req.IconEmoji.Trim();
        var meta = await _db.FileMetadata.FirstOrDefaultAsync(m => m.Path == req.Path);
        if (meta is null)
        {
            if (color is null && icon is null) return Ok(new { ok = true });
            meta = new FileMetadata { Path = req.Path, Color = color, IconEmoji = icon, UpdatedAt = DateTime.UtcNow };
            _db.FileMetadata.Add(meta);
        }
        else
        {
            meta.Color = color;
            meta.IconEmoji = icon;
            meta.UpdatedAt = DateTime.UtcNow;
            if (color is null && icon is null) _db.FileMetadata.Remove(meta);
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, color, iconEmoji = icon });
    }

    public record StatsResponse(int Folders, int Files, long TotalBytes, DateTime? LastUploadAt);

    /// <summary>Stats recursivos de una carpeta — usado por la card del Dashboard.</summary>
    [HttpGet("stats")]
    public IActionResult Stats([FromQuery] string? path)
    {
        Response.Headers["Cache-Control"] = "no-store";
        string full;
        try { full = _storage.ResolveSafe(path); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Path invalido" }); }
        if (!Directory.Exists(full)) return Ok(new StatsResponse(0, 0, 0, null));

        int folders = 0, files = 0;
        long bytes = 0;
        DateTime? last = null;
        try
        {
            foreach (var d in Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories)) folders++;
            foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                files++;
                var fi = new FileInfo(f);
                bytes += fi.Length;
                if (!last.HasValue || fi.LastWriteTime > last.Value) last = fi.LastWriteTime;
            }
        }
        catch { /* ignorar errores parciales por permisos */ }
        return Ok(new StatsResponse(folders, files, bytes, last));
    }

    public record ProviderDto(string Provider, List<ProviderOption> Options);
    public record ProviderOption(string Value, string Label, bool Enabled);

    [HttpGet("provider")]
    public async Task<IActionResult> GetProvider()
    {
        var provider = await _storage.GetProviderAsync();
        var options = new List<ProviderOption>
        {
            new("local", "Servidor local", true),
            new("azure-blob", "Azure Blob Storage", false),
            new("onedrive", "OneDrive", false),
            new("gdrive", "Google Drive", false),
            new("s3", "Amazon S3", false)
        };
        return Ok(new ProviderDto(provider, options));
    }

    public class ProviderRequest { public string Provider { get; set; } = ""; }

    [HttpPost("provider")]
    public async Task<IActionResult> SetProvider([FromBody] ProviderRequest req)
    {
        if (req.Provider != "local")
            return BadRequest(new { error = "Solo 'local' esta disponible por ahora" });
        await _storage.SetProviderAsync(req.Provider);
        await _audit.LogAsync("Files", "provider", "FILES_PROVIDER_SET", req.Provider, CurrentUser);
        return Ok(new { ok = true });
    }

    public class CreateFolderRequest { public string Path { get; set; } = ""; public string Name { get; set; } = ""; }

    [HttpPost("folder")]
    public async Task<IActionResult> CreateFolder([FromBody] CreateFolderRequest req)
    {
        string name;
        try { name = FileStorageService.SanitizeName(req.Name); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        string parent;
        try { parent = _storage.ResolveSafe(req.Path); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Path invalido" }); }

        if (!Directory.Exists(parent)) return NotFound(new { error = "Carpeta padre no existe" });

        var full = Path.Combine(parent, name);
        if (Directory.Exists(full) || System.IO.File.Exists(full))
            return BadRequest(new { error = "Ya existe una carpeta o archivo con ese nombre" });

        Directory.CreateDirectory(full);
        await _audit.LogAsync("Files", _storage.ToRelative(full), "FILES_FOLDER_CREATE", null, CurrentUser);
        return Ok(new { ok = true, path = _storage.ToRelative(full) });
    }

    [HttpPost("upload")]
    [RequestSizeLimit(2L * 1024 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = 2L * 1024 * 1024 * 1024)]
    public async Task<IActionResult> Upload([FromQuery] string? path, [FromForm] List<IFormFile> files)
    {
        string target;
        try { target = _storage.ResolveSafe(path); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Path invalido" }); }

        if (!Directory.Exists(target)) return NotFound(new { error = "Carpeta destino no existe" });
        if (files is null || files.Count == 0) return BadRequest(new { error = "No se envio ningun archivo" });

        var results = new List<object>();
        foreach (var f in files)
        {
            if (f.Length == 0) continue;
            // Soportar subdirectorios (webkitRelativePath) desde el form
            var raw = string.IsNullOrEmpty(f.FileName) ? "" : f.FileName.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(raw) || raw.Split('/').Any(p => p == ".." || p == "."))
            {
                results.Add(new { name = f.FileName, success = false, error = "Nombre invalido" });
                continue;
            }
            try
            {
                var destAbs = Path.GetFullPath(Path.Combine(target, raw));
                var targetFull = Path.GetFullPath(target);
                if (!destAbs.StartsWith(targetFull, StringComparison.Ordinal))
                {
                    results.Add(new { name = raw, success = false, error = "Path invalido" });
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destAbs)!);
                await using var fs = System.IO.File.Create(destAbs);
                await f.CopyToAsync(fs);
                results.Add(new { name = raw, success = true, size = f.Length, path = _storage.ToRelative(destAbs) });
                await _audit.LogAsync("Files", _storage.ToRelative(destAbs), "FILES_UPLOAD",
                    JsonSerializer.Serialize(new { size = f.Length }), CurrentUser);
            }
            catch (Exception ex)
            {
                results.Add(new { name = raw, success = false, error = ex.Message });
            }
        }
        return Ok(results);
    }

    public class DeleteRequest { public List<string> Paths { get; set; } = new(); }

    [HttpPost("delete")]
    public async Task<IActionResult> Delete([FromBody] DeleteRequest req)
    {
        if (req is null || req.Paths is null || req.Paths.Count == 0)
            return BadRequest(new { error = "No se envio ningun path" });

        var results = new List<object>();
        foreach (var p in req.Paths)
        {
            try
            {
                var full = _storage.ResolveSafe(p);
                if (full == Path.GetFullPath(_storage.Root))
                {
                    results.Add(new { path = p, success = false, error = "No se puede eliminar la raiz" });
                    continue;
                }
                if (Directory.Exists(full))
                {
                    Directory.Delete(full, true);
                    // limpiar metadata de la carpeta + todos sus descendientes
                    var prefix = p + "/";
                    var metasDel = await _db.FileMetadata.Where(m => m.Path == p || m.Path.StartsWith(prefix)).ToListAsync();
                    if (metasDel.Count > 0) { _db.FileMetadata.RemoveRange(metasDel); await _db.SaveChangesAsync(); }
                    await _audit.LogAsync("Files", p, "FILES_FOLDER_DELETE", null, CurrentUser);
                    results.Add(new { path = p, success = true });
                }
                else if (System.IO.File.Exists(full))
                {
                    System.IO.File.Delete(full);
                    var metaDel = await _db.FileMetadata.FirstOrDefaultAsync(m => m.Path == p);
                    if (metaDel is not null) { _db.FileMetadata.Remove(metaDel); await _db.SaveChangesAsync(); }
                    await _audit.LogAsync("Files", p, "FILES_DELETE", null, CurrentUser);
                    results.Add(new { path = p, success = true });
                }
                else
                {
                    results.Add(new { path = p, success = false, error = "No existe" });
                }
            }
            catch (Exception ex)
            {
                results.Add(new { path = p, success = false, error = ex.Message });
            }
        }
        return Ok(results);
    }

    public class RenameRequest { public string Path { get; set; } = ""; public string NewName { get; set; } = ""; }

    [HttpPost("rename")]
    public async Task<IActionResult> Rename([FromBody] RenameRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Path)) return BadRequest(new { error = "path requerido" });
        string name;
        try { name = FileStorageService.SanitizeName(req.NewName); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }

        string full;
        try { full = _storage.ResolveSafe(req.Path); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Path invalido" }); }

        var parent = Path.GetDirectoryName(full) ?? _storage.Root;
        var dest = Path.Combine(parent, name);
        if (string.Equals(full, dest, StringComparison.Ordinal))
            return Ok(new { ok = true, path = _storage.ToRelative(dest) });
        if (Directory.Exists(dest) || System.IO.File.Exists(dest))
            return BadRequest(new { error = "Ya existe un archivo o carpeta con ese nombre" });

        var oldRel = req.Path;
        var newRel = _storage.ToRelative(dest);
        if (Directory.Exists(full)) Directory.Move(full, dest);
        else if (System.IO.File.Exists(full)) System.IO.File.Move(full, dest);
        else return NotFound(new { error = "No existe" });

        // Mantener la metadata sincronizada con el nuevo path. Si es carpeta, tambien
        // actualizamos todos los archivos/subcarpetas que tengan metadata adentro.
        var oldPrefix = oldRel + "/";
        var newPrefix = newRel + "/";
        var metas = await _db.FileMetadata.Where(m => m.Path == oldRel || m.Path.StartsWith(oldPrefix)).ToListAsync();
        foreach (var m in metas)
        {
            if (m.Path == oldRel) m.Path = newRel;
            else if (m.Path.StartsWith(oldPrefix)) m.Path = newPrefix + m.Path.Substring(oldPrefix.Length);
            m.UpdatedAt = DateTime.UtcNow;
        }
        if (metas.Count > 0) await _db.SaveChangesAsync();

        await _audit.LogAsync("Files", req.Path, "FILES_RENAME",
            JsonSerializer.Serialize(new { from = req.Path, to = newRel }), CurrentUser);
        return Ok(new { ok = true, path = newRel });
    }

    public class MoveRequest { public List<string> Paths { get; set; } = new(); public string TargetPath { get; set; } = ""; }

    [HttpPost("move")]
    public async Task<IActionResult> Move([FromBody] MoveRequest req)
    {
        if (req is null || req.Paths is null || req.Paths.Count == 0)
            return BadRequest(new { error = "No se envio ningun path" });

        string targetFull;
        try { targetFull = _storage.ResolveSafe(req.TargetPath); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Target invalido" }); }
        if (!Directory.Exists(targetFull)) return BadRequest(new { error = "La carpeta destino no existe" });

        var results = new List<object>();
        foreach (var p in req.Paths)
        {
            try
            {
                var full = _storage.ResolveSafe(p);
                var name = Path.GetFileName(full);
                var dest = Path.Combine(targetFull, name);
                if (string.Equals(full, dest, StringComparison.Ordinal))
                {
                    results.Add(new { path = p, success = false, error = "Origen y destino son iguales" });
                    continue;
                }
                // Evitar mover una carpeta dentro de si misma
                if (Directory.Exists(full) &&
                    (targetFull.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                     string.Equals(targetFull, full, StringComparison.Ordinal)))
                {
                    results.Add(new { path = p, success = false, error = "No se puede mover una carpeta dentro de si misma" });
                    continue;
                }
                if (Directory.Exists(dest) || System.IO.File.Exists(dest))
                {
                    results.Add(new { path = p, success = false, error = "Ya existe en destino" });
                    continue;
                }
                if (Directory.Exists(full)) Directory.Move(full, dest);
                else if (System.IO.File.Exists(full)) System.IO.File.Move(full, dest);
                else { results.Add(new { path = p, success = false, error = "No existe" }); continue; }

                await _audit.LogAsync("Files", p, "FILES_MOVE",
                    JsonSerializer.Serialize(new { from = p, to = _storage.ToRelative(dest) }), CurrentUser);
                results.Add(new { path = p, success = true, newPath = _storage.ToRelative(dest) });
            }
            catch (Exception ex)
            {
                results.Add(new { path = p, success = false, error = ex.Message });
            }
        }
        return Ok(results);
    }

    [HttpGet("download")]
    public IActionResult Download([FromQuery] string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return BadRequest(new { error = "path requerido" });
        string full;
        try { full = _storage.ResolveSafe(path); }
        catch (UnauthorizedAccessException) { return BadRequest(new { error = "Path invalido" }); }

        if (!System.IO.File.Exists(full)) return NotFound();
        var name = Path.GetFileName(full);
        var stream = System.IO.File.OpenRead(full);
        // Inferir Content-Type por extension. Sin esto las imagenes no se renderizan via <img src>
        // en algunos browsers (rechazan application/octet-stream).
        var contentType = MimeFromExtension(Path.GetExtension(name));
        return File(stream, contentType, name);
    }

    private static string MimeFromExtension(string? ext) => (ext?.ToLowerInvariant()) switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".bmp" => "image/bmp",
        ".ico" => "image/x-icon",
        ".pdf" => "application/pdf",
        ".csv" => "text/csv",
        ".txt" => "text/plain",
        ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ".xls" => "application/vnd.ms-excel",
        ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".doc" => "application/msword",
        ".json" => "application/json",
        _ => "application/octet-stream"
    };
}
