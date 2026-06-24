using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Vault_Settings")]
public class VaultSetting
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(255)]
    public string MasterPasswordHash { get; set; } = string.Empty;

    [Required, MaxLength(64)]
    public string KdfSalt { get; set; } = string.Empty;

    public int AutoLockMinutes { get; set; } = 5;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

[Table("Vault_Entries")]
public class VaultEntry
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Servicio { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? Categoria { get; set; }

    [Required]
    public string UsuarioEnc { get; set; } = string.Empty;

    public string? OtroEnc { get; set; }

    [Required]
    public string PasswordEnc { get; set; } = string.Empty;

    public string? PinEnc { get; set; }
    public string? MailEnc { get; set; }
    public string? EnlaceEnc { get; set; }
    public string? NotasEnc { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
