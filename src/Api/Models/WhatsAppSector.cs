using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("WhatsApp_Sectores")]
public class WhatsAppSector
{
    public int Id { get; set; }
    [MaxLength(80)] public string Nombre { get; set; } = "";
    [MaxLength(10)] public string? Emoji { get; set; }
    [MaxLength(300)] public string? Descripcion { get; set; }
    public int Orden { get; set; }
    public bool Activo { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("WhatsApp_SectorOperarios")]
public class WhatsAppSectorOperario
{
    public int Id { get; set; }
    public int SectorId { get; set; }
    public int UsuarioId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
