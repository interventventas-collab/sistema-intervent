using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Alertas configurables por el usuario (motor de reglas). Cada fila es una regla que el
/// usuario arma desde la pantalla "Mis Alertas": "SI pasa esto → avisame con este mensaje
/// → por este canal". Un robot (MisAlertasBackgroundService) las revisa cada pocos minutos
/// y las "dispara" cuando se cumple la condicion.
///
/// Tipos (campo Tipo):
///   - SHELL_BAJO   : el saldo de Shell Flota bajo de {Umbral} pesos.
///   - BANCO_BAJO   : el saldo del Banco Galicia bajo de {Umbral} pesos.
///   - CHEQUE_VENCE : hay un cheque EMITIDO (por pagar) que vence en {Umbral} dias o menos.
///   - FECHA_MES    : es el dia {Umbral} de cada mes (recordatorio: contadora, impuestos, etc).
///   - EMAIL_REMITENTE : entró un correo NO leído de {TextoParam} a la casilla vigilada.
///
/// El campo Umbral es multiuso segun el Tipo: monto (Shell/Banco), cantidad de dias (cheque)
/// o numero de dia del mes 1..31 (fecha).
///
/// "Avisar una sola vez": mientras la condicion se mantiene, EstaDisparada queda en true y no
/// se vuelve a notificar. Cuando la condicion deja de cumplirse, se resetea (EstaDisparada=false,
/// Vista=false), asi la proxima vez que se cumpla vuelve a avisar.
///
/// Pedido del usuario 2026-07-10.
/// </summary>
[Table("Mis_Alertas")]
public class MisAlerta
{
    public int Id { get; set; }

    /// <summary>Usuario dueño de la alerta (la creo el / la ve el).</summary>
    public int UserId { get; set; }

    /// <summary>SHELL_BAJO | BANCO_BAJO | CHEQUE_VENCE | FECHA_MES</summary>
    [Required, MaxLength(30)]
    public string Tipo { get; set; } = "";

    /// <summary>Multiuso segun Tipo: monto, cantidad de dias, o dia del mes.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? Umbral { get; set; }

    /// <summary>Parametro de texto para tipos que no usan numero. Hoy: EMAIL_REMITENTE guarda
    /// aca el remitente a vigilar (ej "contadora@estudio.com" o "@estudio.com").</summary>
    [MaxLength(300)]
    public string? TextoParam { get; set; }

    [Required, MaxLength(300)]
    public string Mensaje { get; set; } = "";

    // --- Canales de aviso ---
    public bool CanalCampanita { get; set; } = true;
    public bool CanalWhatsApp { get; set; } = false;
    public bool CanalCorreo { get; set; } = false;
    /// <summary>2026-07-10: además de la campanita, mandar esta alerta al Telegram del dueño
    /// cuando salte. Se elige por-alerta desde la pantalla Mis Alertas.</summary>
    public bool CanalTelegram { get; set; } = false;

    /// <summary>Interruptor prender/apagar sin borrar la regla.</summary>
    public bool Activa { get; set; } = true;

    /// <summary>Quién ve la alerta: lista de roles separada por coma (admin, oficina, deposito).
    /// Las alertas son COMPARTIDAS entre los roles marcados (no son por-usuario). Default:
    /// "admin,oficina" (depósito queda afuera por ahora, pero el selector ya lo permite).</summary>
    [MaxLength(100)]
    public string Alcance { get; set; } = "admin,oficina";

    // --- Estado de disparo (lo maneja el robot) ---
    /// <summary>La condicion se esta cumpliendo ahora mismo.</summary>
    public bool EstaDisparada { get; set; } = false;
    /// <summary>El usuario ya vio esta notificacion (baja el contador de la campanita).</summary>
    public bool Vista { get; set; } = false;
    public DateTime? DisparadaAt { get; set; }
    /// <summary>Detalle a mostrar en la campanita (ej: "Shell: $32.000" o "2 cheques por $80.000").</summary>
    [MaxLength(300)]
    public string? UltimoDetalle { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}
