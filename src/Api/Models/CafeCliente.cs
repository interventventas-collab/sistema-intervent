using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Clientes")]
public class CafeCliente
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    /// <summary>Código secuencial autogenerado (formato '0001'). Único, sirve para buscar rápido.</summary>
    [MaxLength(20)]
    public string? Codigo { get; set; }

    /// <summary>Nombre fantasía o nombre comercial del cliente. Es el campo "Nombre" original — se usa para mostrar.</summary>
    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    /// <summary>Razón social (nombre legal) — se usa cuando se emite factura.</summary>
    [MaxLength(200)]
    public string? RazonSocial { get; set; }

    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "OTRO"; // BAR | OTRO

    [MaxLength(20)]
    public string? Cuit { get; set; }

    [MaxLength(50)]
    public string? Telefono { get; set; }

    [MaxLength(255)]
    public string? Email { get; set; }

    [MaxLength(300)]
    public string? Direccion { get; set; }

    [MaxLength(150)]
    public string? Localidad { get; set; }

    [MaxLength(150)]
    public string? Ciudad { get; set; }

    [MaxLength(20)]
    public string? Cp { get; set; }

    /// <summary>Condición IVA por default del cliente (CF, RI, MO, EX). Se usa al crear venta si no se especifica.</summary>
    [MaxLength(20)]
    public string? CondicionIvaDefault { get; set; }

    /// <summary>Domicilio donde se entregan los pedidos (puede ser distinto del Direccion fiscal).</summary>
    [MaxLength(500)]
    public string? DomicilioEntrega { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    /// <summary>Comentarios que se imprimen en TODOS los comprobantes de este cliente
    /// (ej: "Entregar antes de las 11 hs", "Solicitar firma del recepcionista").</summary>
    public string? ComentariosComprobante { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Si true, en /cafe/preparacion las cards de este cliente muestran un boton
    /// "mini impresora" para imprimir el ticket rapido. Pensado para clientes mayoristas
    /// que reciben muchas ventas seguidas. Pedido 2026-05-28.</summary>
    public bool TieneMiniImpresora { get; set; } = false;

    /// <summary>Código interno correlativo asignado por el operador (botón en la ficha).
    /// NO confundir con `Codigo` (que es texto libre). Este es un número único para uso interno
    /// del operador: identificar clientes en mapeo, reportes, integraciones externas, etc.</summary>
    public int? CodigoInterno { get; set; }

    /// <summary>Enlace corto de Google Maps a la ubicación del cliente (formato https://maps.app.goo.gl/...).
    /// Si está cargado, el cliente aparece en la página "Clientes Mapeados".</summary>
    [MaxLength(500)]
    public string? MapeoLink { get; set; }

    /// <summary>Latitud extraída del MapeoLink (cuando se resuelve el redirect de Google Maps).
    /// Si está cargada, el cliente puede aparecer como pin en el mapa Leaflet del Mapeo —
    /// independiente de si Google cambia el formato del link en el futuro.</summary>
    [Column(TypeName = "decimal(10,7)")]
    public decimal? MapeoLat { get; set; }

    /// <summary>Longitud extraída del MapeoLink.</summary>
    [Column(TypeName = "decimal(10,7)")]
    public decimal? MapeoLng { get; set; }

    /// <summary>2026-06-22: si esta en true, cada venta que se cree para este cliente automaticamente
    /// se marca con SolicitarFirmaEntrega=true, asi cuando el repartidor entrega le pide firma y nombre.
    /// El operador puede destildarlo manualmente en cada venta si en ese caso particular no se necesita.</summary>
    public bool SolicitarFirmaEntrega { get; set; } = false;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
