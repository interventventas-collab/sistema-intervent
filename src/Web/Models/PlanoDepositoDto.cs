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
    /// <summary>2026-11-26: JSON con dónde va el nombre y de qué tamaño (ver PlanoDeposito.razor).</summary>
    public string? Estilo { get; set; }
    /// <summary>2026-11-27: CALLES. Lado 1 (pared izquierda / entrada) y lado 2 (el otro): calle + número
    /// de rack en esa calle → estante C{calle}-R{nro}-A{altura}. Vacío = ese lado no da a una calle.</summary>
    public int? Calle1 { get; set; }
    public int? Nro1 { get; set; }
    public int? Calle2 { get; set; }
    public int? Nro2 { get; set; }
    /// <summary>2026-11-28: calles de las puntas (cara 3 = lado entrada / pared izq., cara 4 = la otra).</summary>
    public int? CallePunta1 { get; set; }
    public int? CallePunta2 { get; set; }
    /// <summary>2026-11-28: lo que se dibuja en sus estantes (bolsas de café, cajas…) y si tienen foto. Solo lectura.</summary>
    public List<PlanoDibujoDto>? Dibujos { get; set; }
    /// <summary>2026-11-27: fila (línea de estanterías) y número de rack en la fila → "F4-R2".</summary>
    public int? Fila { get; set; }
    public int? NroFila { get; set; }
    /// <summary>2026-11-27: null = rack; "piso" = lugar en el piso de una calle (C3-PISO1);
    /// "estanteria" = estantería suelta con nombre corto (TOST-A3).</summary>
    public string? Forma { get; set; }
    /// <summary>2026-11-27: estantería amurada: a cuántos metros del piso empieza.</summary>
    public decimal? Elevacion { get; set; }
    public bool EsPiso => Tipo == "rack" && Forma == "piso";
    public bool EsEstanteria => Tipo == "rack" && Forma == "estanteria";

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
    public string? Estilo { get; set; }
    public int? Calle1 { get; set; }
    public int? Nro1 { get; set; }
    public int? Calle2 { get; set; }
    public int? Nro2 { get; set; }
    /// <summary>2026-11-28: calles de las puntas (cara 3 = lado entrada / pared izq., cara 4 = la otra).</summary>
    public int? CallePunta1 { get; set; }
    public int? CallePunta2 { get; set; }
    /// <summary>2026-11-27: fila (línea de estanterías) y número de rack en la fila → "F4-R2".</summary>
    public int? Fila { get; set; }
    public int? NroFila { get; set; }
    public string? Forma { get; set; }
    public decimal? Elevacion { get; set; }

    public static PlanoElementoGuardarDto De(PlanoElementoDto e) => new()
    {
        Planta = e.Planta, Tipo = e.Tipo, Codigo = e.Codigo, Nombre = e.Nombre,
        X = e.X, Y = e.Y, Ancho = e.Ancho, Largo = e.Largo, Alto = e.Alto, Rotacion = e.Rotacion,
        Posiciones = e.Posiciones, Niveles = e.Niveles, Color = e.Color, Puntos = e.Puntos,
        Notas = e.Notas, Orden = e.Orden, Estilo = e.Estilo,
        Calle1 = e.Calle1, Nro1 = e.Nro1, Calle2 = e.Calle2, Nro2 = e.Nro2, Fila = e.Fila, NroFila = e.NroFila,
        Forma = e.Forma, Elevacion = e.Elevacion, CallePunta1 = e.CallePunta1, CallePunta2 = e.CallePunta2,
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
    /// <summary>Stock total del producto (2026-11-27: se ve en la vista de frente del rack).</summary>
    public int? Stock { get; set; }
}

public class PlanoEstanteDto
{
    public int Posicion { get; set; }
    public int Nivel { get; set; }
    public string Codigo { get; set; } = "";
    public List<PlanoAsignacionDto> Productos { get; set; } = new();
    /// <summary>2026-11-27: de qué lado del rack (1 o 2) y escrito ("Calle 3 · Rack 4 · Altura 3").</summary>
    public int Cara { get; set; } = 1;
    public string? Desglose { get; set; }
    /// <summary>2026-11-27: comentario libre del estante.</summary>
    public string? Nota { get; set; }
    public string? NotaPor { get; set; }
    public DateTime? NotaAt { get; set; }
    /// <summary>2026-11-28: qué se ve en el estante aunque no tenga stock, y su foto.</summary>
    public string? Dibujo { get; set; }
    public int? DibujoCant { get; set; }
    public string? DibujoTexto { get; set; }
    public string? Foto { get; set; }
}

/// <summary>2026-11-28: dibujo de un estante (para el 3D).</summary>
public class PlanoDibujoDto
{
    public int Cara { get; set; }
    public int Posicion { get; set; }
    public int Nivel { get; set; }
    public string? Dibujo { get; set; }
    public int? Cant { get; set; }
    public string? Texto { get; set; }
    public string? Foto { get; set; }
}

/// <summary>2026-11-27: un lado del rack que da a una calle.</summary>
public class PlanoCaraDto
{
    public int Cara { get; set; }
    public int? Calle { get; set; }
    public int? Nro { get; set; }
    public string Titulo { get; set; } = "";
}

public class PlanoRackDto
{
    public PlanoElementoDto Elemento { get; set; } = new();
    public List<PlanoEstanteDto> Estantes { get; set; } = new();
    public List<PlanoCaraDto> Caras { get; set; } = new();
}

/// <summary>Un estante solo (lo que abre el QR pegado en el estante).</summary>
public class PlanoEstanteDetalleDto
{
    public int RackId { get; set; }
    public string? Planta { get; set; }
    public string? Rack { get; set; }
    public string Codigo { get; set; } = "";
    /// <summary>2026-11-27: cuántas posiciones tiene el rack (1 = el código es rack-nivel, ej. D2-3).</summary>
    public int? Posiciones { get; set; }
    public int Posicion { get; set; }
    public int Nivel { get; set; }
    /// <summary>2026-11-27: lado del rack, calle y número de rack en la calle, y escrito.</summary>
    public int Cara { get; set; } = 1;
    public int? Calle { get; set; }
    public int? Nro { get; set; }
    public string? Desglose { get; set; }
    /// <summary>2026-11-27: null = rack; "piso" / "estanteria".</summary>
    public string? Forma { get; set; }
    public int? Fila { get; set; }
    public List<PlanoAsignacionDto> Productos { get; set; } = new();
    /// <summary>2026-11-28: true = el QR era la etiqueta única del rack: elegir la altura (Alturas, de arriba a abajo).</summary>
    public bool Elegir { get; set; }
    public List<RecorridoAlturaDto> Alturas { get; set; } = new();
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
    public string? Desglose { get; set; }
}

/// <summary>2026-11-26: producto con lugar viejo ("PB · TOST") que todavía no está en ningún estante
/// (lista "Para acomodar" del plano).</summary>
public class PlanoSinEstanteDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string? Nombre { get; set; }
    public string? Planta { get; set; }
    public string? Zona { get; set; }
    public int Stock { get; set; }
}
