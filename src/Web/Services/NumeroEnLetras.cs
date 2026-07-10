using System.Text;

namespace Web.Services;

/// <summary>2026-07-09: convierte un monto en pesos a su forma escrita en español.
/// Usado para mostrar el total "en letras" en la pantalla Valor de stock (/stock/valuacion)
/// y en la card "Mercadería valorizada" del dashboard. Ignora los centavos (redondea al piso).</summary>
public static class NumeroEnLetras
{
    private static readonly string[] UNI = {
        "", "un","dos","tres","cuatro","cinco","seis","siete","ocho","nueve",
        "diez","once","doce","trece","catorce","quince","dieciséis","diecisiete","dieciocho","diecinueve",
        "veinte","veintiún","veintidós","veintitrés","veinticuatro","veinticinco","veintiséis","veintisiete","veintiocho","veintinueve"};
    private static readonly string[] DEC = { "","","","treinta","cuarenta","cincuenta","sesenta","setenta","ochenta","noventa" };
    private static readonly string[] CEN = { "","ciento","doscientos","trescientos","cuatrocientos","quinientos","seiscientos","setecientos","ochocientos","novecientos" };

    private static string Grupo(int n) // 0..999
    {
        if (n == 0) return "";
        if (n == 100) return "cien";
        var sb = new StringBuilder();
        int c = n / 100, r = n % 100;
        if (c > 0) sb.Append(CEN[c]);
        if (r > 0)
        {
            if (sb.Length > 0) sb.Append(' ');
            if (r < 30) sb.Append(UNI[r]);
            else { sb.Append(DEC[r / 10]); if (r % 10 > 0) sb.Append(" y ").Append(UNI[r % 10]); }
        }
        return sb.ToString();
    }

    private static string Miles(long x) // 0..999999
    {
        if (x == 0) return "";
        long miles = x / 1000, u = x % 1000;
        var sb = new StringBuilder();
        if (miles > 0) sb.Append(miles == 1 ? "mil" : Grupo((int)miles) + " mil");
        if (u > 0) { if (sb.Length > 0) sb.Append(' '); sb.Append(Grupo((int)u)); }
        return sb.ToString();
    }

    /// <summary>Devuelve el monto escrito, ej: "Doscientos veintiséis millones ... pesos".</summary>
    public static string PesosEnLetras(decimal monto)
    {
        long n = (long)Math.Floor(monto);
        if (n <= 0) return "cero pesos";
        long millon = n / 1_000_000, resto = n % 1_000_000;
        var sb = new StringBuilder();
        if (millon > 0) sb.Append(millon == 1 ? "un millón" : Miles(millon) + " millones");
        if (resto > 0) { if (sb.Length > 0) sb.Append(' '); sb.Append(Miles(resto)); }
        // "de pesos" solo cuando el monto termina justo en millón (ej: dos millones de pesos)
        sb.Append(millon > 0 && resto == 0 ? " de pesos" : " pesos");
        var s = sb.ToString();
        return char.ToUpper(s[0]) + s.Substring(1);
    }
}
