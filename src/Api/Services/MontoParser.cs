using System.Globalization;

namespace Api.Services;

/// <summary>
/// Lectura de importes que vienen como TEXTO desde afuera (Excel del banco, extracto,
/// mensajes de WhatsApp). 2026-08-18.
///
/// El problema: cada importador tenia su propia receta y todas suponian un formato fijo.
/// - "borrar todos los puntos y cambiar la coma por punto" rompe un archivo con punto
///   decimal: "1234.56" terminaba valiendo 123.456 (cien veces mas).
/// - "parsear con InvariantCulture" rompe un archivo argentino: "386.370,00" no parsea
///   (queda 0) y "386.370" se lee 386,37 (mil veces menos).
///
/// Este parser NO supone: mira los separadores que trae el texto y decide.
/// Regla: el ULTIMO separador que aparece es el decimal, salvo que se comporte como
/// separador de miles (grupos de exactamente 3 digitos), en cuyo caso no hay decimales.
/// </summary>
public static class MontoParser
{
    public static decimal? Parse(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto)) return null;

        var s = texto.Trim()
            .Replace("$", "").Replace("ARS", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "").Replace(" ", "").Replace("'", "");
        if (s.Length == 0) return null;

        var negativo = s.StartsWith('-') || (s.StartsWith('(') && s.EndsWith(')'));
        s = s.Trim('-', '(', ')');
        if (s.Length == 0) return null;

        var ultimoPunto = s.LastIndexOf('.');
        var ultimaComa = s.LastIndexOf(',');
        string limpio;

        if (ultimoPunto >= 0 && ultimaComa >= 0)
        {
            // Vienen los dos: el que esta mas a la derecha es el decimal.
            var decSep = ultimoPunto > ultimaComa ? '.' : ',';
            var milSep = decSep == '.' ? ',' : '.';
            limpio = s.Replace(milSep.ToString(), "").Replace(decSep, '.');
        }
        else if (ultimoPunto >= 0 || ultimaComa >= 0)
        {
            var sep = ultimoPunto >= 0 ? '.' : ',';
            var pos = ultimoPunto >= 0 ? ultimoPunto : ultimaComa;
            var veces = s.Count(c => c == sep);
            var digitosDespues = s.Length - pos - 1;

            // Con un solo separador hay que desempatar:
            // - La COMA en Argentina es el decimal ("12,50" = doce con cincuenta), salvo que
            //   aparezca varias veces ("1,234,567" = formato ingles).
            // - El PUNTO con exactamente 3 digitos atras es miles ("386.370" = trescientos
            //   ochenta y seis mil), que es como lo escriben los bancos; con otra cantidad de
            //   digitos es decimal ("1234.56").
            var esMiles = veces > 1 || (sep == '.' && digitosDespues == 3);
            limpio = esMiles ? s.Replace(sep.ToString(), "") : s.Replace(sep, '.');
        }
        else
        {
            limpio = s;
        }

        if (!decimal.TryParse(limpio, NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            return null;
        return negativo ? -v : v;
    }

    /// <summary>Igual que <see cref="Parse"/> pero devuelve 0 cuando no se entiende el texto.</summary>
    public static decimal ParseOrZero(string? texto) => Parse(texto) ?? 0m;
}
