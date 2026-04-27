namespace Web.Models;

public class WhatsAppStatusDto
{
    public bool Linked { get; set; }
    public bool IsLinking { get; set; }
    public string? Info { get; set; }
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
