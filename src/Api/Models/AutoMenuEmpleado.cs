using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-08-03 (pedido Gabriel/Osmar/Germán): "menú interno para empleados por WhatsApp".
/// Cada fila = un empleado con SU palabra clave (que hace de usuario+clave a la vez). Cuando ese
/// empleado le escribe la palabra al WhatsApp de la empresa, el bot le contesta con un menú de
/// opciones para consultar cosas del sistema (stock, precios, pedidos del día, saldos y facturas
/// de clientes) — SOLO las opciones tildadas para esa persona. Se configura desde la pantalla
/// "🤖 Automatizaciones y Alertas".</summary>
[Table("Auto_MenuEmpleado")]
public class AutoMenuEmpleado
{
    public int Id { get; set; }

    /// <summary>La palabra clave PERSONAL (ej "1983"). Identifica a la persona. Se compara tal cual
    /// (sin distinguir mayúsculas) con lo que escribe el empleado.</summary>
    [Required, MaxLength(30)] public string Codigo { get; set; } = "";

    /// <summary>Nombre para mostrar en el saludo del menú (ej "Gabriel").</summary>
    [Required, MaxLength(80)] public string Nombre { get; set; } = "";

    // Qué puede consultar esta persona (cada opción es un botón del menú).
    public bool OpStock { get; set; } = true;
    public bool OpPrecios { get; set; } = true;
    public bool OpPedidos { get; set; } = true;
    public bool OpSaldos { get; set; } = true;
    public bool OpFacturas { get; set; } = true;

    /// <summary>Seguridad opcional: si está cargado, la palabra clave SOLO funciona desde ese número
    /// de WhatsApp (formato "whatsapp:+549..."). Si está vacío, funciona desde cualquier celular.</summary>
    [MaxLength(40)] public string? SoloDesdeNumero { get; set; }

    public bool Activo { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>2026-08-03: "memoria corta" del bot de empleados. Cuando el empleado toca una opción que
/// necesita que escriba algo (ej "Stock" → "¿qué producto?"), guardamos que ESE número está esperando
/// ESE dato. El próximo texto que mande se interpreta como la respuesta. Expira solo a los minutos.</summary>
[Table("Auto_MenuEstado")]
public class AutoMenuEstado
{
    /// <summary>Número de WhatsApp del empleado (whatsapp:+E164). Uno por número.</summary>
    [Key, MaxLength(60)] public string Numero { get; set; } = "";

    /// <summary>Palabra clave del empleado que está usando el menú (para saber sus permisos).</summary>
    [MaxLength(30)] public string Codigo { get; set; } = "";

    /// <summary>Qué dato estamos esperando: stock | precios | saldos | facturas. Vacío = nada.</summary>
    [MaxLength(20)] public string Esperando { get; set; } = "";

    public DateTime ExpiraAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
