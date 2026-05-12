namespace Web.Models;

public class MapeoDriverDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Telefono { get; set; }
    public string Color { get; set; } = "#1d4ed8";
    public bool IsActive { get; set; } = true;
    public string? ShareToken { get; set; }
}

public class PublicRouteDto
{
    public string DriverNombre { get; set; } = "";
    public string DriverColor { get; set; } = "#1d4ed8";
    public string? DriverTelefono { get; set; }
    public DateTime? Now { get; set; }
    public string? StartAddress { get; set; }
    public decimal? StartLat { get; set; }
    public decimal? StartLng { get; set; }
    public List<PublicStopDto> Stops { get; set; } = new();
}

public class PublicStopDto
{
    public int Id { get; set; }
    public int? OrderInRoute { get; set; }
    public string? Alias { get; set; }
    public string Direccion { get; set; } = "";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? ContactName { get; set; }
    public string? Telefono { get; set; }
    public string? Notas { get; set; }
    public string InternalStatus { get; set; } = "pending";
    public string? Comprador { get; set; }
    public string? NumeroVenta { get; set; }
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
    /// <summary>Localidad / ciudad de la parada — para agrupar la lista lateral por zona.</summary>
    public string? Localidad { get; set; }
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

public class MapeoSnapshotListItemDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int StopsCount { get; set; }
    public int VehiclesCount { get; set; }
    public int DriversCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByUsername { get; set; }
    public string? Notes { get; set; }
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
