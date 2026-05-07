namespace Web.Models;

public class MapeoDriverDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Telefono { get; set; }
    public string Color { get; set; } = "#1d4ed8";
    public bool IsActive { get; set; } = true;
}

public class MapeoFavoritoDto
{
    public int Id { get; set; }
    public string Alias { get; set; } = "";
    public string Direccion { get; set; } = "";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? ContactName { get; set; }
    public string? Telefono { get; set; }
    public string? Notas { get; set; }
    public bool IsActive { get; set; } = true;
}
