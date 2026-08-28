using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Web.Services;

/// <summary>
/// 2026-08-12: Lugar CENTRAL con los chats de WhatsApp que ve el usuario Depósito.
///
/// Para CUALQUIER chat de esta lista aplican las mismas reglas:
///   1) aparece el botón "Ocultar a Depósito" (para admin/oficina).
///   2) los mensajes salen firmados con la abreviatura de quien escribe.
///   3) las reacciones son las DOS de cada equipo, con significado (ver <see cref="EmojisPara"/>).
///
/// En los chats que NO están en esta lista, nada de eso aparece: siguen como siempre.
///
/// 2026-08-28: la lista dejó de estar escrita acá. Ahora la guarda el servidor
/// (AppSettings["whatsapp.deposito.chats"]) y se arma desde el menú ⋮ del chat, con
/// "Que Depósito vea este chat". Este archivo la trae y la deja a mano para las pantallas.
/// Mientras no se haya cargado (o si falla la conexión), vale <see cref="PorDefecto"/>.
/// </summary>
public static class DepositoChats
{
    /// <param name="Numero">Número del contacto (con o sin "whatsapp:"/"+"; se compara solo por dígitos).</param>
    /// <param name="LineaPhoneId">PhoneId de la línea nuestra (ej. FIJO TRANSRADIO = 1195191513683780).</param>
    /// <param name="Titulo">Cómo se llama el contacto (para mostrarlo en la pantalla de Depósito).</param>
    /// <param name="LineaNombre">Nombre lindo de la línea (ej. "FIJO TRANSRADIO").</param>
    public record ChatRef(string Numero, string LineaPhoneId, string Titulo = "", string LineaNombre = "")
    {
        /// <summary>Cómo mostrarlo: el nombre si lo tiene; si no, el número pelado.</summary>
        public string TituloLindo => string.IsNullOrWhiteSpace(Titulo)
            ? Numero.Replace("whatsapp:", "")
            : Titulo;
    }

    /// <summary>El chat de siempre. Es lo que vale hasta que el servidor conteste.</summary>
    private static readonly ChatRef[] PorDefecto =
    {
        new("whatsapp:+5491158464160", "1195191513683780", "Gabriel Palanica", "FIJO TRANSRADIO"),
    };

    private static ChatRef[] _chats = PorDefecto;
    private static bool _cargado;

    /// <summary>Los chats que ve Depósito, hoy.</summary>
    public static IReadOnlyList<ChatRef> Chats => _chats;

    /// <summary>Trae la lista del servidor. Se pide una sola vez por sesión salvo que se fuerce.</summary>
    public static async Task CargarAsync(ApiClient api, bool forzar = false)
    {
        if (_cargado && !forzar) return;
        var lista = await api.GetDepositoChatsAsync();
        if (lista is null) return;   // sin conexión: seguimos con lo que teníamos
        _chats = lista.Select(x => new ChatRef(x.Numero, x.Linea, x.Titulo, x.LineaNombre)).ToArray();
        _cargado = true;
    }

    // ── Reacciones con significado, SOLO en estos chats ────────────────────────────────────────
    // En el resto de los chats la tira de emojis sigue siendo la de siempre (👍 ❤️ 😂 ...).
    // Acá cada equipo tiene sus DOS botones, y no se pisan porque son distintos:
    //   oficina  💻 lo armé (lo cargué al sistema)   ⛔ no se puede
    //   deposito 📦 armado                            🚧 hay un problema
    // Los cuatro se le mandan al cliente (el último pisa al anterior en su celular).
    // PARA CAMBIAR UN EMOJI: tocar el renglón de abajo, no hace falta nada más.
    public static readonly (string Emoji, string Que)[] EmojisOficina =
    {
        ("💻", "lo armé"),
        ("⛔", "no se puede"),
    };

    public static readonly (string Emoji, string Que)[] EmojisDeposito =
    {
        ("📦", "armado"),
        ("🚧", "hay un problema"),
    };

    /// <summary>Los dos emojis que le tocan a quien está mirando el chat.</summary>
    public static (string Emoji, string Que)[] EmojisPara(bool esDeposito)
        => esDeposito ? EmojisDeposito : EmojisOficina;

    /// <summary>Qué significa un emoji (para el globito de ayuda). Vacío si no es de los nuestros.</summary>
    public static string QueSignifica(string? emoji)
    {
        foreach (var e in EmojisOficina) if (e.Emoji == emoji) return e.Que;
        foreach (var e in EmojisDeposito) if (e.Emoji == emoji) return e.Que;
        return "";
    }

    private static string SoloDigitos(string? s)
        => string.IsNullOrEmpty(s) ? "" : new string(s.Where(char.IsDigit).ToArray());

    /// <summary>¿Este chat (número del contacto + PhoneId de la línea) es uno de los que ve Depósito?</summary>
    public static bool EsVisible(string? numero, string? lineaPhoneId)
    {
        var n = SoloDigitos(numero);
        var l = SoloDigitos(lineaPhoneId);
        if (n.Length == 0 || l.Length == 0) return false;
        foreach (var c in _chats)
            if (SoloDigitos(c.Numero) == n && SoloDigitos(c.LineaPhoneId) == l)
                return true;
        return false;
    }
}
