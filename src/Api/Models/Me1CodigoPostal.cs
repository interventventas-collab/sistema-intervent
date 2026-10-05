using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-10-05: Códigos postales que el usuario sacó de me1 desde /meli/me1/codigos-postales.
/// Una fila = un CP que NO se ofrece. La tabla de tarifas (rangos en MeliMe1Controller) sigue
/// siendo la misma; al armar el tarifario para MeLi se saltean los CPs que estén acá.
/// Volver a ofrecer = borrar la fila.
/// </summary>
[Table("Me1_CpExcluidos")]
public class Me1CpExcluido
{
    [Key, DatabaseGenerated(DatabaseGeneratedOption.None)]
    public int Cp { get; set; }
    public DateTime ExcluidoAt { get; set; } = DateTime.UtcNow;
    [MaxLength(100)]
    public string? ExcluidoPor { get; set; }
}

/// <summary>
/// 2026-10-05: Ubicación (lat/lng) de cada localidad/barrio de la tabla de CPs me1, para el mapa.
/// Se busca UNA vez con Google Geocoding y queda guardada. Clave = "Provincia|Localidad".
/// Encontrado=false: Google no la ubicó dentro de la zona esperada (no se reintenta sola).
/// </summary>
[Table("Me1_LocalidadUbicaciones")]
public class Me1LocalidadUbicacion
{
    [Key, MaxLength(150)]
    public string Clave { get; set; } = "";
    [Column(TypeName = "decimal(9,6)")]
    public decimal? Lat { get; set; }
    [Column(TypeName = "decimal(9,6)")]
    public decimal? Lng { get; set; }
    public bool Encontrado { get; set; }
    public DateTime BuscadoAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// 2026-10-05: Cada vez que el sistema le manda el tarifario me1 a MeLi
/// (POST /shipping/me1/v1/tariff/update). MeLi lo procesa aparte y avisa al callback;
/// Estado: enviado → active (MeLi lo tomó) / error (con Detalle).
/// </summary>
[Table("Me1_TarifaEnvios")]
public class Me1TarifaEnvio
{
    public int Id { get; set; }
    public long MeliUserId { get; set; }
    [MaxLength(100)]
    public string? Cuenta { get; set; }
    [MaxLength(100)]
    public string? ResourceId { get; set; }
    [MaxLength(30)]
    public string Estado { get; set; } = "enviado";
    [MaxLength(2000)]
    public string? Detalle { get; set; }
    public int CantidadCps { get; set; }
    public DateTime EnviadoAt { get; set; } = DateTime.UtcNow;
    [MaxLength(100)]
    public string? EnviadoPor { get; set; }
    public DateTime? ActualizadoAt { get; set; }
}
