using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Settings")]
public class CafeSetting
{
    [Key]
    public int Id { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal CostoFraccionamiento { get; set; } = 1000m;

    [Column(TypeName = "decimal(18,2)")]
    public decimal RedondeoMultiplo { get; set; } = 1000m;

    [Column(TypeName = "decimal(8,2)")]
    public decimal MargenOtrosBarPct { get; set; } = 40m;

    [Column(TypeName = "decimal(8,2)")]
    public decimal MargenOtrosNoBarPct { get; set; } = 60m;

    [MaxLength(200)]
    public string? NegocioNombre { get; set; }

    [MaxLength(50)]
    public string? NegocioTelefono { get; set; }

    [MaxLength(50)]
    public string? NegocioWhatsappNumero { get; set; }

    [MaxLength(300)]
    public string? NegocioDireccion { get; set; }

    [MaxLength(50)]
    public string? NegocioCuit { get; set; }

    /// <summary>Razón social legal (puede coincidir con el nombre fantasía o no).</summary>
    [MaxLength(200)]
    public string? NegocioRazonSocial { get; set; }

    /// <summary>Condición frente al IVA: "RI" (Responsable Inscripto), "MO" (Monotributo), "EX" (Exento).</summary>
    [MaxLength(50)]
    public string? NegocioCondicionIva { get; set; }

    /// <summary>Número/dato de Ingresos Brutos.</summary>
    [MaxLength(50)]
    public string? NegocioIngresosBrutos { get; set; }

    /// <summary>Fecha de inicio de actividad (para cabecera de factura).</summary>
    [System.ComponentModel.DataAnnotations.Schema.Column(TypeName = "date")]
    public DateTime? NegocioInicioActividad { get; set; }

    [MaxLength(150)]
    public string? NegocioLocalidad { get; set; }

    [MaxLength(20)]
    public string? NegocioCp { get; set; }

    [MaxLength(200)]
    public string? NegocioEmail { get; set; }

    [MaxLength(200)]
    public string? NegocioWeb { get; set; }

    /// <summary>URL del logo para mostrar en listas de precios y comprobantes. Puede ser ruta interna
    /// (ej. /api/files/download?path=branding/logo.png) o un link externo.</summary>
    [MaxLength(500)]
    public string? NegocioLogoUrl { get; set; }

    /// <summary>
    /// Texto plantilla del mensaje pre-armado para WhatsApp (lo que el cliente envía cuando toca
    /// "Contactanos por WhatsApp" en el PDF). Soporta los placeholders {numero}, {total}, {cliente}, {fecha}.
    /// </summary>
    [MaxLength(500)]
    public string? WhatsappMensajeTemplate { get; set; }

    /// <summary>
    /// Texto plantilla del mensaje pre-armado para WhatsApp DEL NEGOCIO HACIA EL CLIENTE
    /// (lo que dispara el icono 📱 al lado del teléfono del cliente — uso típico: repartidor).
    /// Soporta los mismos placeholders.
    /// </summary>
    [MaxLength(500)]
    public string? WhatsappMensajeClienteTemplate { get; set; }

    public DateTime? UpdatedAt { get; set; }
}
