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

    /// <summary>Token publico para compartir el link de la ruta del chofer (sin login).</summary>
    [MaxLength(64)] public string? ShareToken { get; set; }

    /// <summary>Vinculo al repartidor REAL (Cafe_Repartidores.Id). Cuando esta seteado, la ruta
    /// del mapa de este chofer le aparece en el telefono de ese repartidor (unifica ambos grupos).</summary>
    public int? CafeRepartidorId { get; set; }

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

[Table("MapeoStops")]
public class MapeoStop
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>flex | favorito | venta_cafe | manual</summary>
    [Required, MaxLength(20)] public string Origin { get; set; } = "manual";

    /// <summary>Id del origen (MeliShipmentId, FavoritoId, CafeVentaId, o null para manual).</summary>
    [MaxLength(50)] public string? OriginRefId { get; set; }

    [MaxLength(100)] public string? Alias { get; set; }

    [Required, MaxLength(300)] public string Direccion { get; set; } = string.Empty;

    /// <summary>Localidad/ciudad de la parada — útil para agrupar la lista por zona.
    /// Para Flex importados se toma de MeliShipment.City; para favoritos/manuales puede quedar null.</summary>
    [MaxLength(150)] public string? Localidad { get; set; }

    [Column(TypeName = "decimal(10,7)")] public decimal Latitude { get; set; }
    [Column(TypeName = "decimal(10,7)")] public decimal Longitude { get; set; }

    [MaxLength(150)] public string? ContactName { get; set; }
    [MaxLength(50)] public string? Telefono { get; set; }
    [MaxLength(500)] public string? Notas { get; set; }

    [MaxLength(30)] public string InternalStatus { get; set; } = "pending";

    public int? AssignedDriverId { get; set; }
    [ForeignKey(nameof(AssignedDriverId))] public MapeoDriver? AssignedDriver { get; set; }

    /// <summary>Slot del vehiculo del dia (1..N). El chofer se asigna despues al slot, no al stop directo.</summary>
    public int? AssignedVehicleSlot { get; set; }

    public int? OrderInRoute { get; set; }

    /// <summary>
    /// 2026-09-03: PARA QUÉ DÍA es esta parada (fecha de Argentina, sin hora). Antes el mapa era uno
    /// solo y "hoy" era lo único que existía: lo de días anteriores se borraba al abrir el mapa y no
    /// se podía preparar el reparto de mañana. Ahora cada parada sabe a qué día pertenece, así se
    /// pueden mirar los días pasados (solo lectura) y armar los que vienen sin tocar el de hoy.
    ///
    /// ⚠ REGLA DE ORO: al celular del repartidor SOLO le llegan las paradas de HOY. Todo lo que lea
    /// paradas fuera del mapa (celu, tablero, link compartido, PDF) tiene que filtrar por este campo.
    /// </summary>
    [Column(TypeName = "date")]
    public DateTime FechaReparto { get; set; } = DateTime.UtcNow.AddHours(-3).Date;

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

/// <summary>
/// Caché del "tipo de calle" (asfalto/tierra/empedrado) de un domicilio, deducido por IA
/// mirando la foto de Street View. Se calcula UNA sola vez por punto y se guarda acá, así
/// no se vuelve a pagar la consulta a la IA cada vez que se abre el globito.
/// La clave es "lat,lng" redondeado a 5 decimales (~1 metro).
/// </summary>
public class MapeoSurfaceCache
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(40)]
    public string PointKey { get; set; } = string.Empty;

    // asfalto | tierra | empedrado | no_seguro | sin_foto
    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "no_seguro";

    // alta | media | baja
    [MaxLength(10)]
    public string? Confianza { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
