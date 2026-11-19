using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 18/11/2026: una carga de combustible leída del "Reporte transaccional" de Shell Flota
/// (la trae el mismo robot que lee el saldo). Pantalla /combustible + aviso en la campanita.
/// </summary>
[Table("Shell_Cargas")]
public class ShellCarga
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Para no duplicar: tarjeta + fecha/hora + importe.</summary>
    [Required, MaxLength(120)]
    public string Clave { get; set; } = string.Empty;

    [MaxLength(20)] public string Placa { get; set; } = string.Empty;
    [MaxLength(120)] public string? Vehiculo { get; set; }
    [MaxLength(30)] public string? Tarjeta { get; set; }
    [MaxLength(150)] public string? Estacion { get; set; }
    [MaxLength(60)] public string? Producto { get; set; }
    [MaxLength(30)] public string? Transaccion { get; set; }
    [MaxLength(100)] public string? Conductor { get; set; }

    /// <summary>Fecha y hora de la carga, hora ARGENTINA tal como la muestra Shell.</summary>
    public DateTime FechaHoraAr { get; set; }

    [Column(TypeName = "decimal(18,2)")] public decimal Litros { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Importe { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? PrecioLitro { get; set; }
    public int? Odometro { get; set; }

    /// <summary>Cuándo la vio el robot por primera vez (UTC).</summary>
    public DateTime DetectadaAt { get; set; } = DateTime.UtcNow;
    /// <summary>Vino en la primera importación (no se avisa en la campanita).</summary>
    public bool Inicial { get; set; }
}
