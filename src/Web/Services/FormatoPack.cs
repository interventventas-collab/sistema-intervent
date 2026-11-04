using Web.Models;

namespace Web.Services;

/// <summary>
/// 2026-11-04: formato de venta de los packs prearmados. "PACK_10" = 10 unidades; "PACK_10E" = 10
/// unidades armadas en ESTUCHE (cápsulas). Pueden convivir dos packs de la misma cantidad, uno con
/// y otro sin estuche. Mismo criterio que la API (CafePricingService.ParsePackUnidades).
/// </summary>
public static class FormatoPack
{
    public const string Prefijo = "PACK_";
    public const string SufijoEstuche = "E";

    /// <summary>Unidades del pack ("PACK_10" o "PACK_10E" → 10). null si no es un pack.</summary>
    public static int? Unidades(string? formato)
    {
        if (string.IsNullOrEmpty(formato) || !formato.StartsWith(Prefijo)) return null;
        var rest = formato.AsSpan(Prefijo.Length);
        if (rest.EndsWith(SufijoEstuche)) rest = rest[..^SufijoEstuche.Length];
        return int.TryParse(rest, out var n) && n > 0 ? n : null;
    }

    public static bool ConEstuche(string? formato) => Unidades(formato).HasValue && formato!.EndsWith(SufijoEstuche);

    public static string De(int cantidad, bool conEstuche) => Prefijo + cantidad + (conEstuche ? SufijoEstuche : "");

    public static string De(CafeProductoPackDto pk) => De(pk.Cantidad, pk.ConEstuche);

    /// <summary>El pack activo que corresponde al formato (cantidad + con/sin estuche).</summary>
    public static CafeProductoPackDto? Buscar(IEnumerable<CafeProductoPackDto>? packs, string? formato)
    {
        var n = Unidades(formato);
        if (!n.HasValue || packs is null) return null;
        var est = ConEstuche(formato);
        return packs.FirstOrDefault(p => p.IsActive && p.Cantidad == n.Value && p.ConEstuche == est);
    }

    /// <summary>"Pack x 10" / "Pack x 10 con estuche".</summary>
    public static string Etiqueta(string formato)
        => $"Pack x {Unidades(formato)}" + (ConEstuche(formato) ? " con estuche" : "");
}
