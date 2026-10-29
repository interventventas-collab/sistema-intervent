namespace Web.Models;

// 2026-10-29: ventanita de bienvenida (ver BienvenidaController en la API).
public class BienvenidaDto
{
    public bool AbrirSola { get; set; }
    public List<BienvenidaBloqueDto> Bloques { get; set; } = new();
}

public class BienvenidaBloqueDto
{
    public string Clave { get; set; } = "";
    public string Titulo { get; set; } = "";
    /// <summary>"renglones" | "recordatorios" | "traer-bancos"</summary>
    public string Tipo { get; set; } = "renglones";
    public List<BienvenidaRenglonDto> Renglones { get; set; } = new();
}

public class BienvenidaRenglonDto
{
    public string Texto { get; set; } = "";
    public string? Detalle { get; set; }
    public bool Alarma { get; set; }
    public string? Link { get; set; }
}

public class BienvenidaRecordatorioDto
{
    public int Id { get; set; }
    public string Texto { get; set; } = "";
    public DateTime? Fecha { get; set; }
    public string? Para { get; set; }
    public string De { get; set; } = "";
    public bool Hecho { get; set; }
}
