namespace Web.Models;

public class MeliShipmentDto
{
    public int Id { get; set; }
    public long MeliShipmentId { get; set; }
    public long? MeliOrderId { get; set; }
    public string? Cuenta { get; set; }
    public string? Status { get; set; }
    public string? Substatus { get; set; }
    public string InternalStatus { get; set; } = "pending";
    public string? TrackingNumber { get; set; }
    public string? ReceiverName { get; set; }
    public string? ReceiverPhone { get; set; }
    public string? BuyerNickname { get; set; }
    public string? AddressLine { get; set; }
    public string? Neighborhood { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public decimal? Latitude { get; set; }
    public decimal? Longitude { get; set; }
    public string? GeolocationType { get; set; }
    public string? Comment { get; set; }
    public string? ItemsSummary { get; set; }
    public decimal? OrderTotal { get; set; }
    public DateTime? DateCreated { get; set; }
    public DateTime? DateReadyToShip { get; set; }
    public DateTime? DateShipped { get; set; }
    public DateTime? DateDelivered { get; set; }
    public DateTime? EstimatedDeliveryFinal { get; set; }
    public DateTime? EstimatedDeliveryLimit { get; set; }
    public string? Notes { get; set; }
}

public class MeliShipmentSyncResultDto
{
    public int TotalSynced { get; set; }
    public int TotalFlex { get; set; }
    public int TotalErrors { get; set; }
    public List<string> Errores { get; set; } = new();
}

public class StartPointDto
{
    public string? Address { get; set; }
    public decimal? Lat { get; set; }
    public decimal? Lng { get; set; }
}

public class GeocodeResultDto
{
    public string DisplayName { get; set; } = "";
    public decimal Lat { get; set; }
    public decimal Lng { get; set; }
}
