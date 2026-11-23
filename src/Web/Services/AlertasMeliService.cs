namespace Web.Services;

/// <summary>2026-11-23: comparte entre la ventana del centro (AlertaMeliModal, en MainLayout) y la
/// tarjeta MercadoLibre de Inicio la lista de alertas de publicaciones pausadas con stock.</summary>
public class AlertasMeliService
{
    public List<ApiClient.AlertaPausadaDto> Alertas { get; private set; } = new();
    public event Action? OnChange;
    /// <summary>La tarjeta pide abrir la ventana: "NO_ACTIVADA" o "ACTIVADA_POCO".</summary>
    public event Action<string>? OnAbrir;

    public int SinActivar => Alertas.Count(a => a.Tipo == "NO_ACTIVADA");
    public int ConPoco => Alertas.Count(a => a.Tipo == "ACTIVADA_POCO");

    public void Poner(List<ApiClient.AlertaPausadaDto> lista) { Alertas = lista; OnChange?.Invoke(); }
    public void Abrir(string tipo) => OnAbrir?.Invoke(tipo);
}
