using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("MapeoDrivers")]
public class MapeoDriver
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(50)] public string? Telefono { get; set; }

    /// <summary>Color hex para distinguir al repartidor en el mapa (#1d4ed8 default).</summary>
    [MaxLength(10)] public string Color { get; set; } = "#1d4ed8";

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

[Table("MapeoFavoritos")]
public class MapeoFavorito
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(100)]
    public string Alias { get; set; } = string.Empty;

    [Required, MaxLength(300)]
    public string Direccion { get; set; } = string.Empty;

    [Column(TypeName = "decimal(10,7)")] public decimal Latitude { get; set; }
    [Column(TypeName = "decimal(10,7)")] public decimal Longitude { get; set; }

    [MaxLength(150)] public string? ContactName { get; set; }
    [MaxLength(50)] public string? Telefono { get; set; }
    [MaxLength(500)] public string? Notas { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
