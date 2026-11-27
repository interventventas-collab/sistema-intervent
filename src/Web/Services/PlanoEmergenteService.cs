namespace Web.Services;

/// <summary>2026-11-27: abre el planito emergente (dónde está un estante) desde cualquier pantalla.
/// El planito se dibuja una sola vez en el layout (PlanoEmergenteHost), arriba de toda la página:
/// si se dibujara adentro de una tabla, una tabla con "contain" (ej. Productos, para que no se trabe)
/// lo deja encerrado y se abre fuera de la pantalla.</summary>
public class PlanoEmergenteService
{
    public string? Codigo { get; private set; }
    /// <summary>true cuando el layout tiene el lugar donde se dibuja el planito.</summary>
    public bool HayHost { get; set; }
    public event Action? Cambio;

    public void Abrir(string codigo) { Codigo = codigo; Cambio?.Invoke(); }
    public void Cerrar() { Codigo = null; Cambio?.Invoke(); }
}
