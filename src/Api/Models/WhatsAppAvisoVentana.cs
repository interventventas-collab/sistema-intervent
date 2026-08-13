using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-08-13 (pedido del usuario): regla de "aviso de cierre de ventana de WhatsApp".
/// WhatsApp Cloud API (Meta) abre una ventana de 24hs cuando el cliente escribe; pasadas esas
/// 24hs no se le puede mandar texto libre (solo plantillas). Esta regla vigila las conversaciones
/// abiertas y, cuando falta poco para que se cierre la ventana, dispara un aviso — al equipo
/// interno (personas de la libretita) o al mismo cliente.
///
/// El robot AvisoVentanaBackgroundService las revisa cada pocos minutos.
/// Los destinatarios internos se guardan en Auto_Destinatarios con clave "waventana:{Id}"
/// (misma libretita de Personas que usa el Centro de Automatizaciones).</summary>
[Table("WhatsApp_AvisoVentana_Reglas")]
public class WhatsAppAvisoVentanaRegla
{
    public int Id { get; set; }

    [Required, MaxLength(80)] public string Nombre { get; set; } = "";

    /// <summary>Interruptor prender/apagar sin borrar la regla.</summary>
    public bool Activa { get; set; } = true;

    /// <summary>Qué línea de WhatsApp (phone_id de Meta) vigila. NULL = todas las líneas.</summary>
    [MaxLength(40)] public string? WatchLineaPhoneId { get; set; }

    /// <summary>Opcional: vigilar SOLO estos números de cliente (CSV). Vacío/NULL = todas las conversaciones.</summary>
    [MaxLength(2000)] public string? SoloNumeros { get; set; }

    /// <summary>Momentos en que avisa, en MINUTOS restantes, CSV. Ej "720,360,120,60,15" = 12h,6h,2h,1h,15min.</summary>
    [MaxLength(200)] public string UmbralesMin { get; set; } = "720,360,120,60,15";

    /// <summary>INTERNO = avisa al equipo (personas tildadas). CLIENTE = avisa al propio cliente de la charla.</summary>
    [MaxLength(10)] public string Destino { get; set; } = "INTERNO";

    /// <summary>Solo para Destino=INTERNO: desde qué línea sale el aviso al equipo. NULL = la línea por defecto.
    /// (Para Destino=CLIENTE el aviso sale por la MISMA línea de la conversación del cliente.)</summary>
    [MaxLength(40)] public string? SaleLineaPhoneId { get; set; }

    /// <summary>Texto del aviso. Admite comodines: {cliente} {tiempo} {linea}.</summary>
    [Required, MaxLength(500)] public string Mensaje { get; set; } = "";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>Registro anti-repetición: qué umbral ya se avisó para una ventana concreta de una
/// conversación. La ventana se identifica por su INICIO (el último mensaje entrante del cliente):
/// si el cliente vuelve a escribir, cambia el inicio y los avisos se re-arman solos.</summary>
[Table("WhatsApp_AvisoVentana_Enviados")]
public class WhatsAppAvisoVentanaEnviado
{
    public int Id { get; set; }
    public int ReglaId { get; set; }
    [MaxLength(40)] public string Numero { get; set; } = "";
    [MaxLength(40)] public string? LineaPhoneId { get; set; }
    /// <summary>Inicio de la ventana = CreatedAt del último mensaje entrante que la abrió.</summary>
    public DateTime VentanaInicio { get; set; }
    public int UmbralMin { get; set; }
    public DateTime EnviadoAt { get; set; } = DateTime.UtcNow;
}
