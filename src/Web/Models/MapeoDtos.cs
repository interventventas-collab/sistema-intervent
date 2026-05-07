namespace Web.Models;

public class MapeoDriverDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Telefono { get; set; }
    public string Color { get; set; } = "#1d4ed8";
    public bool IsActive { get; set; } = true;
}

public class MapeoStopDto
{
    public int Id { get; set; }
    public string Origin { get; set; } = "manual";
    public string? OriginRefId { get; set; }
    public string? Alias { get; set; }
    public string Direccion { get; set; } = "";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? ContactName { get; set; }
    public string? Telefono { get; set; }
    public string? Notas { get; set; }
    public string InternalStatus { get; set; } = "pending";
    public int? AssignedDriverId { get; set; }
    public string? AssignedDriverName { get; set; }
    public string? AssignedDriverColor { get; set; }
    public int? AssignedVehicleSlot { get; set; }
    public int? OrderInRoute { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ImportFlexPreviewDto
{
    public int Total { get; set; }
    public int YaCargados { get; set; }
    public int AImportar { get; set; }
    public List<ImportFlexSampleDto> Sample { get; set; } = new();
}

public class ImportFlexSampleDto
{
    public string? ReceiverName { get; set; }
    public string? City { get; set; }
    public string? AddressLine { get; set; }
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
