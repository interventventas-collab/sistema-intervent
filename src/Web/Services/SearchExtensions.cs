namespace Web.Services;

/// <summary>
/// Busqueda en filtros del frontend ignorando acentos + case + ñ.
/// 'cafe' matchea 'Café', 'CAFÉ', 'cafè'. 'nino' matchea 'niño'.
///
/// Implementacion optimizada (2026-05-20): se reescribio para evitar las
/// allocations que ralentizaban la app con listas grandes (9000+ clientes).
/// Antes usaba Unicode Normalize(FormD) + filter de NonSpacingMark que crea
/// 4 strings por llamada. Ahora hace un solo pass char-por-char con un
/// switch de mapeo manual (los chars con acento del castellano son pocos
/// y conocidos). Stack-allocates el buffer cuando es chico (<=256 chars).
/// </summary>
public static class SearchExtensions
{
    /// <summary>Clave canonica para busqueda: lowercase + sin acentos. Allocation-light.</summary>
    public static string ToSearchKey(this string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var span = s.AsSpan().Trim();
        if (span.Length == 0) return "";

        // Fast path: si es todo ASCII lowercase no hay nada que hacer.
        bool needsWork = false;
        foreach (var c in span)
        {
            if (c > 'z' || c < ' ' || (c >= 'A' && c <= 'Z'))
            {
                needsWork = true;
                break;
            }
        }
        if (!needsWork) return new string(span);

        // Slow path: char-by-char mapping. Stack alloc para no presionar al GC.
        Span<char> buf = span.Length <= 256 ? stackalloc char[span.Length] : new char[span.Length];
        for (int i = 0; i < span.Length; i++)
            buf[i] = StripAccent(span[i]);
        return new string(buf);
    }

    /// <summary>True si <paramref name="text"/> contiene <paramref name="query"/> ignorando
    /// acentos y case. Si query es null/vacio, true (no hay filtro).</summary>
    public static bool MatchesSearch(this string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var q = query.ToSearchKey();
        if (q.Length == 0) return true;
        if (string.IsNullOrEmpty(text)) return false;
        return text.ToSearchKey().Contains(q, System.StringComparison.Ordinal);
    }

    /// <summary>Alias semantico de MatchesSearch.</summary>
    public static bool ContainsSearch(this string? text, string? query) => MatchesSearch(text, query);

    /// <summary>Map char-por-char a su equivalente sin acento y en lowercase. Cubre los
    /// chars que aparecen en datos en castellano (vocales con acentos + ñ + ç + ü).
    /// Es un switch que el JIT compila como tabla de saltos — mucho mas rapido que
    /// Unicode Normalize().</summary>
    private static char StripAccent(char c) => c switch
    {
        // Vocales acentuadas — mayusculas y minusculas → base lowercase
        'á' or 'à' or 'ä' or 'â' or 'ã' or 'å' or 'Á' or 'À' or 'Ä' or 'Â' or 'Ã' or 'Å' => 'a',
        'é' or 'è' or 'ë' or 'ê' or 'É' or 'È' or 'Ë' or 'Ê' => 'e',
        'í' or 'ì' or 'ï' or 'î' or 'Í' or 'Ì' or 'Ï' or 'Î' => 'i',
        'ó' or 'ò' or 'ö' or 'ô' or 'õ' or 'Ó' or 'Ò' or 'Ö' or 'Ô' or 'Õ' => 'o',
        'ú' or 'ù' or 'ü' or 'û' or 'Ú' or 'Ù' or 'Ü' or 'Û' => 'u',
        // ñ → n
        'ñ' or 'Ñ' => 'n',
        // ç → c (frances/portugues, puede aparecer en apellidos)
        'ç' or 'Ç' => 'c',
        // ASCII mayuscula → lowercase
        >= 'A' and <= 'Z' => (char)(c + 32),
        // Todo lo demas tal cual
        _ => c
    };
}
