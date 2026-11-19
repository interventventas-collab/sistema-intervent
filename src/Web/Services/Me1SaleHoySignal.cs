namespace Web.Services;

/// <summary>
/// 2026-11-18: después de imprimir etiquetas de MeLi, las pantallas avisan qué envíos se imprimieron y
/// MainLayout muestra la ventanita "¿Sale hoy?" para los ME1 que todavía no tienen día de salida.
/// Así hay una sola ventana para las 5 pantallas que imprimen (Órdenes, Depósito, pastillas).
/// </summary>
public class Me1SaleHoySignal
{
    public event Action<List<long>>? OnPreguntar;
    public void Preguntar(IEnumerable<long> shipmentIds) => OnPreguntar?.Invoke(shipmentIds.Distinct().ToList());
}
