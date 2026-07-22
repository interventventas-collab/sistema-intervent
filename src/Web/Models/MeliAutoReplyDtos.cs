using System.Text.Json.Serialization;

namespace Web.Models;

/// <summary>Config completa del respondedor automático (todo lo que necesita la pantalla).</summary>
public class MeliAutoReplyConfigDto
{
    public bool Enabled { get; set; }
    public int DelayMinutes { get; set; } = 30;
    public string Signature { get; set; } = "";
    public bool HolidayToday { get; set; }
    public string NowArgentina { get; set; } = "";
    public List<MeliAutoReplyScheduleDto> Schedule { get; set; } = new();
    public List<MeliAutoReplyMessageDto> Messages { get; set; } = new();
}

public class MeliAutoReplyMessageDto
{
    public int Id { get; set; }
    public string Body { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public class MeliAutoReplyScheduleDto
{
    public int DayOfWeek { get; set; }
    public bool IsActive { get; set; }
    public bool AllDay { get; set; }
    public string StartTime { get; set; } = "21:00";
    public string EndTime { get; set; } = "06:00";

    // Wrappers para el <input type="time"> de Blazor (que trabaja con TimeOnly, no string).
    // No se serializan: al backend viajan StartTime/EndTime como "HH:mm".
    [JsonIgnore]
    public TimeOnly? Start
    {
        get => TimeOnly.TryParse(StartTime, out var t) ? t : null;
        set => StartTime = value?.ToString("HH:mm") ?? "";
    }

    [JsonIgnore]
    public TimeOnly? End
    {
        get => TimeOnly.TryParse(EndTime, out var t) ? t : null;
        set => EndTime = value?.ToString("HH:mm") ?? "";
    }
}

public class MeliAutoReplyRecentDto
{
    public int Id { get; set; }
    public string? ItemTitle { get; set; }
    public string? FromNickname { get; set; }
    public string? Text { get; set; }
    public string? AnswerText { get; set; }
    public DateTime? DateAnswered { get; set; }
}

public class MeliAutoReplyPreviewDto
{
    public string Text { get; set; } = "";
}
