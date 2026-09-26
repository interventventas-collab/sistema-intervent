namespace Web.Models;

/// <summary>2026-09-26: redirigida cargada por WhatsApp ("redi"), esperando que la vuelquen.</summary>
public class RediPendienteDto
{
    public int Id { get; set; }
    public string Estado { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public decimal Importe { get; set; }
    public string? EnviadoPor { get; set; }
    public int? ClienteId { get; set; }
    public string? ClienteNombre { get; set; }
    public string? ClienteTexto { get; set; }
    /// <summary>EMPLEADO | PROVEEDOR | PRIVADA | TEXTO</summary>
    public string? RecibeTipo { get; set; }
    public int? EmpleadoId { get; set; }
    public string? EmpleadoNombre { get; set; }
    public int? ProveedorId { get; set; }
    public string? ProveedorNombre { get; set; }
    public string? RecibeTexto { get; set; }
    /// <summary>viajes | sueldo (si la recibe un empleado)</summary>
    public string? Destino { get; set; }
    public int? CobranzaCreadaId { get; set; }
    public string? CobranzaNumero { get; set; }
    public string? RechazadaMotivo { get; set; }
    public string? RevisadaPor { get; set; }
    public DateTime? RevisadaAt { get; set; }
    public List<RediAdjuntoDto> Adjuntos { get; set; } = new();
    /// <summary>2026-09-26: lo que escribieron por WhatsApp (quién la mandó, a quién le llegó).</summary>
    public string? Mensaje { get; set; }

    /// <summary>Cómo se lee "quién la recibe" en una línea.</summary>
    public string RecibeLeible => RecibeTipo switch
    {
        null => "se elige al volcar",
        "EMPLEADO" => $"{EmpleadoNombre} · {(Destino == "viajes" ? "de los viajes" : "del sueldo")}",
        "PROVEEDOR" => $"{ProveedorNombre} (proveedor)",
        "PRIVADA" => "queda en la privada",
        _ => $"«{RecibeTexto}» (elegir al volcar)"
    };
}

public class RediAdjuntoDto
{
    public int Id { get; set; }
    public string NombreOriginal { get; set; } = "";
    public string? MimeType { get; set; }
    public string Url => $"/api/cafe/redirigidas-pendientes/adjuntos/{Id}/archivo";
    public bool EsImagen => MimeType?.StartsWith("image/") == true;
}
