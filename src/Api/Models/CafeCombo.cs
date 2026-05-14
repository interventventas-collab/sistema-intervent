using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Combos")]
public class CafeCombo
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(500)]
    public string? Descripcion { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public ICollection<CafeComboItem> Items { get; set; } = new List<CafeComboItem>();
}

[Table("Cafe_ComboItems")]
public class CafeComboItem
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public int ComboId { get; set; }

    [ForeignKey(nameof(ComboId))]
    public CafeCombo? ComboNav { get; set; }

    public int ProductoId { get; set; }

    [ForeignKey(nameof(ProductoId))]
    public CafeProducto? ProductoNav { get; set; }

    [Required, MaxLength(20)]
    public string Formato { get; set; } = "1KG";

    public int Cantidad { get; set; } = 1;

    [MaxLength(30)]
    public string? Molienda { get; set; }

    public bool EsDoyPack { get; set; }

    /// <summary>Si el producto del combo va en envase plateado. Default false = envase negro.</summary>
    public bool EsEnvasePlateado { get; set; }

    public int SortOrder { get; set; }
}
