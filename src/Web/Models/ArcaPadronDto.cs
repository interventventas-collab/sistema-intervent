namespace Web.Models;

/// <summary>Respuesta de la consulta al padrón oficial ARCA (ws_sr_padron_a13).
/// Si Found=false, mirar Error.</summary>
public class ArcaPadronDto
{
    public bool Found { get; set; }
    public string? Cuit { get; set; }
    public string? RazonSocial { get; set; }
    /// <summary>"RI" | "MO" | "EX" | "CF" | null si no se pudo determinar.</summary>
    public string? CondicionIva { get; set; }
    public string? Direccion { get; set; }
    public string? CodPostal { get; set; }
    public string? Localidad { get; set; }
    public string? Provincia { get; set; }
    public bool EsPersonaJuridica { get; set; }
    public DateTime? InicioActividades { get; set; }
    public string? Fuente { get; set; }
    public string? Error { get; set; }
}
