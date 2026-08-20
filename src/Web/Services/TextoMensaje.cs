namespace Web.Services;

/// <summary>
/// 2026-08-20: recorte de los mensajes LARGOS en el chat, como hace WhatsApp con el "Leer más".
///
/// El problema: los mensajes automáticos del sistema (el listado de deudores, el resumen de
/// fichadas del día) salen con decenas de renglones. Al mostrarse enteros, UN mensaje tapaba
/// toda la pantalla del celular y había que scrollear diez veces para pasarlo y llegar a la
/// respuesta del cliente, que es lo que uno estaba buscando.
///
/// Se corta por lo que pase primero: demasiadas letras o demasiados renglones. Lo segundo importa
/// porque un listado de 30 renglones cortitos suma pocas letras pero igual se come la pantalla.
/// </summary>
public static class TextoMensaje
{
    /// <summary>Pasado esto, el mensaje se recorta. WhatsApp corta cerca de este número.</summary>
    public const int TopeCaracteres = 600;

    /// <summary>Y también se recorta si tiene más renglones que esto, aunque sean cortitos.</summary>
    public const int TopeRenglones = 14;

    /// <summary>¿Este mensaje hay que recortarlo?</summary>
    public static bool EsLargo(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return false;
        if (texto.Length > TopeCaracteres) return true;
        return ContarRenglones(texto) > TopeRenglones;
    }

    /// <summary>
    /// El pedacito que se muestra cuando está cerrado. Corta en el corte "lindo" más cercano
    /// (fin de renglón, y si no hay, fin de palabra) para no partir una palabra al medio ni
    /// dejar media cifra colgada, que en un listado de plata se lee como un error.
    /// </summary>
    public static string Recortado(string? texto)
    {
        if (string.IsNullOrEmpty(texto)) return "";
        var t = texto;

        // 1) tope de renglones
        if (ContarRenglones(t) > TopeRenglones)
        {
            var renglones = t.Replace("\r\n", "\n").Split('\n');
            t = string.Join("\n", renglones.Take(TopeRenglones));
        }

        // 2) tope de letras
        if (t.Length > TopeCaracteres)
        {
            var corte = t.Substring(0, TopeCaracteres);
            var finRenglon = corte.LastIndexOf('\n');
            var finPalabra = corte.LastIndexOf(' ');
            // solo usamos el corte lindo si no nos deja con menos de la mitad del texto
            if (finRenglon > TopeCaracteres / 2) corte = corte.Substring(0, finRenglon);
            else if (finPalabra > TopeCaracteres / 2) corte = corte.Substring(0, finPalabra);
            t = corte;
        }

        return t.TrimEnd() + "…";
    }

    private static int ContarRenglones(string texto)
    {
        int n = 1;
        foreach (var c in texto) if (c == '\n') n++;
        return n;
    }
}
