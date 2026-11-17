using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 17/11/2026 — Un impuesto/VEP a pagar (pedido del dueño: "no colgarme de pagar los VEP").
/// Los VEP los genera la contadora (María) y los manda por mail: a veces con el PDF del VEP,
/// a veces con los montos escritos. El robot <c>ImpuestosBackgroundService</c> lee esos mails y
/// los cruza con los débitos "IMP.AFIP" del extracto del Galicia: si aparece el mismo importe,
/// queda PAGADO solo. Los débitos IMP.AFIP sin VEP conocido se anotan como Origen BANCO.
/// </summary>
[Table("Impuestos_Veps")]
public class ImpuestoVep
{
    [Key]
    public int Id { get; set; }
    [MaxLength(30)] public string? NroVep { get; set; }
    [MaxLength(13)] public string? Cuit { get; set; }
    /// <summary>A quién corresponde: SRL, SH, Gabriel, ...</summary>
    [Required, MaxLength(80)] public string Entidad { get; set; } = "SRL";
    /// <summary>IVA, Ingresos brutos, Aportes empleados, Ganancias, Honorarios contadora, Monotributo, Pago AFIP, Otro.</summary>
    [Required, MaxLength(80)] public string Concepto { get; set; } = "Otro";
    /// <summary>El renglón tal cual lo escribió la contadora, o la descripción del VEP.</summary>
    [MaxLength(300)] public string? Detalle { get; set; }
    [MaxLength(20)] public string? Periodo { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal Importe { get; set; }
    /// <summary>Día de expiración del VEP (si vino el PDF). Día argentino, sin hora.</summary>
    public DateTime? Vence { get; set; }
    /// <summary>MAIL | MANUAL | BANCO</summary>
    [Required, MaxLength(10)] public string Origen { get; set; } = "MAIL";
    [MaxLength(300)] public string? MailMessageId { get; set; }
    [MaxLength(200)] public string? MailAsunto { get; set; }
    /// <summary>Cuándo llegó el mail (o se cargó a mano), UTC.</summary>
    public DateTime? MailFecha { get; set; }
    /// <summary>PENDIENTE | PAGADO | DESCARTADO</summary>
    [Required, MaxLength(12)] public string Estado { get; set; } = "PENDIENTE";
    public DateTime? PagadoFecha { get; set; }
    [MaxLength(40)] public string? PagadoComo { get; set; }
    public int? ExtractoMovimientoId { get; set; }
    public byte[]? Pdf { get; set; }
    [MaxLength(200)] public string? PdfNombre { get; set; }
    [MaxLength(500)] public string? Nota { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
