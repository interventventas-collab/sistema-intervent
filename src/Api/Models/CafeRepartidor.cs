using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// Repartidor con PIN (ultimos 3 del DNI) para autenticarse en la pantalla publica
/// /repartidor/{token}. Patron tomado de HorasExtrasEmpleado. Pedido 2026-05-19.
/// </summary>
[Table("Cafe_Repartidores")]
public class CafeRepartidor
{
    public int Id { get; set; }

    [Required, MaxLength(120)]
    public string Nombre { get; set; } = "";

    /// <summary>Ultimos 3 digitos del DNI. PIN corto para login en mobile.</summary>
    [MaxLength(3)]
    public string? DniUltimos3 { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>2026-06-05: Token publico fijo para la URL personal /mis-pedidos/{token}.
    /// El repartidor lo guarda como acceso directo en su celu y entra siempre desde ahi.
    /// Solo se regenera manualmente desde admin (perdio celu, ex-empleado, etc).</summary>
    [MaxLength(64)]
    public string? PublicToken { get; set; }

    /// <summary>2026-06-25: vinculo opcional a Nom_Empleados. Si el repartidor es tambien
    /// empleado en nominas (Alexis, Walter/Nacho, etc.), apunta a su Id de Nom_Empleados.
    /// NULL = repartidor sin link a empleado de nominas. Lo usa el dashboard para mostrar
    /// fichaje + rendicion + sueldo en una sola ficha por persona.</summary>
    public int? NomEmpleadoId { get; set; }
    [ForeignKey(nameof(NomEmpleadoId))]
    public NomEmpleado? NomEmpleado { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }
}

/// <summary>
/// 2026-06-05: Sesion del repartidor en el celu. Token HTTP en localStorage.
/// Permite escanear varios QR seguidos sin re-tipear el PIN.
/// </summary>
[Table("Cafe_RepartidorSesiones")]
public class CafeRepartidorSesion
{
    public int Id { get; set; }

    public int RepartidorId { get; set; }
    [ForeignKey(nameof(RepartidorId))]
    public CafeRepartidor? Repartidor { get; set; }

    /// <summary>Token de sesion (64 chars) que el celu manda en cada llamada al escanear.</summary>
    [Required, MaxLength(80)]
    public string SessionToken { get; set; } = "";

    /// <summary>Fingerprint informativo del celu (user-agent corto + IP) para auditoria.</summary>
    [MaxLength(200)]
    public string? DeviceInfo { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    /// <summary>Cuando expira. Default = CreatedAt + 8 hs.</summary>
    public DateTime ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    /// <summary>Si el usuario hizo logout, queda en true y la sesion deja de servir.</summary>
    public bool Revoked { get; set; } = false;
}

/// <summary>
/// 2026-06-05: Log de cada escaneo de QR de venta. Permite saber quien escaneo
/// cada remito (no solo quien lo entrego) + auditoria de movimientos.
/// </summary>
[Table("Cafe_QrEscaneos")]
public class CafeQrEscaneo
{
    public int Id { get; set; }

    public int VentaId { get; set; }
    public int RepartidorId { get; set; }
    [ForeignKey(nameof(RepartidorId))]
    public CafeRepartidor? Repartidor { get; set; }

    /// <summary>Que paso al escanear: "cargado" (agregado a la lista),
    /// "entregado" (confirmo entrega), "cobrado" (registro cobro).</summary>
    [Required, MaxLength(20)]
    public string Accion { get; set; } = "cargado";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>IP del cliente (opcional, para auditoria).</summary>
    [MaxLength(64)]
    public string? Ip { get; set; }
}
