using Web.Models;

namespace Web.Services;

/// <summary>
/// 2026-08-21: buscador de clientes COMPARTIDO por todas las pantallas (ventas, cobranzas,
/// alquileres, listas, extracto, cheques, saldos, máquinas, visitas, mapeo).
///
/// El problema que resuelve: antes cada pantalla filtraba con un "contiene" y mostraba los
/// primeros N ORDENADOS ALFABÉTICAMENTE. Con 9.000+ clientes eso escondía al que buscabas:
/// tipear "sergio" daba 48 coincidencias y 15 iban antes alfabéticamente que "SERGIO FERNANDEZ",
/// así que el cliente NO aparecía nunca y parecía que no existía.
///
/// Ahora:
///  1. Ordena por RELEVANCIA: primero los que empiezan con lo tipeado, después los que lo tienen
///     al principio de alguna palabra, y al final los que lo tienen en el medio.
///  2. Busca por PALABRAS SUELTAS en cualquier orden: "fernandez sergio" encuentra "SERGIO FERNANDEZ".
///  3. Las pantallas muestran el tope con <see cref="LeyendaTope"/> para que nunca parezca
///     "no existe" cuando en realidad quedó cortado.
///
/// OJO PERFORMANCE: esto corre en cada tecla sobre la lista completa de clientes que ya tiene
/// la pantalla en memoria. Por eso NO normaliza acentos (ver <see cref="SearchExtensions"/>:
/// hacerlo trababa la UI con 9.000 clientes) y corta apenas una palabra no aparece.
/// </summary>
public static class ClienteSearch
{
    /// <summary>Corte duro cuando lo tipeado es muy general (ej. una sola letra) y coinciden cientos:
    /// ahí no tiene sentido dibujar la lista entera. Antes cada pantalla ponía lo suyo (8, 10, 12, 15, 20).</summary>
    public const int Tope = 50;

    /// <summary>Hasta acá mostramos TODAS las coincidencias (la lista scrollea). Es el caso de un
    /// apellido común: "fernandez" da 52 clientes y ningún orden por relevancia puede adivinar cuál
    /// de los 52 querías — pero si están todos, lo encontrás scrolleando.</summary>
    public const int MostrarTodoHasta = 120;

    /// <summary>Lo que realmente se dibuja en el desplegable: todo si son pocos, o el tope si son
    /// muchísimos (ahí la pantalla muestra <see cref="LeyendaTope"/> avisando cuántos quedaron afuera).</summary>
    public static List<CafeClienteDto> Recortar(List<CafeClienteDto> ordenados)
        => ordenados.Count <= MostrarTodoHasta ? ordenados : ordenados.Take(Tope).ToList();

