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
    public bool AutoAnswered { get; set; }
    /// <summary>Tipo de logística de la publicación (self_service, fulfillment, drop_off, ...). Para el cartelito Flex/Correo/ME1/Full.</summary>
    public string? LogisticType { get; set; }
    public string MeliUrl { get; set; } = "";
}

public class MeliQuestionsUnreadDto
{
    public int Total { get; set; }
    public int NotSeen { get; set; }
}

/// <summary>Detalle de una pregunta para la bandeja: publicación + comprador.</summary>
public class MeliQuestionDetailDto
{
    public MeliQuestionDto Question { get; set; } = new();
    public MeliItemBriefDto? Item { get; set; }
    public MeliBuyerBriefDto? Buyer { get; set; }
}

public class MeliItemBriefDto
{
    public string ItemId { get; set; } = "";
    public string? Title { get; set; }
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public string? Sku { get; set; }
    public string? Status { get; set; }
    public bool CatalogListing { get; set; }
    public string? LogisticType { get; set; }
    public string? Permalink { get; set; }
    public string? Thumbnail { get; set; }
}

public class MeliBuyerBriefDto
{
    public long BuyerId { get; set; }
    public string? Nickname { get; set; }
    /// <summary>True si el comprador ya está en nuestra base de clientes (compró antes).</summary>
    public bool IsKnown { get; set; }
    public string? ReceiverName { get; set; }
    public string? Phone { get; set; }
    public string? Neighborhood { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public int OrdersCount { get; set; }
    public decimal TotalSpent { get; set; }
    public string? LastItems { get; set; }
    public DateTime? FirstPurchaseAt { get; set; }
    public DateTime? LastPurchaseAt { get; set; }
}
