using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Cheques importados desde el archivo del banco (Galicia / BBVA / etc.). A diferencia
/// de CafeCheque (que son los que el usuario carga manualmente al hacer cobranzas), esta
/// tabla guarda el LISTADO COMPLETO de cheques tal cual el banco lo devuelve.
///
/// Tipos:
///   - RECIBIDO: cheque que un cliente le dio a Palanica (Palanica es el beneficiario actual)
///   - EMITIDO: cheque que Palanica firmo contra su cuenta para pagar a un proveedor
///   - ENDOSADO: cheque que Palanica recibio de un cliente y endoso a un proveedor
///
/// Identidad: `IdBanco` (el "ID del cheque" que devuelve el banco) es la clave unica para
/// detectar duplicados al re-importar el mismo archivo. Asi el import es idempotente.
///
/// Pedido del usuario 2026-05-19.
/// </summary>
[Table("Cafe_ChequesBanco")]
public class CafeChequeBanco
{
    public int Id { get; set; }

    [Required, MaxLength(50)]
    public string IdBanco { get; set; } = "";

    /// <summary>RECIBIDO | EMITIDO | ENDOSADO</summary>
    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "EMITIDO";

    [Required, MaxLength(50)]
    public string Numero { get; set; } = "";

    [MaxLength(50)]
    public string? Cmc7 { get; set; }

    [MaxLength(50)]
    public string? Clausula { get; set; }

    [MaxLength(200)]
    public string? BancoEmisor { get; set; }

    /// <summary>FK opcional al catalogo Cafe_Bancos. BancoEmisor queda como nombre original
    /// del extracto bancario; BancoId apunta al banco normalizado.</summary>
    public int? BancoId { get; set; }
    [ForeignKey(nameof(BancoId))]
    public CafeBanco? BancoNav { get; set; }

    [Column(TypeName = "date")]
    public DateTime? FechaEmision { get; set; }

    [Column(TypeName = "date")]
    public DateTime? FechaPago { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Importe { get; set; }

    /// <summary>Pagado | Aceptado | Disponible | Endosado | Rechazado (segun lo que diga el banco)</summary>
    [Required, MaxLength(30)]
    public string Estado { get; set; } = "";

    [MaxLength(500)]
    public string? Motivo { get; set; }

    [MaxLength(50)]
    public string? CuentaLibradora { get; set; }

    [MaxLength(50)]
    public string? CbuDeposito { get; set; }

    // Librador: quien firmo originalmente el cheque
    [MaxLength(200)]
    public string? LibradorNombre { get; set; }

    [MaxLength(20)]
    public string? LibradorCuit { get; set; }

    // Beneficiario actual: quien lo tiene en este momento
    [MaxLength(200)]
    public string? BeneficiarioActualNombre { get; set; }

    [MaxLength(20)]
    public string? BeneficiarioActualCuit { get; set; }

    // Contraparte: segun tipo es "Emitido a" / "Recibido de" / "Enviado a"
    [MaxLength(200)]
    public string? ContraparteNombre { get; set; }

    [MaxLength(20)]
    public string? ContraparteCuit { get; set; }

    public int CantidadEndosos { get; set; }
    public int CantidadCesiones { get; set; }
    public int CantidadAvales { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
