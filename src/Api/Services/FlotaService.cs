using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Api.Services;

/// <summary>
/// 20/11/2026: los vehículos con los que se reparte. Los de Shell Flota salen solos de las cargas
/// (con los km de la última carga); a eso se suman los autos propios de cada repartidor (Siena de
/// Nacho) y la foto de cada uno. El repartidor elige con cuál sale en /mis-pedidos.
/// </summary>
public class FlotaService
{
    private readonly AppDbContext _db;
    private readonly ShellCargasService _shell;
    public FlotaService(AppDbContext db, ShellCargasService shell) { _db = db; _shell = shell; }

    public record Coche(string Placa, string Nombre, int? Km, DateTime? UltimaCarga, bool Propio,
        int? RepartidorId, string? Repartidor, long? FotoVer, DateTime? KmAt = null);
    public record CocheElegido(string? Placa, string Nombre, string? Comentario, int? Km, long? FotoVer, DateTime ElegidoAt);

    public static DateTime HoyAr => DateTime.UtcNow.AddHours(-3).Date;

    /// <summary>Los de Shell + los propios. Con repartidorId: solo SUS propios (los de otros no le aparecen).</summary>
    public async Task<List<Coche>> CochesAsync(int? soloPropiosDe, bool todosLosPropios = false)
    {
        var shell = await _shell.FlotaAsync();
        var filas = await _db.FlotaVehiculos.AsNoTracking()
            .Select(f => new { f.Placa, f.Nombre, f.RepartidorId, f.FotoAt, f.KmManual, f.KmManualAt, TieneFoto = f.Foto != null })
            .ToListAsync();
        var porPlaca = filas.ToDictionary(f => f.Placa, StringComparer.OrdinalIgnoreCase);
        var reps = await _db.CafeRepartidores.AsNoTracking().ToDictionaryAsync(r => r.Id, r => r.Nombre);
        long? Ver(string placa) => porPlaca.TryGetValue(placa, out var f) && f.TieneFoto ? f.FotoAt?.Ticks ?? 1 : null;

        var lista = shell.Select(s => new Coche(s.Placa, s.Nombre, s.Km, s.UltimaCarga, false, null, null, Ver(s.Placa))).ToList();
        foreach (var f in filas.Where(f => f.RepartidorId != null).OrderBy(f => f.Nombre))
        {
            if (!todosLosPropios && f.RepartidorId != soloPropiosDe) continue;
            if (lista.Any(c => c.Placa.Equals(f.Placa, StringComparison.OrdinalIgnoreCase))) continue;
            lista.Add(new Coche(f.Placa, f.Nombre ?? f.Placa, f.KmManual, null, true, f.RepartidorId,
                reps.TryGetValue(f.RepartidorId!.Value, out var rn) ? rn : null, Ver(f.Placa), f.KmManualAt));
        }
        // Los propios primero (al que lo tiene le conviene verlo arriba), después los de Shell por última carga.
        return lista.OrderByDescending(c => c.Propio).ThenByDescending(c => c.UltimaCarga).ToList();
    }

    public async Task<CocheElegido?> ElegidoHoyAsync(int repartidorId)
    {
        var hoy = HoyAr;
        var e = await _db.RepCocheDias.AsNoTracking()
            .Where(x => x.RepartidorId == repartidorId && x.FechaAr == hoy)
            .OrderByDescending(x => x.Id).FirstOrDefaultAsync();
        if (e is null) return null;
        Coche? c = null;
        if (e.Placa != null) c = (await CochesAsync(repartidorId)).FirstOrDefault(x => x.Placa == e.Placa);
        return new CocheElegido(e.Placa, e.Nombre, e.Comentario, c?.Km, c?.FotoVer, e.CreatedAt);
    }

