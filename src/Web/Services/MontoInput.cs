using System.Globalization;

namespace Web.Services;

/// <summary>
/// Campos de plata escritos a mano (2026-08-18).
///
/// Historia: los importes editables eran &lt;input type="number"&gt;, que NO puede mostrar
/// separador de miles — el usuario veia "386370,00" al lado de un saldo que decia
/// "$392.040,00" y tenia que contar los ceros. Ademas el input numerico del browser
/// se peleaba con el locale es-AR (bug 2026-06-17: truncaba el monto al perder foco,
/// por eso se forzaba InvariantCulture en el value).
///
/// Solucion: input de texto, mostrado SIEMPRE formateado con la cultura del navegador
/// (es-AR = "392.040,00"), y parseo tolerante al volver.
/// </summary>
public static class MontoInput
{
    /// <summary>Como se muestra el monto en el input: igual que los importes de al lado.</summary>
    public static string Formato(decimal v) => v.ToString("N2");

    /// <summary>
    /// Interpreta lo que tipeo el usuario. Acepta lo que se ve en pantalla
    /// ("392.040,00"), lo que sale de copiar y pegar ("$ 392.040,00"), un numero pelado
    /// ("392040") y tambien el formato con punto decimal ("392040.00").
    /// </summary>
    public static bool TryParse(string? raw, out decimal valor)
    {
        valor = 0m;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var s = raw.Trim().Replace("$", "").Replace(" ", "").Replace(" ", "");
        if (s.Length == 0) return false;

        // Primero, tal como se muestra en pantalla (cultura del navegador).
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.CurrentCulture, out valor)) return true;

        // Fallback: formato con punto decimal ("392040.00"), por si el navegador
        // esta en ingles o el texto vino pegado de otro lado.
        if (decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out valor)) return true;

        valor = 0m;
        return false;
    }
}
