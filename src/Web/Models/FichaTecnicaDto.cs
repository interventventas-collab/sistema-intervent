namespace Web.Models;

// 2026-10-30: ficha técnica de un café (Catálogo → Fichas técnicas y recuadro en la ficha del producto).
public class FichaTecnicaDto
{
    public int Id { get; set; }
    public int? CafeProductoId { get; set; }
    public string? ProductoNombre { get; set; }
    public string? ProductoSku { get; set; }
    public string Nombre { get; set; } = "";
    public string Tipo { get; set; } = "ORIGEN"; // ORIGEN | BLEND
    public string? Especie { get; set; }
    public string? Perfil { get; set; }
    public string? Pais { get; set; }
    public string? Region { get; set; }
    public string? Finca { get; set; }
    public string? Altura { get; set; }
    public string? Variedad { get; set; }
    public string? Proceso { get; set; }
    public string? Tueste { get; set; }
    public string? IdealPara { get; set; }
    public string? Puntaje { get; set; }
    public string? Descripcion { get; set; }
    public string? FraganciaAroma { get; set; }
    public string? Sabor { get; set; }
    public string? Retrogusto { get; set; }
    public List<string> Rueda { get; set; } = new();
    public decimal? Cuerpo { get; set; }
    public decimal? Acidez { get; set; }
    public decimal? Dulzor { get; set; }
    public List<FichaComponenteDto> Componentes { get; set; } = new();
    public List<string> NoAplica { get; set; } = new();
    public string? FotoCafeUrl { get; set; }
    public string? FotoEtiquetaUrl { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }

    public bool EsBlend => Tipo == "BLEND";
}

/// <summary>Una parte de un blend, con sus propios datos de origen.</summary>
public class FichaComponenteDto
{
    public string? Pais { get; set; }
    public decimal? Pct { get; set; }
    public string? Region { get; set; }
    public string? Variedad { get; set; }
    public string? Proceso { get; set; }
    public string? Finca { get; set; }
    public string? Altura { get; set; }
}

public class FichaProductoCafeDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Sku { get; set; }
    public int? FichaId { get; set; }
}

/// <summary>
/// Los datos de la ficha y cómo se cargan. La pantalla "Todas juntas" arma sus botones
/// "Trabajar en:" con esta lista, así un dato nuevo se agrega en un solo lugar.
/// Origen = true: en un blend ese dato va por cada parte (no en la ficha general).
/// </summary>
public static class FichaTecnicaCatalogo
{
    public record Campo(string Key, string Label, string Tipo, bool Origen = false, string[]? Opciones = null, string? Ayuda = null);

    public static readonly Campo[] Campos =
    {
        new("fotos", "Fotos", "fotos"),
        new("perfil", "Perfil (sello)", "texto", Ayuda: "Ej: FUNKY, CLÁSICO, INTENSO"),
        new("especie", "Especie", "opciones", Opciones: new[] { "Arábica", "Robusta", "Arábica + Robusta" }),
        new("pais", "País", "texto", Origen: true),
        new("region", "Región", "texto", Origen: true),
        new("finca", "Finca / productor", "texto", Origen: true),
        new("altura", "Altura", "texto", Origen: true, Ayuda: "Ej: 1.600 – 1.900 msnm"),
        new("variedad", "Variedad", "texto", Origen: true),
        new("proceso", "Proceso", "texto", Origen: true, Ayuda: "Ej: Lavado, Natural fermentado 40 h"),
        new("tueste", "Tueste", "opciones", Opciones: new[] { "Claro", "Medio", "Medio-oscuro", "Oscuro" }),
        new("idealPara", "Ideal para", "texto", Ayuda: "Ej: Espresso · Filtro"),
        new("puntaje", "Puntaje SCA", "texto"),
        new("descripcion", "Descripción", "largo"),
        new("fraganciaAroma", "Fragancia y aroma", "texto"),
        new("sabor", "Sabor", "texto"),
        new("retrogusto", "Retrogusto", "texto"),
        new("rueda", "Rueda de sabores", "rueda"),
        new("balance", "Balance", "balance"),
    };

