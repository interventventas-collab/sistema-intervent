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

    /// <summary>
    /// 2026-08-20: minusculas Y SIN ACENTOS, para que "martin" encuentre "Martín" y "cafe"
    /// encuentre "Café".
    ///
    /// Esto es lo que arriba se saco a proposito de MatchesSearch: con 9000 clientes habia que
    /// normalizar ~144.000 textos por cada tecla y la pantalla se trababa. Aca NO pasa: se usa
    /// para buscar DENTRO de una conversacion, que como mucho tiene los 200 mensajes que la
    /// pantalla ya trajo. Por eso va como metodo aparte y NO se toca MatchesSearch: si alguien
    /// lo usa en una lista larga, que sea una decision consciente.
    /// </summary>
    public static string ToSearchKeySinTildes(this string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var t = s.Trim();
        var sb = new System.Text.StringBuilder(t.Length);
        foreach (var ch in t)
        {
            var c = char.ToLowerInvariant(ch);
            sb.Append(c switch
            {
                'á' or 'à' or 'ä' or 'â' or 'ã' => 'a',
                'é' or 'è' or 'ë' or 'ê' => 'e',
                'í' or 'ì' or 'ï' or 'î' => 'i',
                'ó' or 'ò' or 'ö' or 'ô' or 'õ' => 'o',
                'ú' or 'ù' or 'ü' or 'û' => 'u',
                'ñ' => 'n',
                'ç' => 'c',
                _ => c
            });
        }
        return sb.ToString();
    }
}
