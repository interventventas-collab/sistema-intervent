namespace Web.Models;

public class FileEntryDto
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public bool IsFolder { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }
}

public class FilesListResponse
{
    public string Path { get; set; } = "";
    public string Provider { get; set; } = "local";
    public List<FileEntryDto> Entries { get; set; } = new();
}

public class StorageProviderOption
{
    public string Value { get; set; } = "";
    public string Label { get; set; } = "";
    public bool Enabled { get; set; }
}

public class StorageProviderResponse
{
    public string Provider { get; set; } = "local";
    public List<StorageProviderOption> Options { get; set; } = new();
}

public class FileUploadResult
{
    public string? Name { get; set; }
    public bool Success { get; set; }
    public long? Size { get; set; }
    public string? Path { get; set; }
    public string? Error { get; set; }
}

public class FileDeleteResult
{
    public string? Path { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}
