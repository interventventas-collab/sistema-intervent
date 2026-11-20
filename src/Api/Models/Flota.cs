using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 20/11/2026: datos propios de cada vehículo (foto, y los que NO son de Shell Flota: el auto
/// propio de un repartidor, como el Siena de Nacho). Los de Shell salen solos de Shell_Cargas;
/// acá solo hay fila si se les cargó foto.
/// </summary>
[Table("Flota_Vehiculos")]
public class FlotaVehiculo
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    [Required, MaxLength(20)] public string Placa { get; set; } = "";
    /// <summary>Solo para los propios (los de Shell toman el nombre de Shell).</summary>
    [MaxLength(60)] public string? Nombre { get; set; }
    /// <summary>NULL = de la empresa (Shell). Con valor = auto propio de ese repartidor: solo le aparece a él.</summary>
    public int? RepartidorId { get; set; }
    public byte[]? Foto { get; set; }
    public DateTime? FotoAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 20/11/2026: con qué vehículo sale a repartir cada repartidor cada día. Si lo cambia a mitad
/// del día queda otra fila: vale la última (y las entregas anteriores, la que regía a esa hora).
/// </summary>
[Table("Rep_CocheDia")]
public class RepCocheDia
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    public int RepartidorId { get; set; }
    /// <summary>Día argentino.</summary>
    [Column(TypeName = "date")] public DateTime FechaAr { get; set; }
    /// <summary>NULL = "Otro" (va el comentario).</summary>
    [MaxLength(20)] public string? Placa { get; set; }
    [Required, MaxLength(80)] public string Nombre { get; set; } = "";
    [MaxLength(200)] public string? Comentario { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
