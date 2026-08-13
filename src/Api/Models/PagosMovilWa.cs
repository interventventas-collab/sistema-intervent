using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>2026-08-13 (pedido del usuario): "cargar un pago escribiendo PAGO por WhatsApp".
/// Como es PLATA, solo pueden hacerlo números autorizados. Cada fila habilita un número de WhatsApp
/// y lo ata a un usuario del sistema (el pago pendiente se crea a nombre de ESE usuario, igual que
/// si lo hubiera precargado desde la pantalla "Pagos desde el móvil"). Se administra desde la
/// pantalla de Pagos.</summary>
[Table("PagosMovil_WaAutorizados")]
public class PagosMovilWaAutorizado
{
    public int Id { get; set; }

    /// <summary>Número de WhatsApp habilitado (formato "whatsapp:+549..."). Único.</summary>
    [Required, MaxLength(60)] public string Numero { get; set; } = "";

    /// <summary>Nombre para mostrar (ej "Osmar"). Solo referencia visual.</summary>
    [Required, MaxLength(80)] public string Nombre { get; set; } = "";

    /// <summary>Usuario del sistema a cuyo nombre queda el pago pendiente cargado por este número.</summary>
    public int UserId { get; set; }
    public User? User { get; set; }

    public bool Activo { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>2026-08-13: "memoria corta" del asistente de PAGO por WhatsApp. Cargar un pago lleva
/// varios pasos (tipo → a quién → concepto/factura → monto → medio) y hay que recordar lo que se
/// fue eligiendo entre mensaje y mensaje. Una fila por número. Expira sola a los minutos.</summary>
[Table("PagosMovil_WaEstado")]
public class PagosMovilWaEstado
{
    /// <summary>Número de WhatsApp (whatsapp:+E164). Uno por número.</summary>
    [Key, MaxLength(60)] public string Numero { get; set; } = "";

    /// <summary>En qué paso del asistente está. Ver WhatsAppPagoBotService (tipo | emp_concepto |
    /// emp_concepto_texto | emp_monto | emp_medio | prov_factura | prov_monto | prov_medio).</summary>
    [MaxLength(30)] public string Paso { get; set; } = "";

    /// <summary>empleado | proveedor.</summary>
    [MaxLength(15)] public string? Tipo { get; set; }

    public int? EmpleadoId { get; set; }
    public int? ProveedorId { get; set; }

    /// <summary>Factura (Cafe_Compras.Id) que se está pagando, cuando Tipo=proveedor.</summary>
    public int? CompraId { get; set; }

    /// <summary>Saldo de esa factura (para ofrecer "TODO" y validar el monto).</summary>
    public decimal? CompraSaldo { get; set; }

    [MaxLength(60)] public string? Concepto { get; set; }
    public decimal? Monto { get; set; }

    public DateTime ExpiraAt { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
