namespace Web.Models;

public class TelegramAccountDto
{
    public int Id { get; set; }
    public string Proposito { get; set; } = "AVISOS";
    public bool HasToken { get; set; }
    public string? BotUsername { get; set; }
    public long? ChatId { get; set; }
    public string? VinculacionCode { get; set; }
    public bool IsActive { get; set; } = true;
    public bool NotifVentas { get; set; } = true;
    public bool NotifAlertas { get; set; } = true;
    public bool NotifFichadas { get; set; } = true;
    public bool LastSyncOk { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastSyncAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    /// <summary>Cuántas personas están vinculadas a este bot (2026-07-16: varias por bot).</summary>
    public int PersonasVinculadas { get; set; }
}

/// <summary>Una persona vinculada a un bot de Telegram, con sus tildes de qué avisos recibe.</summary>
public class TelegramChatDto
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public string? Nombre { get; set; }
    public bool NotifVentas { get; set; }
    public bool NotifAlertas { get; set; }
    public bool NotifFichadas { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class UpdateTelegramChatRequest
{
    public string? Nombre { get; set; }
    public bool NotifVentas { get; set; }
    public bool NotifAlertas { get; set; }
    public bool NotifFichadas { get; set; }
}

public class SaveTelegramAccountRequest
{
    public string? BotToken { get; set; }
    public bool IsActive { get; set; } = true;
    public bool NotifVentas { get; set; } = true;
    public bool NotifAlertas { get; set; } = true;
    public bool NotifFichadas { get; set; } = true;
}

public class TelegramProbarResultDto
{
    public bool Ok { get; set; }
    public string? BotUsername { get; set; }
    public long? ChatId { get; set; }
    public bool TestEnviado { get; set; }
    public string? Error { get; set; }
}

public class TelegramVincularResultDto
{
    public bool Ok { get; set; }
    public long? ChatId { get; set; }
    public string? Error { get; set; }
}

public class TelegramTestMsgResultDto
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
}
