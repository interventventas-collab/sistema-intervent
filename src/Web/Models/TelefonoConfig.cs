namespace Web.Models;

// 2026-11-26: datos del telefono dentro del sistema (ver Api/Controllers/TelefonoController.cs)
public class TelefonoInterno
{
    public string Nombre { get; set; } = "";
    public string Numero { get; set; } = "";
    public string Grupo { get; set; } = "";
}

public class TelefonoConfig
{
    public bool Habilitado { get; set; }
    public string WsUrl { get; set; } = "";
    public string Dominio { get; set; } = "";
    public string Usuario { get; set; } = "";
    public string Clave { get; set; } = "";
    public string MiNombre { get; set; } = "";
    public List<TelefonoInterno> Internos { get; set; } = new();
}

public class TelefonoEstado
{
    public List<string> Conectados { get; set; } = new();
}

// 2026-11-26: llamadas perdidas y mensajes programados del telefono
public class TelefonoPerdida
{
    public int Id { get; set; }
    public string De { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string? Busca { get; set; }
    public DateTime FechaAr { get; set; }
}

public class TelefonoMensaje
{
    public int Id { get; set; }
    public string Texto { get; set; } = "";
    public string Destino { get; set; } = "todos";
    public string Hora { get; set; } = "09:00";
    public string Dias { get; set; } = "";
    public DateTime? Fecha { get; set; }
    public bool Activo { get; set; } = true;
    public DateTime? UltimoEnvioAr { get; set; }
    public DateTime? UltimoEscuchadoAr { get; set; }
}
