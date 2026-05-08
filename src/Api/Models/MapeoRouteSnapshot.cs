using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("MapeoRouteSnapshots")]
public class MapeoRouteSnapshot
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public int StopsCount { get; set; }
    public int VehiclesCount { get; set; }
    public int DriversCount { get; set; }

    /// <summary>JSON con la lista de paradas + sus asignaciones (driver, vehículo, orden) al momento del snapshot.</summary>
    [Required]
    public string StopsJson { get; set; } = "[]";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [MaxLength(100)] public string? CreatedByUsername { get; set; }
    [MaxLength(500)] public string? Notes { get; set; }
}
