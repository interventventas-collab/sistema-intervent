namespace Api.Services;

/// <summary>
/// 2026-09-24: ubicación de los productos en el depósito. Se arranca por PLANTA (lo que más tiempo
/// hace perder es subir a buscar algo que estaba abajo) y una ZONA corta opcional. El texto que se
/// muestra en todos lados sale de acá, así se lee igual en el armado, en el celu y en MeLi Depósito.
/// Cuando haya pasillo y estante se suman acá sin tocar lo ya cargado.
/// </summary>
public static class UbicacionHelper
{
    public static readonly (string Codigo, string Nombre)[] Plantas =
    {
        ("PB", "Planta baja"),
        ("P1", "Primer piso"),
        ("OTRO", "Otro depósito"),
    };

    public static bool PlantaValida(string? planta) => Plantas.Any(p => p.Codigo == planta);

    public static string NombrePlanta(string? planta) =>
        Plantas.FirstOrDefault(p => p.Codigo == planta).Nombre ?? planta ?? "";

    /// <summary>Zona en mayúsculas, sin espacios de más. Vacía → null.</summary>
    public static string? NormalizarZona(string? zona)
    {
        if (string.IsNullOrWhiteSpace(zona)) return null;
        var z = string.Join(' ', zona.Trim().ToUpperInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return z.Length > 30 ? z[..30] : z;
    }

    /// <summary>"PB · TOST", "P1", o null si no tiene planta.</summary>
    public static string? Texto(string? planta, string? zona)
    {
        if (string.IsNullOrEmpty(planta)) return null;
        return string.IsNullOrEmpty(zona) ? planta : $"{planta} · {zona}";
    }
}
