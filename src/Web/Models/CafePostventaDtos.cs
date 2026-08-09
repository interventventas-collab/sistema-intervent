namespace Web.Models;

public class CafePostventaConfigDto
{
    public bool Enabled { get; set; }
    public List<string> Mensajes { get; set; } = new();
    public List<string> Opciones { get; set; } = new();
    public List<CafePostventaPublicacionDto> Publicaciones { get; set; } = new();
    public List<CafePostventaVentaDto> VentasRecientes { get; set; } = new();
}

public class CafePostventaPublicacionDto
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Thumbnail { get; set; }
    public string? Status { get; set; }
    public bool Selected { get; set; }
}

public class CafePostventaVentaDto
{
    public int OrderId { get; set; }
    public long MeliOrderId { get; set; }
    public string Buyer { get; set; } = "";
    public string Item { get; set; } = "";
    public DateTime Fecha { get; set; }
    public bool YaEnviado { get; set; }
}

public class CafePostventaProbarResultDto
{
    public bool Ok { get; set; }
    public string? Detalle { get; set; }
}

public class CafePostventaBuscarDto
{
    public List<CafePostventaPublicacionDto> Publicaciones { get; set; } = new();
}
