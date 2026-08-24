using System.Globalization;

namespace Web.Services;

/// <summary>
/// Campos de plata escritos a mano (2026-08-18).
///
/// Historia: los importes editables eran &lt;input type="number"&gt;, que NO puede mostrar
/// separador de miles — el usuario veia "386370,00" al lado de un saldo que decia
/// "$392.040,00" y tenia que contar los ceros. Peor: cuando el value se renderizaba con
/// la cultura del navegador ("392040,00"), el browser DESCARTA ese valor y el casillero
/// queda VACIO (medido con Playwright el 18/08/2026), que es el bug 2026-06-17.
///
/// Solucion: input de texto, mostrado SIEMPRE formateado con la cultura del navegador
/// (es-AR = "392.040,00"), y parseo que MIRA los separadores en vez de suponerlos.
/// Mismas reglas que Api/Services/MontoParser.cs (que lee los importes de los archivos
/// del banco); si se cambia una, cambiar la otra.
/// </summary>
public static class MontoInput
{
    /// <summary>Como se muestra el monto en el input: igual que los importes de al lado.</summary>
    public static string Formato(decimal v) => v.ToString("N2");

    /// <summary>
    /// 2026-08-24: para CANTIDADES (kg de cafe, horas, dias), no plata. Mismo parseo que los
    /// importes (asi "0,5" no vacia el casillero) pero sin los dos decimales de relleno:
    /// 22 se ve "22" y no "22,00"; 7,5 se ve "7,5". Separador de miles para las cantidades grandes.
    /// </summary>
    public static string FormatoCantidad(decimal v) => v.ToString("#,0.##");

    /// <summary>
    /// Interpreta lo que tipeo el usuario. Acepta lo que se ve en pantalla ("392.040,00"),
    /// lo que sale de copiar y pegar ("$ 392.040,00"), un numero pelado ("392040"),
    /// y tambien el formato con punto decimal ("1234.56").
    /// </summary>
    public static bool TryParse(string? raw, out decimal valor)
    {
        valor = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim().Replace("$", "").Replace(" ", "").Replace(" ", "").Replace("'", "");
        if (s.Length == 0) return false;

        var negativo = s.StartsWith('-') || (s.StartsWith('(') && s.EndsWith(')'));
        s = s.Trim('-', '(', ')');
        if (s.Length == 0) return false;

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

            // La COMA en Argentina es el decimal ("12,50"), salvo que se repita ("1,234,567").
            // El PUNTO con exactamente 3 digitos atras es miles ("386.370" = trescientos ochenta
            // y seis mil); con otra cantidad de digitos es decimal ("1234.56").
            var esMiles = veces > 1 || (sep == '.' && digitosDespues == 3);
            limpio = esMiles ? s.Replace(sep.ToString(), "") : s.Replace(sep, '.');
        }
        else
        {
            limpio = s;
        }

        if (!decimal.TryParse(limpio, NumberStyles.Any, CultureInfo.InvariantCulture, out valor))
        {
            valor = 0m;
            return false;
        }
        if (negativo) valor = -valor;
        return true;
    }
}
