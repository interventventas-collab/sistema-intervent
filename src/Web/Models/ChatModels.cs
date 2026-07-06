namespace Web.Models;

public class ChatUsuarioDto
{
    public int UserId { get; set; }
    public string Nombre { get; set; } = "";
    public string? Rol { get; set; }
    public int NoLeidos { get; set; }
    public string? UltimoMensaje { get; set; }
    public DateTime? UltimaFecha { get; set; }
}

public class ChatConversacionesDto
{
    public int GrupoNoLeidos { get; set; }
    public string? GrupoUltimo { get; set; }
    public DateTime? GrupoUltimaFecha { get; set; }
    public List<ChatUsuarioDto> Usuarios { get; set; } = new();
}

public class ChatMensajeDto
{
    public int Id { get; set; }
    public int DeUserId { get; set; }
    public string DeNombre { get; set; } = "";
    public int? ParaUserId { get; set; }
    public string Cuerpo { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public bool Mio { get; set; }
}

public class ChatNoLeidosDto
{
    public int Total { get; set; }
    public int Grupo { get; set; }
    public int Directos { get; set; }
}

public class EnviarChatRequest
{
    public int? ParaUserId { get; set; }
    public string Cuerpo { get; set; } = "";

    /// <summary>2026-07-06: firma del operador activo (Osmar/Germán/Gabriel). Se usa como
    /// nombre a mostrar para que se vea quién escribió aunque compartan la cuenta admin.</summary>
    public string? Firma { get; set; }
}
