namespace Web.Models;

// 2026-07-17: base propia de clientes de MercadoLibre.
public class MeliClienteDto
{
    public int Id { get; set; }
    public long BuyerId { get; set; }
    public string? Nickname { get; set; }
    public string? ReceiverName { get; set; }
    public string? Phone { get; set; }
    public string? AddressLine { get; set; }
    public string? Neighborhood { get; set; }
    public string? City { get; set; }
    public string? State { get; set; }
    public string? ZipCode { get; set; }
    public int OrdersCount { get; set; }
    public decimal TotalSpent { get; set; }
    public DateTime? FirstPurchaseAt { get; set; }
    public DateTime? LastPurchaseAt { get; set; }
    public string? LastItems { get; set; }
}

public class MeliClientesListDto
{
    public int Total { get; set; }
    public int TotalGlobal { get; set; }
    public int ConTelefono { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; }
    public List<MeliClienteDto> Items { get; set; } = new();
}

public class MeliClienteCompraDto
{
    public long MeliOrderId { get; set; }
    public DateTime? Fecha { get; set; }
    public string? Items { get; set; }
    public int Cantidad { get; set; }
    public decimal Total { get; set; }
    public string? Canal { get; set; }
}

public class MeliClientesSyncResultDto
{
    public int Procesadas { get; set; }
    public int Telefonos { get; set; }
    public int TotalClientes { get; set; }
}
