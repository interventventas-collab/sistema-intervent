namespace Web.Models;

public class MeliQuestionDto
{
    public int Id { get; set; }
    public long MeliQuestionId { get; set; }
    public int AccountId { get; set; }
    public string? AccountNickname { get; set; }
    public string ItemId { get; set; } = "";
    public string? ItemTitle { get; set; }
    public string? ItemThumbnail { get; set; }
    public long FromUserId { get; set; }
    public string? FromNickname { get; set; }
    public string Text { get; set; } = "";
    public string? AnswerText { get; set; }
    public string Status { get; set; } = "UNANSWERED";
    public DateTime DateCreated { get; set; }
    public DateTime? DateAnswered { get; set; }
    public DateTime? SeenAt { get; set; }
    public bool IsNew { get; set; }
    public string MeliUrl { get; set; } = "";
}

public class MeliQuestionsUnreadDto
{
    public int Total { get; set; }
    public int NotSeen { get; set; }
}
