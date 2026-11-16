namespace Web.Services;

/// <summary>2026-11-16: fechas en palabras para los pagos al equipo: "hoy", "mañana", "este sábado 21/11",
/// "el lunes 23/11", "venció vie 13/11 (hace 3 días)". Pedido del dueño: "que diga siempre la fecha
/// que hay que pagarle, por ejemplo mañana o este sábado".</summary>
public static class FechaPalabras
{
    private static readonly System.Globalization.CultureInfo EsAr = new("es-AR");
    private static readonly string[] Dias = { "domingo", "lunes", "martes", "miércoles", "jueves", "viernes", "sábado" };

    public static DateTime HoyAr => DateTime.UtcNow.ToArTime().Date;

    private static DateTime Lunes(DateTime d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    /// <summary>Para una fecha que viene (o es hoy). Si ya pasó, devuelve el texto de vencido.</summary>
    public static string Cuando(DateTime fecha)
    {
        var hoy = HoyAr; var f = fecha.Date;
        var d = (f - hoy).Days;
        if (d < 0) return Vencio(f);
        if (d == 0) return "hoy";
        if (d == 1) return $"mañana {Dias[(int)f.DayOfWeek].Substring(0, 3)} {f:dd/MM}";
        if (d <= 6) return (Lunes(f) == Lunes(hoy) ? "este " : "el ") + $"{Dias[(int)f.DayOfWeek]} {f:dd/MM}";
        return f.ToString("ddd dd/MM", EsAr);
    }

    public static string Vencio(DateTime fecha)
    {
        var d = (HoyAr - fecha.Date).Days;
        return $"venció {fecha.ToString("ddd dd/MM", EsAr)} ({(d == 1 ? "ayer" : $"hace {d} días")})";
    }

    public static bool Atrasado(DateTime fecha) => fecha.Date < HoyAr;

    public static string Corta(DateTime fecha) => fecha.ToString("ddd dd/MM", EsAr);
}