    public async Task<(bool ok, string? error)> ElegirAsync(int repartidorId, string? placa, string? comentario)
    {
        comentario = string.IsNullOrWhiteSpace(comentario) ? null : comentario.Trim();
        string nombre;
        if (string.IsNullOrWhiteSpace(placa))
        {
            if (comentario is null) return (false, "Contá con qué vehículo salís.");
            if (comentario.Length > 200) comentario = comentario[..200];
            nombre = "Otro";
            placa = null;
        }
        else
        {
            var c = (await CochesAsync(repartidorId)).FirstOrDefault(x => x.Placa.Equals(placa, StringComparison.OrdinalIgnoreCase));
            if (c is null) return (false, "Ese vehículo no está en la lista.");
            placa = c.Placa; nombre = c.Nombre; comentario = null;
        }
        _db.RepCocheDias.Add(new RepCocheDia
        {
            RepartidorId = repartidorId, FechaAr = HoyAr, Placa = placa, Nombre = nombre, Comentario = comentario
        });
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>Lo que se muestra en la oficina: "Kangoo" / "Otro: camioneta del hermano". Por repartidor, hoy.</summary>
    public async Task<Dictionary<int, string>> CochesHoyAsync()
    {
        var hoy = HoyAr;
        var filas = await _db.RepCocheDias.AsNoTracking().Where(x => x.FechaAr == hoy).ToListAsync();
        return filas.GroupBy(x => x.RepartidorId)
            .ToDictionary(g => g.Key, g =>
            {
                var u = g.OrderByDescending(x => x.Id).First();
                return u.Placa is null ? $"Otro: {u.Comentario}" : u.Nombre;
            });
    }

    public async Task<byte[]?> FotoAsync(string placa)
        => await _db.FlotaVehiculos.AsNoTracking().Where(f => f.Placa == placa).Select(f => f.Foto).FirstOrDefaultAsync();

    /// <summary>Achica la foto a 900 px de lado (las del celu pesan 4-5 MB) y la guarda como JPG.</summary>
    public async Task<(bool ok, string? error)> GuardarFotoAsync(string placa, Stream archivo)
    {
        placa = placa.Trim().ToUpperInvariant();
        byte[] jpg;
        try
        {
            using var codec = SKCodec.Create(archivo);
            if (codec is null) return (false, "No es una foto.");
            using var orig = SKBitmap.Decode(codec);
            using var rot = Enderezar(orig, codec.EncodedOrigin);
            var escala = Math.Min(1.0, 900.0 / Math.Max(rot.Width, rot.Height));
            var w = Math.Max(1, (int)(rot.Width * escala)); var h = Math.Max(1, (int)(rot.Height * escala));
            using var chica = rot.Resize(new SKImageInfo(w, h), SKFilterQuality.High) ?? rot;
            using var img = SKImage.FromBitmap(chica);
            jpg = img.Encode(SKEncodedImageFormat.Jpeg, 82).ToArray();
        }
        catch { return (false, "No se pudo leer la foto."); }

        var f = await _db.FlotaVehiculos.FirstOrDefaultAsync(x => x.Placa == placa);
        if (f is null) { f = new FlotaVehiculo { Placa = placa }; _db.FlotaVehiculos.Add(f); }
        f.Foto = jpg; f.FotoAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (true, null);
    }

    public async Task QuitarFotoAsync(string placa)
    {
        var f = await _db.FlotaVehiculos.FirstOrDefaultAsync(x => x.Placa == placa);
        if (f is null) return;
        if (f.RepartidorId is null) _db.FlotaVehiculos.Remove(f); else { f.Foto = null; f.FotoAt = null; }
        await _db.SaveChangesAsync();
    }

    public async Task<(bool ok, string? error)> GuardarPropioAsync(string placa, string nombre, int repartidorId)
    {
        placa = (placa ?? "").Trim().ToUpperInvariant().Replace(" ", "");
        nombre = (nombre ?? "").Trim();
        if (placa.Length < 5 || placa.Length > 20) return (false, "Poné la patente.");
        if (nombre.Length == 0) return (false, "Poné qué vehículo es (por ejemplo: Siena).");
        if (!await _db.CafeRepartidores.AnyAsync(r => r.Id == repartidorId)) return (false, "Elegí de qué repartidor es.");
        if ((await _shell.FlotaAsync()).Any(s => s.Placa == placa)) return (false, "Esa patente ya es de Shell Flota.");
        var f = await _db.FlotaVehiculos.FirstOrDefaultAsync(x => x.Placa == placa);
        if (f is null) { f = new FlotaVehiculo { Placa = placa }; _db.FlotaVehiculos.Add(f); }
        f.Nombre = nombre.Length > 60 ? nombre[..60] : nombre;
        f.RepartidorId = repartidorId;
        await _db.SaveChangesAsync();
        return (true, null);
    }

    /// <summary>El dueño de un auto propio anota sus km (desde su celu).</summary>
    public async Task<(bool ok, string? error)> KmPropioAsync(int repartidorId, string placa, int km)
    {
        var f = await _db.FlotaVehiculos.FirstOrDefaultAsync(x => x.Placa == placa && x.RepartidorId == repartidorId);
        if (f is null) return (false, "Ese vehículo no es tuyo.");
        if (km < 0 || km > 2_000_000) return (false, "Revisá los km.");
        f.KmManual = km; f.KmManualAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return (true, null);
    }

    public async Task<bool> EsPropioDeAsync(int repartidorId, string placa)
        => await _db.FlotaVehiculos.AnyAsync(x => x.Placa == placa && x.RepartidorId == repartidorId);

    public async Task BorrarPropioAsync(string placa)
    {
        var f = await _db.FlotaVehiculos.FirstOrDefaultAsync(x => x.Placa == placa && x.RepartidorId != null);
        if (f is null) return;
        _db.FlotaVehiculos.Remove(f);
        await _db.SaveChangesAsync();
    }

    private static SKBitmap Enderezar(SKBitmap b, SKEncodedOrigin o)
    {
        if (o == SKEncodedOrigin.TopLeft) return b.Copy();
        var rota90 = o is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom;
        var r = new SKBitmap(rota90 ? b.Height : b.Width, rota90 ? b.Width : b.Height);
        using var c = new SKCanvas(r);
        switch (o)
        {
            case SKEncodedOrigin.BottomRight: c.RotateDegrees(180, b.Width / 2f, b.Height / 2f); break;
            case SKEncodedOrigin.RightTop: c.Translate(r.Width, 0); c.RotateDegrees(90); break;
            case SKEncodedOrigin.LeftBottom: c.Translate(0, r.Height); c.RotateDegrees(270); break;
        }
        c.DrawBitmap(b, 0, 0);
        return r;
    }
}
