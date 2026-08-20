namespace Web.Services;

/// <summary>
/// 2026-06-22: Helpers de fecha/hora para Blazor WASM. Reemplazo de DateTime.ToLocalTime
/// que NO depende de la zona horaria del navegador (el usuario opera desde España un negocio
/// argentino — todas las fechas deben mostrarse SIEMPRE en hora ARG, no en la del browser).
/// </summary>
public static class DateTimeExt
{
    // Argentina no hace cambio de horario desde 2009. Offset fijo UTC-3.
    private static readonly TimeSpan ArOffset = TimeSpan.FromHours(-3);

    /// <summary>Convierte un DateTime UTC a hora Argentina (UTC-3). Reemplazo de ToLocalTime()
    /// que en Blazor WASM usa la zona del navegador.</summary>
    public static DateTime ToArTime(this DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local) return utc; // ya viene local, no tocar
        return DateTime.SpecifyKind(utc.Add(ArOffset), DateTimeKind.Unspecified);
    }

    public static DateTime? ToArTime(this DateTime? utc)
    {
        if (!utc.HasValue) return null;
        return utc.Value.ToArTime();
    }

    public static DateTimeOffset ToArTime(this DateTimeOffset utc)
    {
        return utc.ToOffset(ArOffset);
    }

    public static DateTimeOffset? ToArTime(this DateTimeOffset? utc)
    {
        if (!utc.HasValue) return null;
        return utc.Value.ToArTime();
    }

    private static readonly System.Globalization.CultureInfo EsAr = new("es-AR");

    /// <summary>
    /// 2026-08-20: ¿los dos mensajes son del MISMO día? (en hora argentina, que es lo que se
    /// muestra). Sirve para saber dónde va el cartelito que separa la charla por día.
    /// </summary>
    public static bool MismoDiaAr(DateTime aUtc, DateTime bUtc)
        => aUtc.ToArTime().Date == bUtc.ToArTime().Date;

    /// <summary>
    /// 2026-08-20: texto del cartelito que separa la charla por día, como WhatsApp: "Hoy",
    /// "Ayer", el día de la semana si fue en la última semana, "12 de agosto" dentro del mismo
    /// año y la fecha completa más atrás. Recibe la fecha en UTC (como viene de la base).
    /// </summary>
    public static string EtiquetaDiaAr(DateTime utc)
    {
        var dia = utc.ToArTime().Date;
        var hoy = DateTime.UtcNow.ToArTime().Date;
        if (dia == hoy) return "Hoy";
        if (dia == hoy.AddDays(-1)) return "Ayer";
        if (dia > hoy.AddDays(-7)) return Mayuscula(dia.ToString("dddd", EsAr));
        if (dia.Year == hoy.Year) return Mayuscula(dia.ToString("d 'de' MMMM", EsAr));
        return dia.ToString("d/M/yyyy", EsAr);
    }

    /// <summary>"martes" -> "Martes" (es-AR devuelve los días en minúscula).</summary>
    private static string Mayuscula(string t)
        => string.IsNullOrEmpty(t) ? t : char.ToUpper(t[0]) + t.Substring(1);
}
