using System.Globalization;

namespace Api.Services;

// Convierte numeros decimales a palabras en espanol.
// Soporta hasta cifras de millones. "Pesos" + "centavos".
public static class NumberToWordsEs
{
    private static readonly string[] Units = {
        "", "UNO", "DOS", "TRES", "CUATRO", "CINCO", "SEIS", "SIETE", "OCHO", "NUEVE",
        "DIEZ", "ONCE", "DOCE", "TRECE", "CATORCE", "QUINCE", "DIECISEIS", "DIECISIETE", "DIECIOCHO", "DIECINUEVE",
        "VEINTE", "VEINTIUNO", "VEINTIDOS", "VEINTITRES", "VEINTICUATRO", "VEINTICINCO",
        "VEINTISEIS", "VEINTISIETE", "VEINTIOCHO", "VEINTINUEVE"
    };
    private static readonly string[] Tens = {
        "", "", "", "TREINTA", "CUARENTA", "CINCUENTA", "SESENTA", "SETENTA", "OCHENTA", "NOVENTA"
    };
    private static readonly string[] Hundreds = {
        "", "CIENTO", "DOSCIENTOS", "TRESCIENTOS", "CUATROCIENTOS", "QUINIENTOS",
        "SEISCIENTOS", "SETECIENTOS", "OCHOCIENTOS", "NOVECIENTOS"
    };

    public static string AmountToPesos(decimal amount)
    {
        var integer = (long)Math.Floor(Math.Abs(amount));
        var cents = (int)Math.Round((Math.Abs(amount) - integer) * 100m);
        if (cents == 100) { integer++; cents = 0; }

        var words = ConvertInteger(integer);
        return $"Son Pesos {words} con {cents:00}/100.";
    }

    private static string ConvertInteger(long n)
    {
        if (n == 0) return "CERO";
        if (n < 0) return "MENOS " + ConvertInteger(-n);

        var parts = new List<string>();
        long millones = n / 1_000_000;
        long resto = n % 1_000_000;
        long miles = resto / 1000;
        long unidades = resto % 1000;

        if (millones > 0)
        {
            if (millones == 1) parts.Add("UN MILLON");
            else parts.Add(ConvertHundreds(millones) + " MILLONES");
        }
        if (miles > 0)
        {
            if (miles == 1) parts.Add("MIL");
            else parts.Add(ConvertHundreds(miles) + " MIL");
        }
        if (unidades > 0)
        {
            parts.Add(ConvertHundreds(unidades));
        }

        return string.Join(" ", parts).Trim();
    }

    private static string ConvertHundreds(long n)
    {
        if (n == 0) return "";
        if (n == 100) return "CIEN";
        var hundreds = n / 100;
        var rest = n % 100;
        var sb = new System.Text.StringBuilder();
        if (hundreds > 0) sb.Append(Hundreds[hundreds]);
        if (rest > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (rest < 30) sb.Append(Units[rest]);
            else
            {
                var tens = rest / 10;
                var u = rest % 10;
                sb.Append(Tens[tens]);
                if (u > 0) sb.Append(" Y ").Append(Units[u]);
            }
        }
        return sb.ToString();
    }
}
