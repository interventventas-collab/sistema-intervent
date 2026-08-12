using System.Linq;

namespace Web.Services;

/// <summary>
/// 2026-08-12: Lugar CENTRAL con los chats de WhatsApp que ve el usuario Depósito.
///
/// Para CUALQUIER chat de esta lista aplican las mismas dos reglas:
///   1) aparece el botón "Ocultar a Depósito" (para admin/oficina).
///   2) los mensajes salen firmados con la abreviatura del operador del PIN
///      (ver <see cref="OperatorService.FirmaCorta"/>): OSMAR→(os), GERMAN→(ger), GABRIEL→(ga).
///
/// En los chats que NO están en esta lista, el botón no aparece y no se firma nada.
///
/// PARA SUMAR UN CHAT NUEVO: agregar un renglón a <see cref="Chats"/> con el número del
/// contacto y el PhoneId de la línea. Las dos reglas se aplican solas, sin tocar nada más.
/// </summary>
public static class DepositoChats
{
    /// <param name="Numero">Número del contacto (con o sin "whatsapp:"/"+"; se compara solo por dígitos).</param>
    /// <param name="LineaPhoneId">PhoneId de la línea nuestra (ej. FIJO TRANSRADIO = 1195191513683780).</param>
    public record ChatRef(string Numero, string LineaPhoneId);

    // Hoy: FIJO TRANSRADIO (línea 1195191513683780)  <->  +54 9 11 5846-4160
    public static readonly ChatRef[] Chats =
    {
        new("+5491158464160", "1195191513683780"),
    };

    private static string SoloDigitos(string? s)
        => string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

    /// <summary>¿Este chat (número del contacto + PhoneId de la línea) es uno de los que ve Depósito?</summary>
    public static bool EsVisible(string? numero, string? lineaPhoneId)
    {
        var n = SoloDigitos(numero);
        var l = SoloDigitos(lineaPhoneId);
        if (n.Length == 0 || l.Length == 0) return false;
        foreach (var c in Chats)
            if (SoloDigitos(c.Numero) == n && SoloDigitos(c.LineaPhoneId) == l)
                return true;
        return false;
    }
}
