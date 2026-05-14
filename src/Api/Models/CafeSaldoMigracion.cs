using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Saldo pendiente migrado desde el sistema viejo (Contabilium u otro CSV).
/// Empieza en estado="pendiente" y el usuario lo asocia a mano con un cliente
/// de la base. Al asociar se crea una Venta tipo "X" como saldo de migracion.
/// </summary>
[Table("Cafe_SaldosMigracion")]
public class CafeSaldoMigracion
{
    [Key] public int Id { get; set; }
    [Required, MaxLength(500)] public string RazonSocialOriginal { get; set; } = "";
    [MaxLength(200)] public string? Tags { get; set; }
    [MaxLength(20)] public string? TipoDocumento { get; set; }   // DNI / CUIT
    [MaxLength(20)] public string? NroDocumento { get; set; }
    [MaxLength(10)] public string? CondicionIva { get; set; }   // CF / RI / MO / EX
    [Column(TypeName = "decimal(18,2)")] public decimal Saldo { get; set; }
    [MaxLength(5)] public string Moneda { get; set; } = "$";
    [MaxLength(20)] public string Estado { get; set; } = "pendiente"; // pendiente / asociado / ignorado
    public int? ClienteId { get; set; }
    public int? VentaId { get; set; }
    [MaxLength(500)] public string? Notas { get; set; }
    public DateTime FechaImport { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }

    [ForeignKey(nameof(ClienteId))] public CafeCliente? Cliente { get; set; }
    [ForeignKey(nameof(VentaId))] public CafeVenta? Venta { get; set; }
}