    /// <summary>Familias de la rueda de sabores (mismas keys y colores que el PDF).</summary>
    public static readonly (string Key, string Label, string Color)[] Rueda =
    {
        ("floral", "Floral", "#d63384"),
        ("afrutado", "Afrutado", "#dc2626"),
        ("acido", "Ácido / fermentado", "#d4a017"),
        ("verde", "Verde / vegetal", "#6b7a2a"),
        ("otros", "Otros", "#9ca3af"),
        ("tostado", "Tostado", "#b45309"),
        ("especiado", "Especiado", "#c2410c"),
        ("cacao", "Nueces / cacao", "#5b3a24"),
        ("dulce", "Dulce", "#ea580c"),
    };

    public static string? Get(FichaTecnicaDto f, string key) => key switch
    {
        "perfil" => f.Perfil, "especie" => f.Especie, "pais" => f.Pais, "region" => f.Region,
        "finca" => f.Finca, "altura" => f.Altura, "variedad" => f.Variedad, "proceso" => f.Proceso,
        "tueste" => f.Tueste, "idealPara" => f.IdealPara, "puntaje" => f.Puntaje,
        "descripcion" => f.Descripcion, "fraganciaAroma" => f.FraganciaAroma, "sabor" => f.Sabor,
        "retrogusto" => f.Retrogusto, _ => null
    };

    public static void Set(FichaTecnicaDto f, string key, string? v)
    {
        switch (key)
        {
            case "perfil": f.Perfil = v; break; case "especie": f.Especie = v; break;
            case "pais": f.Pais = v; break; case "region": f.Region = v; break;
            case "finca": f.Finca = v; break; case "altura": f.Altura = v; break;
            case "variedad": f.Variedad = v; break; case "proceso": f.Proceso = v; break;
            case "tueste": f.Tueste = v; break; case "idealPara": f.IdealPara = v; break;
            case "puntaje": f.Puntaje = v; break; case "descripcion": f.Descripcion = v; break;
            case "fraganciaAroma": f.FraganciaAroma = v; break; case "sabor": f.Sabor = v; break;
            case "retrogusto": f.Retrogusto = v; break;
        }
    }

    public static string? GetComp(FichaComponenteDto c, string key) => key switch
    {
        "pais" => c.Pais, "region" => c.Region, "finca" => c.Finca,
        "altura" => c.Altura, "variedad" => c.Variedad, "proceso" => c.Proceso, _ => null
    };

    public static void SetComp(FichaComponenteDto c, string key, string? v)
    {
        switch (key)
        {
            case "pais": c.Pais = v; break; case "region": c.Region = v; break;
            case "finca": c.Finca = v; break; case "altura": c.Altura = v; break;
            case "variedad": c.Variedad = v; break; case "proceso": c.Proceso = v; break;
        }
    }

    /// <summary>¿Le falta este dato? (lo marcado "no aplica" nunca falta).</summary>
    public static bool Falta(FichaTecnicaDto f, string key)
    {
        if (key == "fotos") return string.IsNullOrEmpty(f.FotoCafeUrl) && !f.NoAplica.Contains("fotoCafe")
                                || string.IsNullOrEmpty(f.FotoEtiquetaUrl) && !f.NoAplica.Contains("fotoEtiqueta");
        if (f.NoAplica.Contains(key)) return false;
        var campo = Campos.FirstOrDefault(c => c.Key == key);
        if (campo is null) return false;
        if (campo.Origen && f.EsBlend)
            return f.Componentes.Count == 0 || f.Componentes.Any(c => string.IsNullOrWhiteSpace(GetComp(c, key)));
        return key switch
        {
            "rueda" => f.Rueda.Count == 0,
            "balance" => f.Cuerpo is null || f.Acidez is null || f.Dulzor is null,
            _ => string.IsNullOrWhiteSpace(Get(f, key))
        };
    }

    public static List<string> Faltantes(FichaTecnicaDto f) =>
        Campos.Where(c => Falta(f, c.Key)).Select(c => c.Label).ToList();
}
