namespace Web.Models;

public class ClientDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Cuit { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? ContactName { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? CustomerTierId { get; set; }
    public string? CustomerTierName { get; set; }
}

public class CreateClientRequest
{
    public string? Code { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Cuit { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? ContactName { get; set; }
    public string? Notes { get; set; }
    public int? CustomerTierId { get; set; }
}

public class UpdateClientRequest
{
    public string? Code { get; set; }
    public string? Name { get; set; }
    public string? Cuit { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? ContactName { get; set; }
    public string? Notes { get; set; }
    public bool? IsActive { get; set; }
    public int? CustomerTierId { get; set; }
}
