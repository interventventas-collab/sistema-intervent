namespace Api.DTOs;

/// <summary>Un usuario del sistema como interlocutor del chat, con su contador de no leídos.</summary>
public record ChatUsuarioDto(int UserId, string Nombre, string? Rol, int NoLeidos, string? UltimoMensaje, DateTime? UltimaFecha);

/// <summary>Estado completo del panel izquierdo: grupo general + lista de usuarios.</summary>
public record ChatConversacionesDto(
    int GrupoNoLeidos,
    string? GrupoUltimo,
    DateTime? GrupoUltimaFecha,
    List<ChatUsuarioDto> Usuarios);

/// <summary>Un mensaje ya listo para pintar en pantalla.</summary>
public record ChatMensajeDto(int Id, int DeUserId, string DeNombre, int? ParaUserId, string Cuerpo, DateTime CreatedAt, bool Mio);

/// <summary>Contadores para el globito de aviso.</summary>
public record ChatNoLeidosDto(int Total, int Grupo, int Directos);

public class EnviarChatRequest
{
    /// <summary>NULL = Grupo general. Con valor = privado a ese usuario.</summary>
    public int? ParaUserId { get; set; }
    public string Cuerpo { get; set; } = string.Empty;
}
