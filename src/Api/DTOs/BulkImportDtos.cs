namespace Api.DTOs;

public record BulkImportError(int Row, string Message);

public record BulkImportWarning(int Row, string Message);

public record BulkImportResult(
    int TotalRows,
    int Created,
    int Updated,
    int Skipped,
    List<BulkImportError> Errors,
    List<BulkImportWarning> Warnings
);
