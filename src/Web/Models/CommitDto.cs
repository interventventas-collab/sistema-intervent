namespace Web.Models;

public record CommitDto(string Hash, string ShortHash, string Subject, string Body, string Author, string Date);
public record ChangelogResponse(List<CommitDto> Commits, int Total, int Page, int PageSize, int TotalPages);
