using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// "Camino al picking" — escalon 1 (2026-08-02).
/// Una lista de PICKING es una foto CONGELADA de los productos sumados de varios pedidos
/// que el armador tildo en /cafe/preparacion y decidio "llevar al celu". Una vez creada,
/// sus items NO se recalculan: quedan fijos para ir tildando lo que ya se junto en el deposito.
/// Se accede sin login por un Token (QR), pero SOLO desde el celular habilitado del deposito
/// (ver <see cref="CafePickingDispositivo"/>).
/// </summary>
[Table("Cafe_PickingLista")]
public class CafePickingLista
{
    public int Id { get; set; }

    /// <summary>Token publico (Guid "N") que va en el QR/URL: /picking/{token}. Sin login.</summary>
    [Required, MaxLength(64)]
    public string Token { get; set; } = "";

    /// <summary>Operador que genero la lista (informativo).</summary>
    [MaxLength(120)]
    public string? CreadaPor { get; set; }

    /// <summary>Numeros de comprobante incluidos, separados por coma (ej "CAFE-2026-1254, CAFE-2026-1255"). Informativo.</summary>
    [MaxLength(2000)]
    public string? VentaNumeros { get; set; }

    public int CantidadPedidos { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey(nameof(CafePickingItem.PickingListaId))]
    public List<CafePickingItem> Items { get; set; } = new();
}

/// <summary>
/// Renglon CONGELADO de una lista de picking: un producto ya sumado (nombre + formato + molienda),
/// con la cantidad total a juntar y un tilde para marcar que ya se agarro del deposito.
/// </summary>
[Table("Cafe_PickingItem")]
public class CafePickingItem
{
    public int Id { get; set; }

    public int PickingListaId { get; set; }

    public int Orden { get; set; }

    [Required, MaxLength(300)]
    public string ProductoNombre { get; set; } = "";

    [MaxLength(50)]
    public string? Formato { get; set; }

    [MaxLength(50)]
    public string? Molienda { get; set; }

    [MaxLength(100)]
    public string? Sku { get; set; }

    [MaxLength(50)]
    public string? Categoria { get; set; }

    public int Cantidad { get; set; }

    /// <summary>Se marca en TRUE cuando el armador ya junto este producto en el deposito.</summary>
    public bool Tildado { get; set; }

    public DateTime? TildadoAt { get; set; }
}

/// <summary>
/// El celular del deposito habilitado para armar picking. Regla del usuario (2026-08-02):
/// que NO usen sus telefonos personales — solo un aparato dedicado. El primero que se
/// "habilita" queda registrado; el resto de los celulares reciben "no habilitado".
/// Si cambian de celu, se "olvida" desde /cafe/preparacion y se habilita el nuevo.
/// </summary>
[Table("Cafe_PickingDispositivo")]
public class CafePickingDispositivo
{
    public int Id { get; set; }

    /// <summary>Identificador generado en el navegador del celu (Guid) y guardado en su localStorage.</summary>
    [Required, MaxLength(80)]
    public string DeviceId { get; set; } = "";

    /// <summary>Nombre amigable derivado del navegador (ej "Android · Chrome") para reconocerlo.</summary>
    [MaxLength(160)]
    public string? Nombre { get; set; }

    public bool Activo { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastSeenAt { get; set; }
}
