namespace Web.Services;

/// <summary>
/// Coordina el atajo "+ Nueva Venta" del topbar con la pantalla CafeVentas.
/// Antes usabamos un query string (?nueva=timestamp) pero Blazor a veces no
/// dispara LocationChanged cuando el path no cambia — el modal no abria.
/// Ahora el boton emite un evento y la pantalla, si esta montada, lo escucha
/// directamente. Si no esta montada, MainLayout cae al fallback de navegar.
/// </summary>
public class NuevaVentaSignal
{
    public event Action? OnRequest;
    public void Request() => OnRequest?.Invoke();
}
