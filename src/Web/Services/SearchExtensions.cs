namespace Web.Services;

/// <summary>
/// Busqueda en filtros del frontend. Solo case-insensitive (sin tocar acentos).
///
/// Historia: 2026-05-19 tuvimos una version que ignoraba acentos (cafe == Café)
/// usando Unicode Normalize o un mapeo char-por-char. Pero con 9000+ clientes,
/// ejecutar esa normalizacion ~144k veces por tecla lageaba la UI. El usuario
/// pidio sacarla. Ahora 'cafe' NO matchea 'Café' — el usuario tipea el acento
/// si lo necesita. Si en el futuro queremos volver, mejor moverlo al backend
/// (SQL Server) o cachear las claves normalizadas al cargar la lista.
/// </summary>
public static class SearchExtensions
{
    /// <summary>Clave canonica para busqueda: lowercase.</summary>
    public static string ToSearchKey(this string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        return s.Trim().ToLowerInvariant();
    }

    /// <summary>True si <paramref name="text"/> contiene <paramref name="query"/> ignorando case.
    /// Si query es null/vacio, true (no hay filtro).</summary>
    public static bool MatchesSearch(this string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        if (string.IsNullOrEmpty(text)) return false;
        return text.Contains(query, System.StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Alias semantico de MatchesSearch.</summary>
    public static bool ContainsSearch(this string? text, string? query) => MatchesSearch(text, query);
}
