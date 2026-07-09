using System.ComponentModel.DataAnnotations;

namespace Api.Models;

/// <summary>
/// 2026-07-09: Configuración de la casilla de correo (IMAP) donde llegan las facturas de proveedores.
/// El robot la lee, baja los PDF adjuntos y los deja en "facturas recibidas" para el match por QR.
/// Una sola fila (Id=1). La clave se guarda en la base (igual que las cuentas de ARCA) y la API nunca la devuelve.
/// </summary>
public class ConfigCorreoFacturas
{
    public int Id { get; set; }
    [MaxLength(120)] public string Host { get; set; } = "imap.gmail.com";
    public int Port { get; set; } = 993;
    [MaxLength(200)] public string Usuario { get; set; } = string.Empty;
    [MaxLength(300)] public string? Password { get; set; }
    /// <summary>Etiqueta/carpeta a leer (ej. "Facturas"). Vacío = bandeja de entrada.</summary>
    [MaxLength(120)] public string? Carpeta { get; set; }
    public bool Activo { get; set; } = true;
    public DateTime? UltimaCorrida { get; set; }
    public DateTime ActualizadoEn { get; set; } = DateTime.UtcNow;
}
