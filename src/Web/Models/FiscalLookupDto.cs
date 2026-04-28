namespace Web.Models;

public class FiscalLookupDto
{
    public string Cuit { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? Address { get; set; }
    public string? IvaCondition { get; set; }
    public bool Found { get; set; }
    public string? Source { get; set; }
    public string? Error { get; set; }
}
