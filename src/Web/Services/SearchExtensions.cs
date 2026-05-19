using System.Globalization;
using System.Text;

namespace Web.Services;

/// <summary>
/// Helpers para que las busquedas en filtros del frontend ignoren acentos,
/// mayusculas/minusculas y caracteres no relevantes. Asi escribir "cafe" matchea
/// "Café", "CAFÉ", "cafè", etc.
///
/// Pedido del usuario 2026-05-20: 'me cuesta encontrar productos / clientes con
/// acentos, hay manera de normalizar eso, asi busco "cafe" y sino pongo el
/// acento me lo encuentra facil?'.
///
/// Uso tipico:
///   list.Where(x => x.Nombre.MatchesSearch(query))
/// o:
///   x.Nombre.ContainsSearch("cafe")
/// </summary>
public static class SearchExtensions
{
    /// <summary>Devuelve una clave canonica para busqueda:
    ///   - Lowercase (invariant)
    ///   - Sin acentos (Café → cafe, niño → nino)
    ///   - Sin caracteres de control (nbsp, etc)
    /// Null/vacio → string vacio.</summary>
    public static string ToSearchKey(this string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        // FormD descompone los chars con acento en char base + acento.
        // Despues filtramos los acentos (NonSpacingMark) para quedarnos con la base.
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c);
        }
        return sb.ToString().ToLowerInvariant().Trim();
    }

    /// <summary>True si <paramref name="text"/> contiene <paramref name="query"/> ignorando
    /// acentos y case. Si query es null/vacio, true (no hay filtro).</summary>
    public static bool MatchesSearch(this string? text, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        return text.ToSearchKey().Contains(query.ToSearchKey());
    }

    /// <summary>Alias semantico de MatchesSearch. Para que sea natural de leer:
    ///   x.Nombre.ContainsSearch(q)  ←→  x.Nombre matchea con q (ignorando acentos)</summary>
    public static bool ContainsSearch(this string? text, string? query) => MatchesSearch(text, query);
}
