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
}
