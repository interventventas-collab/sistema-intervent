using System.ComponentModel.DataAnnotations;

namespace Api.Models;

public class BackupFile
{
    public int Id { get; set; }

    [Required, MaxLength(255)]
    public string FileName { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [Required, MaxLength(20)]
    public string BackupType { get; set; } = "Manual"; // Manual | Programado | Subido

    [Required, MaxLength(20)]
    public string Status { get; set; } = "Completed"; // InProgress | Completed | Failed

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int? CreatedByUserId { get; set; }
}
