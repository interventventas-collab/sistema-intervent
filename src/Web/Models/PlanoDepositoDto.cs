namespace Web.Models;

// 2026-11-26: "Plano del depósito" — el dibujo del depósito (racks, puertas, muebles) y qué
// producto está en cada estante. Medidas en metros: X = a lo ancho del terreno desde la pared
// izquierda (0..10), Y = hacia el fondo desde la calle (0..42).

public class PlanoDepositoDto
{
    public string Planta { get; set; } = "PB";
    public List<PlanoElementoDto> Elementos { get; set; } = new();
}

public class PlanoElementoDto
{
    public int Id { get; set; }
    public string Planta { get; set; } = "PB";
    public string Tipo { get; set; } = "mueble";
    public string? Codigo { get; set; }
    public string? Nombre { get; set; }
    public decimal X { get; set; }
    public decimal Y { get; set; }
    public decimal Ancho { get; set; }
    public decimal Largo { get; set; }
    public decimal? Alto { get; set; }
    public int Rotacion { get; set; }
    public int? Posiciones { get; set; }
    public int? Niveles { get; set; }
    public string? Color { get; set; }
    public string? Puntos { get; set; }
    public string? Notas { get; set; }
    public int Orden { get; set; }
    public int Productos { get; set; }

    public PlanoElementoDto Copia() => (PlanoElementoDto)MemberwiseClone();
}

/// <summary>Lo que se manda al guardar (sin id ni productos).</summary>
public class PlanoElementoGuardarDto
{
    public string Planta { get; set; } = "PB";
    public string Tipo { get; set; } = "mueble";
    public string? Codigo { get; set; }
    public string? Nombre { get; set; }
    public decimal X { get; set; }
    public decimal Y { get; set; }
    public decimal Ancho { get; set; }
    public decimal Largo { get; set; }
    public decimal? Alto { get; set; }
    public int Rotacion { get; set; }
    public int? Posiciones { get; set; }
    public int? Niveles { get; set; }
    public string? Color { get; set; }
    public string? Puntos { get; set; }
    public string? Notas { get; set; }
    public int Orden { get; set; }

    public static PlanoElementoGuardarDto De(PlanoElementoDto e) => new()
    {
        Planta = e.Planta, Tipo = e.Tipo, Codigo = e.Codigo, Nombre = e.Nombre,
        X = e.X, Y = e.Y, Ancho = e.Ancho, Largo = e.Largo, Alto = e.Alto, Rotacion = e.Rotacion,
        Posiciones = e.Posiciones, Niveles = e.Niveles, Color = e.Color, Puntos = e.Puntos,
        Notas = e.Notas, Orden = e.Orden,
    };
}

public class PlanoAsignacionDto
{
    public int Id { get; set; }
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string? Nombre { get; set; }
    public string? Cantidad { get; set; }
    public string? Nota { get; set; }
    public DateTime? At { get; set; }
    public string? Por { get; set; }
}

public class PlanoEstanteDto
{
    public int Posicion { get; set; }
    public int Nivel { get; set; }
    public string Codigo { get; set; } = "";
    public List<PlanoAsignacionDto> Productos { get; set; } = new();
}

public class PlanoRackDto
{
    public PlanoElementoDto Elemento { get; set; } = new();
    public List<PlanoEstanteDto> Estantes { get; set; } = new();
}

/// <summary>Un estante solo (lo que abre el QR pegado en el estante).</summary>
public class PlanoEstanteDetalleDto
{
    public int RackId { get; set; }
    public string? Planta { get; set; }
    public string? Rack { get; set; }
    public string Codigo { get; set; } = "";
    public int Posicion { get; set; }
    public int Nivel { get; set; }
    public List<PlanoAsignacionDto> Productos { get; set; } = new();
}

public class PlanoBuscarDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string? Nombre { get; set; }
    public List<PlanoLugarDto> Lugares { get; set; } = new();
}

public class PlanoLugarDto
{
    public int AsignacionId { get; set; }
    public string Codigo { get; set; } = "";
    public int RackId { get; set; }
    public string? Planta { get; set; }
    public string? Cantidad { get; set; }
}
