using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-08-20: un mismo teléfono de WhatsApp puede tener VARIOS clientes del sistema
/// colgados. Nació porque hay clientas que manejan 3 razones sociales (ej. ACOYTE 500 S.R.L.,
/// AVENIDA LA PLATA 502 SRL y MOLLYS) y factura a las tres desde el mismo WhatsApp.
///
/// Antes el vínculo era uno solo (WhatsApp_TwilioContactos.ClienteId). Esa columna sigue viva
/// como el "principal" (lo que se usa si el operador no tildó nada, y lo que leen las pantallas
/// viejas); esta tabla es la lista completa.</summary>
[Table("WhatsApp_ContactoClientes")]
public class WhatsAppContactoCliente
{
    public int Id { get; set; }
    [MaxLength(30)] public string Numero { get; set; } = "";
    public int ClienteId { get; set; }
    /// <summary>Para que salgan siempre en el mismo orden en el chat.</summary>
    public int Orden { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>2026-08-20: con cuál de las razones sociales está trabajando CADA operador en cada
/// chat. Es de cada uno a propósito: Osmar puede estar tildado en ACOYTE 500 y Germán en
/// AVENIDA LA PLATA 502 en la misma charla y al mismo tiempo, sin pisarse.
///
/// Quien = la firma del operador (OSMAR/GERMAN/GABRIEL, del PIN) o "user:{id}" si no hay PIN.
/// Se guarda en la base y no en el navegador para que el tilde lo siga también desde el celu.
/// Sin fila = usa el principal del contacto.</summary>
[Table("WhatsApp_ClienteElegido")]
public class WhatsAppClienteElegido
{
    public int Id { get; set; }
    [MaxLength(30)] public string Numero { get; set; } = "";
    [MaxLength(40)] public string Quien { get; set; } = "";
    public int ClienteId { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
