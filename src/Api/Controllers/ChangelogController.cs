using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ChangelogController : ControllerBase
{
    public record CommitDto(string Hash, string ShortHash, string Subject, string Body, string Author, string Date);
    public record ChangelogResponse(List<CommitDto> Commits, int Total, int Page, int PageSize, int TotalPages);

    [HttpGet]
    public IActionResult GetChangelog([FromQuery] int page = 1, [FromQuery] int pageSize = 15)
    {
        try
        {
            if (page < 1) page = 1;
            if (pageSize < 1) pageSize = 15;
            if (pageSize > 50) pageSize = 50;

            var skip = (page - 1) * pageSize;

            // Get total count
            var countPsi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--git-dir=/repo/.git rev-list --count HEAD",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var countProcess = Process.Start(countPsi);
            var totalStr = countProcess?.StandardOutput.ReadToEnd().Trim() ?? "0";
            countProcess?.WaitForExit();
            int.TryParse(totalStr, out var total);

            // Get paginated commits
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = $"--git-dir=/repo/.git log --skip={skip} -{pageSize} --format=\"COMMIT_START%nHash:%H%nShortHash:%h%nAuthor:%an%nDate:%aI%nSubject:%s%nBody:%b%nCOMMIT_END\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
                return Ok(new ChangelogResponse(new List<CommitDto>(), 0, page, pageSize, 0));

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                return Ok(new ChangelogResponse(new List<CommitDto>(), 0, page, pageSize, 0));

            var commits = ParseCommits(output);
            var totalPages = (int)Math.Ceiling((double)total / pageSize);

            return Ok(new ChangelogResponse(commits, total, page, pageSize, totalPages));
        }
        catch
        {
            return Ok(new ChangelogResponse(new List<CommitDto>(), 0, page, pageSize, 0));
        }
    }

    private static List<CommitDto> ParseCommits(string output)
    {
        var commits = new List<CommitDto>();
        var blocks = output.Split("COMMIT_START", StringSplitOptions.RemoveEmptyEntries);

        foreach (var block in blocks)
        {
            var trimmed = block.Trim();
            if (string.IsNullOrEmpty(trimmed) || !trimmed.Contains("COMMIT_END"))
                continue;

            var content = trimmed.Replace("COMMIT_END", "").Trim();
            var lines = content.Split('\n');

            string hash = "", shortHash = "", author = "", date = "", subject = "";
            var bodyLines = new List<string>();
            bool inBody = false;

            foreach (var line in lines)
            {
                if (line.StartsWith("Hash:") && !inBody)
                    hash = line[5..].Trim();
                else if (line.StartsWith("ShortHash:") && !inBody)
                    shortHash = line[10..].Trim();
                else if (line.StartsWith("Author:") && !inBody)
                    author = line[7..].Trim();
                else if (line.StartsWith("Date:") && !inBody)
                    date = line[5..].Trim();
                else if (line.StartsWith("Subject:") && !inBody)
                {
                    subject = line[8..].Trim();
                    inBody = true;
                }
                else if (inBody && line.StartsWith("Body:"))
                    bodyLines.Add(line[5..].Trim());
                else if (inBody)
                    bodyLines.Add(line);
            }

            var body = string.Join("\n", bodyLines).Trim();

            if (!string.IsNullOrEmpty(hash))
                commits.Add(new CommitDto(hash, shortHash, subject, body, author, date));
        }

        return commits;
    }
}
