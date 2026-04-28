namespace Web.Models;

public class BulkImportError
{
    public int Row { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class BulkImportWarning
{
    public int Row { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class BulkImportResult
{
    public int TotalRows { get; set; }
    public int Created { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public List<BulkImportError> Errors { get; set; } = new();
    public List<BulkImportWarning> Warnings { get; set; } = new();
}
