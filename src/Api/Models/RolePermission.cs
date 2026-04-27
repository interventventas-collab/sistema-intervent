using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("RolePermissions")]
public class RolePermission
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int RoleId { get; set; }

    [Required]
    [MaxLength(50)]
    public string MenuKey { get; set; } = string.Empty;

    [ForeignKey("RoleId")]
    public Role Role { get; set; } = null!;
}
