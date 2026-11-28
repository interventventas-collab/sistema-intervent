using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-11-27: comentario libre de un estante (lado, posición y altura de un rack), lo que
/// quieran anotar ("acá van las cajas abiertas", "falta un travesaño"...). Uno por estante: escribirlo
/// de nuevo lo reemplaza; vacío lo borra. Se escribe desde Recorrer estantes (celu) o desde el plano.</summary>
[Table("Deposito_EstanteNotas")]
public class DepositoEstanteNota
{
    [Key]
    public int Id { get; set; }
    public int ElementoId { get; set; }
    public int Cara { get; set; } = 1;
    public int Posicion { get; set; } = 1;
    public int Nivel { get; set; }
    [MaxLength(500)]
    public string Nota { get; set; } = "";
    public DateTime At { get; set; } = DateTime.UtcNow;
    [MaxLength(100)]
    public string? Por { get; set; }

    /// <summary>2026-11-28: qué se ve en el estante aunque no tenga stock cargado (en el 3D se dibuja eso):
    /// "bolsas" (de café crudo), "cajas", "bidones", "sillas", "pallet", "rollos", "bultos", "foto". Null = nada.</summary>
    [MaxLength(20)]
    public string? Dibujo { get; set; }
    public int? DibujoCant { get; set; }
    [MaxLength(100)]
    public string? DibujoTexto { get; set; }
    /// <summary>2026-11-28: foto del estante (archivo en /data/files/estante-fotos).</summary>
    [MaxLength(100)]
    public string? Foto { get; set; }
}
