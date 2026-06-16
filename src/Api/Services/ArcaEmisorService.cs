using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// CRUD de la ficha de empresa emisora (datos legales que van en el header
/// del PDF: razón social, domicilio, IIBB, inicio de actividades, logo).
/// Una fila por CUIT. Si tenés varios certificados para el mismo CUIT, comparten
/// la misma ficha.
/// </summary>
public class ArcaEmisorService
{
    private readonly AppDbContext _db;
    private readonly FileStorageService _files;
    private readonly ILogger<ArcaEmisorService> _logger;

    private const long MaxLogoBytes = 5L * 1024 * 1024; // 5 MB
    private const string LogosRootFolder = "Logos Empresa";

    public ArcaEmisorService(AppDbContext db, FileStorageService files, ILogger<ArcaEmisorService> logger)
    {
        _db = db;
        _files = files;
        _logger = logger;
    }

    private static ArcaEmisorDto Map(ArcaEmisor e) => new(
        e.Id, e.Cuit, e.RazonSocial, e.CondicionIva, e.Domicilio,
        e.IIBBTipo, e.IIBBNumero, e.InicioActividades, e.LogoPath,
        e.Telefono, e.Telefono2, e.Email, e.Web, e.Web2,
        e.BancoNombre, e.BancoCbu, e.BancoAlias,
        e.CreatedAt, e.UpdatedAt
    );

    private static string? NormalizeCuit(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var digits = new string(raw.Where(char.IsDigit).ToArray());
        return digits.Length == 11 ? digits : null;
    }

    public async Task<List<ArcaEmisorDto>> GetAllAsync()
    {
        var list = await _db.ArcaEmisores.OrderBy(e => e.Cuit).ToListAsync();
        return list.Select(Map).ToList();
    }

    public async Task<ArcaEmisorDto?> GetByCuitAsync(string cuitRaw)
    {
        var cuit = NormalizeCuit(cuitRaw);
        if (cuit is null) return null;
        var entity = await _db.ArcaEmisores.FirstOrDefaultAsync(e => e.Cuit == cuit);
        return entity is null ? null : Map(entity);
    }

    public async Task<ArcaEmisor?> GetEntityByCuitAsync(string cuitRaw)
    {
        var cuit = NormalizeCuit(cuitRaw);
        if (cuit is null) return null;
        return await _db.ArcaEmisores.FirstOrDefaultAsync(e => e.Cuit == cuit);
    }

    public async Task<(bool ok, string? error, ArcaEmisorDto? dto)> UpsertAsync(UpsertArcaEmisorRequest req)
    {
        var cuit = NormalizeCuit(req.Cuit);
        if (cuit is null) return (false, "El CUIT debe tener 11 dígitos", null);

        var entity = await _db.ArcaEmisores.FirstOrDefaultAsync(e => e.Cuit == cuit);
        if (entity is null)
        {
            entity = new ArcaEmisor { Cuit = cuit, CreatedAt = DateTime.UtcNow };
            _db.ArcaEmisores.Add(entity);
        }
        else
        {
            entity.UpdatedAt = DateTime.UtcNow;
        }

        entity.RazonSocial = string.IsNullOrWhiteSpace(req.RazonSocial) ? null : req.RazonSocial.Trim();
        entity.CondicionIva = string.IsNullOrWhiteSpace(req.CondicionIva) ? "Responsable Inscripto" : req.CondicionIva.Trim();
        entity.Domicilio = string.IsNullOrWhiteSpace(req.Domicilio) ? null : req.Domicilio.Trim();
        entity.IIBBTipo = string.IsNullOrWhiteSpace(req.IIBBTipo) ? null : req.IIBBTipo.Trim();
        entity.IIBBNumero = string.IsNullOrWhiteSpace(req.IIBBNumero) ? null : req.IIBBNumero.Trim();
        entity.InicioActividades = req.InicioActividades;

        entity.Telefono = string.IsNullOrWhiteSpace(req.Telefono) ? null : req.Telefono.Trim();
        entity.Telefono2 = string.IsNullOrWhiteSpace(req.Telefono2) ? null : req.Telefono2.Trim();
        entity.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim();
        entity.Web = string.IsNullOrWhiteSpace(req.Web) ? null : req.Web.Trim();
        entity.Web2 = string.IsNullOrWhiteSpace(req.Web2) ? null : req.Web2.Trim();
        entity.BancoNombre = string.IsNullOrWhiteSpace(req.BancoNombre) ? null : req.BancoNombre.Trim();
        entity.BancoCbu = string.IsNullOrWhiteSpace(req.BancoCbu) ? null : req.BancoCbu.Trim();
        entity.BancoAlias = string.IsNullOrWhiteSpace(req.BancoAlias) ? null : req.BancoAlias.Trim();

        await _db.SaveChangesAsync();
        return (true, null, Map(entity));
    }

