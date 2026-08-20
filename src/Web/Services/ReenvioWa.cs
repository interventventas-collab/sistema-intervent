namespace Web.Services;

/// <summary>
/// 2026-08-20: reenviar un mensaje de WhatsApp a otro contacto.
///
/// Vive acá y no adentro de una pantalla porque lo usan LAS DOS: la de la computadora
/// (`Pages/WhatsAppChat.razor`) y la del celular (`Shared/PinnedWaChat.razor`). Si cada una
/// tuviera su copia, el día que se arregle algo en una la otra queda con el error viejo —
/// y acá lo que se manda son precios y comprobantes a clientes.
///
/// Un mensaje puede ser tres cosas distintas y cada una se manda por su camino: una tarjeta de
/// contacto (que por dentro viaja como "CONTACTO_WA:[…]"), una foto/archivo, o texto pelado.
/// </summary>
public static class ReenvioWa
{
    private const string PrefijoContacto = "CONTACTO_WA:";

    /// <summary>¿El cuerpo de este mensaje es en realidad una tarjeta de contacto?</summary>
    public static bool EsMensajeContacto(string? cuerpo)
        => !string.IsNullOrEmpty(cuerpo) && cuerpo.StartsWith(PrefijoContacto, StringComparison.Ordinal);

    /// <summary>Los contactos guardados adentro de un mensaje "CONTACTO_WA:[{n,t}]".</summary>
    public static List<(string Nombre, string Numero)> ParseContactos(string? cuerpo)
    {
        var res = new List<(string, string)>();
        if (!EsMensajeContacto(cuerpo)) return res;
        try
        {
            var json = cuerpo!.Substring(PrefijoContacto.Length);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var n = el.TryGetProperty("n", out var nn) ? nn.GetString() ?? "" : "";
                var t = el.TryGetProperty("t", out var tt) ? tt.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(n)) n = t;
                res.Add((n, t));
            }
        }
        catch { }
        return res;
    }

    /// <summary>
    /// A quién y POR DÓNDE. Ojo: una conversación en este sistema es un número MÁS una línea, así
    /// que el mismo cliente puede ser dos destinos distintos (uno por cada empresa) — y eso es lo
    /// correcto, porque el mensaje le llega de un remitente diferente en cada caso.
    /// </summary>
    /// <param name="Nombre">Cómo se le muestra al usuario.</param>
    /// <param name="Numero">Número del destinatario.</param>
    /// <param name="Linea">Línea NUESTRA por la que sale (phone id). Nunca null si se puede evitar.</param>
    /// <param name="NombreLinea">"FRIKAF by INTERVENT" / "FIJO TRANSRADIO", para mostrarlo.</param>
    /// <param name="Abierto">Si el contacto escribió por ESA línea en las últimas 24 hs.</param>
    /// <param name="EsOtraEmpresa">True si sale por una línea distinta de la que estás trabajando.</param>
    public record Destino(string Nombre, string Numero, string? Linea, string NombreLinea, bool Abierto, bool EsOtraEmpresa);

    /// <summary>Qué pasó al intentar reenviar un mensaje.</summary>
    public enum Resultado
    {
        /// <summary>Salió.</summary>
        Enviado,
        /// <summary>No salió (lo más común: el destinatario no escribió en 24 hs y Meta lo rechaza).</summary>
        Fallo,
        /// <summary>No había nada que reenviar (por ejemplo un mensaje que es solo una reacción).</summary>
        NadaQueMandar
    }

    /// <summary>
    /// Reenvía UN mensaje a UN número, POR UNA LÍNEA CONCRETA.
    ///
    /// 2026-08-20 — POR QUÉ LA LÍNEA ES OBLIGATORIA ACÁ:
    /// hasta hoy el reenvío mandaba la línea en null y el servidor la elegía solo, usando aquella
    /// por la que ese contacto había escrito ÚLTIMO. La idea era no chocar con la regla de las 24 hs
    /// de Meta, pero esa regla no sabe que FRIKAF by INTERVENT y FIJO TRANSRADIO son DOS EMPRESAS
    /// distintas: para los contactos que le escriben a las dos, la línea de salida terminaba siendo
    /// una moneda al aire según quién escribió último. El dueño reenvió algo parado en FRIKAF y le
    /// salió por TRANSRADIO. Y peor: si el destinatario NUNCA había escrito, no había línea que
    /// elegir y caía en la línea por defecto del .env (que es TRANSRADIO), sin avisar.
    ///
    /// Ahora la línea siempre viene decidida DESDE LA PANTALLA, que es la única que sabe con qué
    /// empresa está trabajando el usuario y se la muestra antes de mandar. Nunca más se adivina.
    /// </summary>
    public static async Task<Resultado> ReenviarUnoAsync(ApiClient api, ApiClient.TwMsgDto m, string numeroDestino, string? lineaPhoneId)
    {
        try
        {
            if (EsMensajeContacto(m.Cuerpo))
            {
                var contactos = ParseContactos(m.Cuerpo);
                if (contactos.Count == 0) return Resultado.NadaQueMandar;
                bool todos = true;
                foreach (var ct in contactos)
                {
                    var (cok, _) = await api.EnviarTwContactoAsync(numeroDestino, ct.Nombre, ct.Numero, lineaPhoneId);
                    if (!cok) todos = false;
                }
                return todos ? Resultado.Enviado : Resultado.Fallo;
            }

            if (!string.IsNullOrEmpty(m.MediaUrl))
            {
                var (mok, _) = await api.SendTwMediaAsync(numeroDestino, m.MediaUrl!, m.Cuerpo, m.MediaFilename, lineaPhoneId);
                return mok ? Resultado.Enviado : Resultado.Fallo;
            }

            if (!string.IsNullOrWhiteSpace(m.Cuerpo))
            {
                var (tok, _) = await api.SendTwMensajeAsync(numeroDestino, m.Cuerpo!, lineaPhoneId);
                return tok ? Resultado.Enviado : Resultado.Fallo;
            }

            return Resultado.NadaQueMandar;
        }
        catch
        {
            return Resultado.Fallo;
        }
    }

    /// <summary>
    /// El texto que se le muestra al usuario según cómo salió la tanda. Está acá para que el
    /// celular y la computadora digan LO MISMO — sobre todo el aviso de las 24 hs, que es la
    /// causa real de casi todos los fallos y si no se explica parece que el sistema se rompió.
    /// </summary>
    public static (string Texto, bool EsError, bool EsAdvertencia) Resumen(int ok, int fail)
    {
        if (fail == 0)
            return ($"Reenviado ✓ ({ok} envío(s))", false, false);
        if (ok == 0)
            return ("No se pudo reenviar. Puede ser que esos contactos no te escribieron en las últimas 24 hs " +
                    "(WhatsApp no deja mandarles algo libre hasta que respondan).", true, false);
        return ($"Reenviado a algunos: {ok} sí, {fail} no (los que fallaron seguro están fuera de las 24 hs).",
                false, true);
    }
}
