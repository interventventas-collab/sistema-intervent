using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("WhatsApp_TwilioRespuestasRapidas")]
public class WhatsAppTwilioRespuestaRapida
{
    public int Id { get; set; }
    [MaxLength(80)] public string Nombre { get; set; } = "";
    public string Texto { get; set; } = "";
    public int Orden { get; set; }
    public bool Activo { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("WhatsApp_TwilioContactos")]
public class WhatsAppTwilioContacto
{
    public int Id { get; set; }
    [MaxLength(30)] public string Numero { get; set; } = "";
    [MaxLength(120)] public string Nombre { get; set; } = "";
    [MaxLength(20)] public string Rol { get; set; } = "otro";
    public string? Notas { get; set; }
    public bool Activo { get; set; } = true;
    public int? ClienteId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("WhatsApp_TwilioReacciones")]
public class WhatsAppTwilioReaccion
{
    public int Id { get; set; }
    public int MensajeId { get; set; }
    [MaxLength(20)] public string Emoji { get; set; } = "";
    public int? UsuarioId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// 2026-07-29: Datos bancarios / CBUs guardados para pasarle a los clientes desde el chat.
// Mismo mecanismo que las respuestas rapidas: se cargan una vez y aparecen como botones.
[Table("WhatsApp_TwilioDatosBancarios")]
public class WhatsAppTwilioDatoBancario
{
    public int Id { get; set; }
    [MaxLength(80)] public string Nombre { get; set; } = "";       // etiqueta interna, ej "Galicia PALANICA"
    [MaxLength(120)] public string Banco { get; set; } = "";       // ej "Banco Galicia"
    [MaxLength(40)] public string TipoCuenta { get; set; } = "";   // ej "cuenta corriente" / "caja de ahorro"
    [MaxLength(160)] public string Titular { get; set; } = "";     // ej "PALANICA HERMANOS SRL"
    [MaxLength(20)] public string Cuit { get; set; } = "";         // ej "30-71721214-9"
    [MaxLength(30)] public string Cbu { get; set; } = "";          // ej "0070350320000004290126"
    [MaxLength(60)] public string Alias { get; set; } = "";        // ej "Tiendafrikaf.galicia"
    [MaxLength(120)] public string Mail { get; set; } = "";        // adonde mandan el comprobante
    public int Orden { get; set; }
    public bool Activo { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
