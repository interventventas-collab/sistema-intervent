namespace Web.Models;

// 2026-11-26: datos del telefono dentro del sistema (ver Api/Controllers/TelefonoController.cs)
public class TelefonoInterno
{
    public string Nombre { get; set; } = "";
    public string Numero { get; set; } = "";
}

public class TelefonoConfig
{
    public bool Habilitado { get; set; }
    public string WsUrl { get; set; } = "";
    public string Dominio { get; set; } = "";
    public string Usuario { get; set; } = "";
    public string Clave { get; set; } = "";
    public List<TelefonoInterno> Internos { get; set; } = new();
}
