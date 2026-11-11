namespace Web.Services;

/// <summary>
/// 10/11/2026: arma el aviso amarillo "Osmar cambió este pedido" de Preparación (compu y celu)
/// a partir de los renglones del historial de la venta. Deja solo lo que le importa al que arma
/// (productos, cantidades, comentarios, dirección…) y saca plata, comprobante, IVA y Maps.
/// </summary>
public static class CambiosArmado
{
    public enum Tipo { Agrego, Saco, Otro }
    public record Linea(Tipo Tipo, string Texto);

    private static readonly string[] CamposQueNoVan =
    {
        "fecha:", "comprobante:", "condición de IVA:", "condición de pago:", "descuento:", "total:",
    };

    public static List<Linea> Filtrar(IEnumerable<string>? detalles)
    {
        var res = new List<Linea>();
        if (detalles is null) return res;
        foreach (var d in detalles)
            foreach (var crudo in d.Split('\n'))
            {
                var l = crudo.Trim();
                if (l.Length == 0) continue;
                if (l.Contains("Maps", StringComparison.OrdinalIgnoreCase)) continue;
                if (CamposQueNoVan.Any(c => l.StartsWith(c, StringComparison.OrdinalIgnoreCase))) continue;
                if (l.Contains(": precio ", StringComparison.Ordinal) || l.Contains(": descuento ", StringComparison.Ordinal)) continue;

                if (l.StartsWith("agregó ", StringComparison.Ordinal)) res.Add(new(Tipo.Agrego, "+ " + l["agregó ".Length..]));
                else if (l.StartsWith("sacó ", StringComparison.Ordinal)) res.Add(new(Tipo.Saco, "− " + l["sacó ".Length..]));
                else res.Add(new(Tipo.Otro, char.ToUpper(l[0]) + l[1..]));
            }
        return res.DistinctBy(x => x.Texto).ToList();
    }

    /// <summary>"OSMAR" → "Osmar".</summary>
    public static string Nombre(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "Alguien";
        s = s.Trim();
        return string.Join(" ", s.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Length == 1 ? p.ToUpper() : char.ToUpper(p[0]) + p[1..].ToLower()));
    }

    public static string HaceCuanto(DateTime? utc)
    {
        if (utc is null) return "";
        var min = (int)Math.Floor((DateTime.UtcNow - utc.Value).TotalMinutes);
        return min < 1 ? "recién" : min == 1 ? "hace 1 min" : min < 60 ? $"hace {min} min" : $"hace {min / 60} h";
    }
}
