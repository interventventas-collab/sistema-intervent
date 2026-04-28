namespace Web.Models;

public class BulkImportError
{
    public int Row { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class BulkImportResult
{
    public int TotalRows { get; set; }
    public int Created { get; set; }
    public int Skipped { get; set; }
    public List<BulkImportError> Errors { get; set; } = new();
}