    public async Task<(bool ok, string? error, ArcaEmisorDto? dto)> UploadLogoAsync(string cuitRaw, byte[] bytes, string originalFileName)
    {
        var cuit = NormalizeCuit(cuitRaw);
        if (cuit is null) return (false, "CUIT inválido", null);

        if (bytes is null || bytes.Length == 0) return (false, "Archivo vacío", null);
        if (bytes.Length > MaxLogoBytes) return (false, "El logo supera el máximo de 5 MB", null);

        var ext = Path.GetExtension(originalFileName).ToLowerInvariant();
        var permitidas = new[] { ".png", ".jpg", ".jpeg", ".webp", ".svg" };
        if (string.IsNullOrEmpty(ext) || !permitidas.Contains(ext))
            return (false, "Formato no soportado. Usá PNG, JPG, JPEG, WEBP o SVG.", null);

        // Path: Logos Empresa/{cuit}/logo{ext}
        var folderRel = $"{LogosRootFolder}/{cuit}";
        var folderAbs = _files.ResolveSafe(folderRel);
        Directory.CreateDirectory(folderAbs);

        // Borrar logo previo (cualquier extensión) — siempre tenemos UNO solo
        foreach (var oldExt in permitidas)
        {
            var oldPath = Path.Combine(folderAbs, $"logo{oldExt}");
            if (File.Exists(oldPath)) { try { File.Delete(oldPath); } catch { } }
        }

        var fileName = $"logo{ext}";
        var fileAbs = Path.Combine(folderAbs, fileName);
        await File.WriteAllBytesAsync(fileAbs, bytes);
        var relPath = $"{folderRel}/{fileName}";

        // Upsert del emisor con el LogoPath
        var entity = await _db.ArcaEmisores.FirstOrDefaultAsync(e => e.Cuit == cuit);
        if (entity is null)
        {
            entity = new ArcaEmisor { Cuit = cuit, LogoPath = relPath, CreatedAt = DateTime.UtcNow };
            _db.ArcaEmisores.Add(entity);
        }
        else
        {
            entity.LogoPath = relPath;
            entity.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return (true, null, Map(entity));
    }

    public async Task<bool> DeleteLogoAsync(string cuitRaw)
    {
        var cuit = NormalizeCuit(cuitRaw);
        if (cuit is null) return false;

        var entity = await _db.ArcaEmisores.FirstOrDefaultAsync(e => e.Cuit == cuit);
        if (entity is null || string.IsNullOrEmpty(entity.LogoPath)) return false;

        try
        {
            var abs = _files.ResolveSafe(entity.LogoPath);
            if (File.Exists(abs)) File.Delete(abs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo borrar el logo {Path}", entity.LogoPath);
        }
        entity.LogoPath = null;
        entity.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Devuelve los bytes del logo en disco para incrustarlos en el PDF, o null
    /// si no hay logo o no se pudo leer.
    /// </summary>
    public byte[]? TryGetLogoBytes(string? relPath)
    {
        if (string.IsNullOrEmpty(relPath)) return null;
        try
        {
            var abs = _files.ResolveSafe(relPath);
            return File.Exists(abs) ? File.ReadAllBytes(abs) : null;
        }
        catch { return null; }
    }
}
