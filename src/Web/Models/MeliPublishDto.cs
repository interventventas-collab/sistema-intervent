namespace Web.Models;

public class CategoryPredictionDto
{
    public string CategoryId { get; set; } = "";
    public string CategoryName { get; set; } = "";
    public string CategoryPath { get; set; } = "";
    public double Probability { get; set; }
}

public class CategoryAttributeDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string ValueType { get; set; } = "";
    public bool Required { get; set; }
    public List<AttributeValueOption> Values { get; set; } = new();
    public string? DefaultValue { get; set; }
    public string? SuggestedValue { get; set; }
}

public class AttributeValueOption
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
}

public class SuggestedAttributeDto
{
    public string AttributeId { get; set; } = "";
    public string? ValueId { get; set; }
    public string? ValueName { get; set; }
}

public class PublishItemRequest
{
    public int ProductId { get; set; }
    public int MeliAccountId { get; set; }
    public string CategoryId { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public int AvailableQuantity { get; set; }
    public string Condition { get; set; } = "new";
    public string ListingTypeId { get; set; } = "gold_special";
    public bool FreeShipping { get; set; } = true;
    public List<PublishAttributeDto> Attributes { get; set; } = new();
    public List<string> PictureUrls { get; set; } = new();
}

public class PublishAttributeDto
{
    public string Id { get; set; } = "";
    public string? ValueId { get; set; }
    public string? ValueName { get; set; }
}

public class PublishItemResponse
{
    public bool Success { get; set; }
    public string? MeliItemId { get; set; }
    public string? Permalink { get; set; }
    public string? Error { get; set; }
}

public class BulkPublishRequest
{
    public List<int> ProductIds { get; set; } = new();
    public int MeliAccountId { get; set; }
    public string ListingTypeId { get; set; } = "gold_special";
    public string PriceMode { get; set; } = "markup";
    public decimal MarkupPercent { get; set; }
    public string Condition { get; set; } = "new";
    public bool FreeShipping { get; set; }
}

public class BulkPublishResponse
{
    public int Total { get; set; }
    public int Successful { get; set; }
    public int Failed { get; set; }
    public int Skipped { get; set; }
    public List<BulkPublishItemResult> Results { get; set; } = new();
}

public class BulkPublishItemResult
{
    public int ProductId { get; set; }
    public string ProductTitle { get; set; } = "";
    public bool Success { get; set; }
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
    public string? MeliItemId { get; set; }
    public string? Permalink { get; set; }
    public string? Error { get; set; }
}
