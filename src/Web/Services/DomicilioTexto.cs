using System.Text.RegularExpressions;

namespace Web.Services;

/// <summary>
/// 2026-09-12 — QUÉ DIRECCIÓN SE MUESTRA, en un solo lugar.
///
/// El problema que resuelve (lo encontró Osmar el 12/09/2026 mirando el chat de CAFE VENTANA):
/// un cliente tiene DOS domicilios y son datos distintos.
///
///   · <c>Direccion</c> / <c>Localidad</c>          → el FISCAL, el que va en la factura (MURILLO 1221)
///   · <c>DomicilioEntrega</c> / <c>LocalidadEntrega</c> → el de ENTREGA, a donde va el reparto (QUIRNO 94)
///
/// El <c>MapeoLink</c> (el pin de Google Maps) apunta en la práctica al de ENTREGA. Pero las
/// pantallas del chat escribían el FISCAL. Resultado: la barra de arriba del chat decía
/// "MURILLO 1221" y al tocarla se abría QUIRNO 94. Textual: *"es como una contradicción"*.
///
/// La regla es una sola y vale para todas las pantallas donde lo que importa es a dónde se LLEVA
/// (el chat, el reparto, el modo venta): **si hay domicilio de entrega, mandá ese; si no, el fiscal**.
/// Es la misma regla que ya usaba la carga de venta normal (`CafeVentas.razor`), que es la pantalla
/// que él marcó como "ésta me gusta más".
///
/// ⚠ NO usar esto para el COMPROBANTE: ahí va el fiscal, siempre. Esto es para mostrar en pantalla.
/// </summary>
public static class DomicilioTexto
{
    /// <summary>
    /// Etiquetas vacías que vienen pegadas adentro del propio dato, no las arma el sistema:
    /// "MURILLO 1221 Piso:- Dpto:- S:- T:- M:-". Son restos de una importación vieja y ensucian
    /// cada renglón donde se muestra la dirección.
    ///
    /// Sólo se borra la etiqueta cuando el valor es un guión (o está vacía y queda al final):
    /// "Piso:-" se va, "Piso:3" y "Piso: 3" se quedan como están. Esto NO toca la base: limpia
    /// al mostrar. Vaciarlo de verdad es otra tarea, y se consulta antes.
    /// </summary>
    static readonly Regex RxEtiquetaGuion = new(@"\s*\b[\p{L}]{1,12}\.?\s*:\s*-+(?=$|[\s,;|])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Una etiqueta suelta sin nada atrás, al final del texto ("... Dpto:").</summary>
    static readonly Regex RxEtiquetaSola = new(@"\s*\b[\p{L}]{1,12}\.?\s*:\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static readonly Regex RxEspacios = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>Saca las etiquetas vacías y los espacios de más. Devuelve "" si no queda nada.</summary>
    public static string Limpiar(string? texto)
    {
        var t = (texto ?? "").Trim();
        if (t.Length == 0) return "";
        t = RxEtiquetaGuion.Replace(t, "");
        t = RxEtiquetaSola.Replace(t, "");
        t = RxEspacios.Replace(t, " ").Trim();
        return t.Trim(' ', ',', ';', '|', '-');
    }

    /// <summary>
    /// La dirección que hay que MOSTRAR: la de entrega si existe, si no la fiscal.
    /// Devuelve también la localidad que le corresponde a esa dirección (la de entrega puede estar
    /// en otra localidad que la fiscal: por eso existe <c>LocalidadEntrega</c>).
    /// </summary>
    public static (string Direccion, string Localidad, bool EsEntrega) Elegir(
        string? direccionFiscal, string? localidadFiscal,
        string? domicilioEntrega, string? localidadEntrega)
    {
        var ent = Limpiar(domicilioEntrega);
        if (ent.Length > 0)
        {
            // Si el domicilio de entrega no trae localidad propia, cae en la fiscal: es mejor eso
            // que dejarlo sin localidad (sin localidad el mapa no encuentra el punto).
            var locEnt = Limpiar(localidadEntrega);
            if (locEnt.Length == 0) locEnt = Limpiar(localidadFiscal);
            return (ent, locEnt, true);
        }
        return (Limpiar(direccionFiscal), Limpiar(localidadFiscal), false);
    }

    /// <summary>Un renglón listo para mostrar: "QUIRNO 94, CIUDAD AUTONOMA DE BUENOS AIRES".
    /// Devuelve null si el cliente no tiene ninguna dirección cargada (así la pantalla no
    /// dibuja un renglón vacío).</summary>
    public static string? Renglon(
        string? direccionFiscal, string? localidadFiscal,
        string? domicilioEntrega, string? localidadEntrega)
    {
        var (dir, loc, _) = Elegir(direccionFiscal, localidadFiscal, domicilioEntrega, localidadEntrega);
        if (dir.Length == 0 && loc.Length == 0) return null;
        if (dir.Length == 0) return loc;
        if (loc.Length == 0) return dir;
        return $"{dir}, {loc}";
    }

    /// <summary>Junta los datos de abajo del nombre con el separador " · ", salteando los vacíos.
    /// Se usa en los buscadores de cliente: "CIUDAD AUTONOMA DE BUENOS AIRES · 1414".</summary>
    public static string Puntos(params string?[] partes)
        => string.Join(" · ", partes.Select(Limpiar).Where(x => x.Length > 0));
}
