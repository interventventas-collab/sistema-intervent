using System.ComponentModel.DataAnnotations;

namespace Api.Models;

/// <summary>
/// 2026-07-09: Retenciones y percepciones de IVA sufridas en un mes, por empresa (CUIT).
/// Es lo que te retienen/perciben de IVA al cobrar (MercadoPago, bancos, agentes de retención).
/// Se resta del saldo técnico para llegar al IVA "a pagar" real, igual que el F2051 de AFIP.
/// Una fila por (empresa, año, mes). Se carga a mano o, mas adelante, del scraper de "Mis Retenciones".
/// </summary>
public class ContadoraRetencion
{
    public int Id { get; set; }

    [MaxLength(20)]
    public string EmpresaCuit { get; set; } = string.Empty;
    public int Anio { get; set; }
    public int Mes { get; set; }

    /// <summary>Total de retenciones + percepciones de IVA sufridas en el mes.</summary>
    public decimal Monto { get; set; }

    [MaxLength(300)]
    public string? Nota { get; set; }
    public DateTime ActualizadoEn { get; set; } = DateTime.UtcNow;
}