    /// <summary>Filtra + ordena por relevancia. Devuelve TODOS los que coinciden (la pantalla hace
    /// el .Take(Tope) y compara con .Count para avisar si hay más).</summary>
    public static List<CafeClienteDto> Buscar(IEnumerable<CafeClienteDto>? clientes, string? query, bool soloActivos = true)
    {
        if (clientes is null) return new();
        var baseList = soloActivos ? clientes.Where(c => c.IsActive) : clientes;

        var frase = (query ?? "").Trim();
        if (frase.Length == 0)
            return baseList.OrderBy(c => c.Nombre, StringComparer.OrdinalIgnoreCase).ToList();

        var palabras = Palabras(frase);
        int.TryParse(frase, out var num);

        return baseList
            .Where(c => Coincide(c, palabras))
            .OrderByDescending(c => Relevancia(c, frase, palabras, num))
            .ThenBy(c => c.Nombre, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Ordena por relevancia una lista YA filtrada por la pantalla (para las que tienen
    /// su propio filtro con campos raros y solo les falta el orden).</summary>
    public static IEnumerable<CafeClienteDto> OrdenarPorRelevancia(this IEnumerable<CafeClienteDto> matches, string? query)
    {
        var frase = (query ?? "").Trim();
        if (frase.Length == 0) return matches.OrderBy(c => c.Nombre, StringComparer.OrdinalIgnoreCase);
        var palabras = Palabras(frase);
        int.TryParse(frase, out var num);
        return matches
            .OrderByDescending(c => Relevancia(c, frase, palabras, num))
            .ThenBy(c => c.Nombre, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Cartelito para cuando la lista quedó cortada por el tope.</summary>
    public static string LeyendaTope(int total, int mostrados)
        => $"Mostrando {mostrados} de {total} clientes que coinciden — seguí escribiendo (nombre + apellido, código o CUIT) para achicar la lista.";

    // ── internos ──────────────────────────────────────────────────────────────

    private static string[] Palabras(string frase)
        => frase.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>True si TODAS las palabras tipeadas aparecen en algún campo del cliente
    /// (sin importar el orden ni en qué campo caiga cada una).</summary>
    private static bool Coincide(CafeClienteDto c, string[] palabras)
    {
        foreach (var p in palabras)
            if (!AlgunCampo(c, p)) return false;
        return true;
    }

    private static bool AlgunCampo(CafeClienteDto c, string t)
        => Tiene(c.Nombre, t)
        || Tiene(c.RazonSocial, t)
        || Tiene(c.Codigo, t)
        || Tiene(c.Cuit, t)
        || Tiene(c.Telefono, t)
        || Tiene(c.Telefono2, t)
        || Tiene(c.Email, t)
        || Tiene(c.Direccion, t)
        || Tiene(c.DomicilioEntrega, t)
        || Tiene(c.EntreCalles, t)
        || Tiene(c.Localidad, t)
        || Tiene(c.Ciudad, t)
        || Tiene(c.Cp, t)
        || (c.CodigoInterno.HasValue && c.CodigoInterno.Value.ToString().Contains(t, StringComparison.Ordinal));

    private static bool Tiene(string? s, string t)
        => !string.IsNullOrEmpty(s) && s.Contains(t, StringComparison.OrdinalIgnoreCase);

    /// <summary>Puntaje: cuanto más alto, más arriba sale. El nombre pesa más que la razón social
    /// y que el código; un código interno EXACTO se lleva el primer puesto.</summary>
    private static int Relevancia(CafeClienteDto c, string frase, string[] palabras, int num)
    {
        var score = Puntaje(c.Nombre, frase, palabras) * 4
                  + Puntaje(c.RazonSocial, frase, palabras) * 2
                  + Puntaje(c.Codigo, frase, palabras);
        if (num > 0 && c.CodigoInterno == num) score += 100;
        return score;
    }

    /// <summary>4 = el texto EMPIEZA con lo tipeado · 3 = alguna palabra del texto empieza con lo
    /// tipeado · 2 = cada palabra tipeada arranca alguna palabra del texto (en cualquier orden)
    /// · 1 = lo tiene en el medio · 0 = no aparece.</summary>
    private static int Puntaje(string? texto, string frase, string[] palabras)
    {
        if (string.IsNullOrEmpty(texto)) return 0;
        if (texto.StartsWith(frase, StringComparison.OrdinalIgnoreCase)) return 4;
        if (EmpiezaPalabra(texto, frase)) return 3;

        bool todasArrancanPalabra = true, todasAparecen = true;
        foreach (var p in palabras)
        {
            if (!EmpiezaPalabra(texto, p)) todasArrancanPalabra = false;
            if (texto.IndexOf(p, StringComparison.OrdinalIgnoreCase) < 0) { todasAparecen = false; break; }
        }
        if (todasArrancanPalabra) return 2;
        return todasAparecen ? 1 : 0;
    }

    /// <summary>True si <paramref name="term"/> arranca alguna palabra de <paramref name="texto"/>
    /// ("fernandez" arranca la 2ª palabra de "SERGIO FERNANDEZ").</summary>
    private static bool EmpiezaPalabra(string texto, string term)
    {
        if (term.Length == 0) return false;
        var i = 0;
        while (i <= texto.Length - term.Length)
        {
            var p = texto.IndexOf(term, i, StringComparison.OrdinalIgnoreCase);
            if (p < 0) return false;
            if (p == 0 || !char.IsLetterOrDigit(texto[p - 1])) return true;
            i = p + 1;
        }
        return false;
    }
}
