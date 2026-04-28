namespace Api.DTOs;

public record BulkImportError(int Row, string Message);

public record BulkImportResult(
    int TotalRows,
    int Created,
    int Skipped,
    List<BulkImportError> Errors
);
