using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

/// <summary>
/// 2026-11-26: un elemento dibujado en el plano del depósito (terreno, zona, puerta, ventana,
/// vehículo, rack, etc.). Medidas en METROS: X = a lo ancho del terreno desde la pared izquierda,
/// Y = de fondo desde la calle. Ancho = tamaño sobre X, Largo = tamaño sobre Y.
/// Si la forma no es un rectángulo, Puntos trae un JSON [[x,y],...] y X/Y/Ancho/Largo son la caja
/// que la encierra.
///
/// Racks (Tipo = 'rack'): con Rotacion 0, las posiciones corren sobre Y (posición 1 = la punta más
/// cerca de la calle, Y más chica) cuando Largo ≥ Ancho; si no, corren sobre X (posición 1 = X más
/// chica). Nivel 1 = el piso. Cada estante se llama C-R-A (ver Calle1/Nro1) o, sin calle, rack-nivel (ej. "D2-3").
/// </summary>
[Table("Deposito_PlanoElementos")]
public class DepositoPlanoElemento
{
    [Key]
    public int Id { get; set; }

    [Required, MaxLength(10)]
    public string Planta { get; set; } = "PB";

    [Required, MaxLength(20)]
    public string Tipo { get; set; } = "";

    [MaxLength(10)]
    public string? Codigo { get; set; }

    [MaxLength(100)]
    public string? Nombre { get; set; }

    [Column(TypeName = "decimal(7,2)")]
    public decimal X { get; set; }
    [Column(TypeName = "decimal(7,2)")]
    public decimal Y { get; set; }
    [Column(TypeName = "decimal(7,2)")]
    public decimal Ancho { get; set; }
    [Column(TypeName = "decimal(7,2)")]
    public decimal Largo { get; set; }
    [Column(TypeName = "decimal(5,2)")]
    public decimal? Alto { get; set; }

    public int Rotacion { get; set; }
    public int? Posiciones { get; set; }
    public int? Niveles { get; set; }

    /// <summary>2026-11-27: CALLES. Cada rack tiene hasta dos lados; cada lado que da a una calle tiene
    /// calle + número de rack dentro de esa calle → el estante se llama C{calle}-R{número}-A{altura}
    /// (ej. "C3-R4-A3"). Lado 1 = el que mira a la pared izquierda (X chica) o, si el rack está girado
    /// (90/270), el que mira a la entrada (Y chica). Lado 2 = el otro. Un rack de pared usa un solo lado;
    /// uno del medio, los dos (productos de los dos lados). Sin calle = código viejo (A1-3).</summary>
    public int? Calle1 { get; set; }
    public int? Nro1 { get; set; }
    public int? Calle2 { get; set; }
    public int? Nro2 { get; set; }

    [MaxLength(9)]
    public string? Color { get; set; }

    public string? Puntos { get; set; }

    [MaxLength(300)]
    public string? Notas { get; set; }

    /// <summary>2026-11-26: cómo se ve, en JSON chico (ej. {"lx":0.5,"ly":-1,"ls":1.4} = el nombre corrido
    /// y más grande). Lo arma y lo lee la pantalla; la API solo lo guarda.</summary>
    [MaxLength(400)]
    public string? Estilo { get; set; }

    public int Orden { get; set; }
    public DateTime CreadoAt { get; set; } = DateTime.UtcNow;
    public DateTime? ModificadoAt { get; set; }

    [MaxLength(100)]
    public string? ModificadoPor { get; set; }
}
