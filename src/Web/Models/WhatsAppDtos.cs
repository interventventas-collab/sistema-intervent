namespace Web.Models;

/// <summary>2026-06-23: chat del sidebar del WhatsApp Web vinculado via Playwright.</summary>
public class WhatsAppChatDto
{
    public string Name { get; set; } = "";
    public string LastMsg { get; set; } = "";
    public string LastMsgAt { get; set; } = "";
    public int Unread { get; set; }
}

public class WhatsAppStatusDto
{
    public bool Linked { get; set; }
    public bool IsLinking { get; set; }
    public string? Info { get; set; }
    /// <summary>2026-06-23: timestamp ISO del ultimo heartbeat exitoso. Si esta vieja (>5min)
    /// el container Playwright probablemente esta colgado.</summary>
    public string? LastHeartbeatAt { get; set; }
    /// <summary>2026-06-23: cuando paso de linked=true a linked=false la ultima vez.</summary>
    public string? LastDisconnectedAt { get; set; }
}

public class WhatsAppLinkedDto
{
    public bool Linked { get; set; }
}

public class WhatsAppSendResultDto
{
    public string Phone { get; set; } = "";
    public string? Name { get; set; }
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
