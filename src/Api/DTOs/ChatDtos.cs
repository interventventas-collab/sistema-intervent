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
public record ChatMensajeDto(int Id, int DeUserId, string DeNombre, int? ParaUserId, string Cuerpo, DateTime CreatedAt, bool Mio,
    // 2026-07-06: adjunto opcional. AdjuntoTipo: "image"|"audio"|"file". AdjuntoDisponible=false si el archivo ya se limpió (vencido).
    string? AdjuntoTipo = null, string? AdjuntoNombre = null, bool AdjuntoDisponible = false);

/// <summary>Contadores para el globito de aviso.</summary>
public record ChatNoLeidosDto(int Total, int Grupo, int Directos);

public class EnviarChatRequest
{
    /// <summary>NULL = Grupo general. Con valor = privado a ese usuario.</summary>
    public int? ParaUserId { get; set; }
    public string Cuerpo { get; set; } = string.Empty;

    /// <summary>2026-07-06: firma del operador activo (Osmar/Germán/Gabriel/...). Si viene,
    /// se usa como nombre a mostrar del mensaje, así se ve QUIÉN escribió aunque varios
    /// compartan la misma cuenta (PIN por operador). Si es null, se usa el nombre de la cuenta.</summary>
    public string? Firma { get; set; }
}
