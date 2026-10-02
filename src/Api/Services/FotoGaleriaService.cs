using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-10-02 — Galería de fotos chroma: lo que comparten el sistema (CafeProductoFotoController, con
/// usuario y clave) y el LINK público del celu (FotosLinkPublicController, sin login).
///
/// El link es para SACAR FOTOS y nada más (pedido del dueño: "el celu saca fotos a full y fue"). Sacar el
/// fondo lo hace el servidor al subir; retocar, ponerla en un producto, en una publicación de MeLi o
/// descargarla queda SOLO dentro del sistema: el link lo puede abrir cualquiera que lo tenga.
/// </summary>
public static class FotoGaleriaService
{
    // Mismo destino que las fotos propias (volume files_data, persiste a los rebuilds).
    public const string FotosDir = "/data/files/producto-fotos";
    public const long MaxBytes = 25L * 1024 * 1024;

    /// <summary>Token del link público del celu (AppSettings). Si se cambia, el link viejo deja de andar.</summary>
    public const string LinkSettingKey = "fotos.link.token";

    private static string? Corto(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];

    /// <summary>Procesa la original de la fila con sus opciones y reemplaza la foto procesada.</summary>
    public static async Task ProcesarAsync(CafeFotoGaleria g)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(FotosDir, g.OriginalArchivo));
        var r = FotoChromaService.Procesar(bytes, new FotoChromaService.Opciones(g.Color, g.Tolerancia, g.Suavizado));
        var viejo = g.Archivo;
        if (r.Jpeg is not null)
        {
            var nuevo = $"gal-{Guid.NewGuid():N}.jpg";
            await File.WriteAllBytesAsync(Path.Combine(FotosDir, nuevo), r.Jpeg);
            g.Archivo = nuevo;
        }
        else g.Archivo = null;
        g.ColorUsado = r.ColorUsado;
        g.Aviso = Corto(r.Aviso, 600);
        g.Error = Corto(r.Error, 300);
        g.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrEmpty(viejo) && viejo != g.Archivo)
        {
            try { var f = Path.Combine(FotosDir, viejo); if (File.Exists(f)) File.Delete(f); }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Guarda la original, la procesa y crea la fila. Devuelve la fila o el motivo del rechazo.</summary>
    public static async Task<(CafeFotoGaleria? Foto, string? Error)> SubirAsync(AppDbContext db, IFormFile? file,
        FotoChromaService.Opciones op, string? usuario)
    {
        if (file is null || file.Length == 0) return (null, "No se recibió ninguna foto.");
        if (file.Length > MaxBytes) return (null, "La foto es muy grande (máx 25 MB).");
        if (!file.ContentType.StartsWith("image/")) return (null, "El archivo tiene que ser una imagen.");

        Directory.CreateDirectory(FotosDir);
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(ext) || ext.Length > 6 || ext.Any(c => !char.IsLetterOrDigit(c) && c != '.')) ext = ".jpg";
        var original = $"gal-orig-{Guid.NewGuid():N}{ext}";
        await using (var fs = File.Create(Path.Combine(FotosDir, original)))
            await file.CopyToAsync(fs);

        var g = new CafeFotoGaleria
        {
            OriginalArchivo = original,
            Color = op.Color ?? "auto", Tolerancia = op.Tolerancia, Suavizado = op.Suavizado,
            Usuario = Corto(usuario, 100),
            CreatedAt = DateTime.UtcNow,
        };
        await ProcesarAsync(g);
        db.CafeFotoGaleria.Add(g);
        await db.SaveChangesAsync();
        return (g, null);
    }

    /// <summary>Token actual del link del celu; si no hay y <paramref name="crear"/>, lo genera.</summary>
    public static async Task<string?> LinkTokenAsync(AppDbContext db, bool crear)
    {
        var s = await db.AppSettings.FirstOrDefaultAsync(a => a.Key == LinkSettingKey);
        if (!string.IsNullOrEmpty(s?.Value) || !crear) return s?.Value;
        return await CambiarLinkAsync(db);
    }

    /// <summary>Genera un token nuevo: el link anterior deja de funcionar.</summary>
    public static async Task<string> CambiarLinkAsync(AppDbContext db)
    {
        var token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var s = await db.AppSettings.FirstOrDefaultAsync(a => a.Key == LinkSettingKey);
        if (s is null) db.AppSettings.Add(new AppSetting { Key = LinkSettingKey, Value = token, UpdatedAt = DateTime.UtcNow });
        else { s.Value = token; s.UpdatedAt = DateTime.UtcNow; }
        await db.SaveChangesAsync();
        return token;
    }

    /// <summary>¿El token que vino en el link es el vigente? (comparación de tiempo constante)</summary>
    public static async Task<bool> LinkValidoAsync(AppDbContext db, string? token)
    {
        var actual = await LinkTokenAsync(db, crear: false);
        if (string.IsNullOrEmpty(actual) || string.IsNullOrEmpty(token) || token.Length != actual.Length) return false;
        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(token), System.Text.Encoding.ASCII.GetBytes(actual));
    }
}
