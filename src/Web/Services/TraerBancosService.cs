namespace Web.Services;

/// <summary>
/// 2026-10-29: botón "Traer movimientos" de la bienvenida (pedido de Osmar: que los robots de Shell y
/// Galicia NO entren solos, sino cuando él lo aprieta). Vive fuera de la ventanita para que, si la
/// cierra o se va a otra pantalla, la traída siga y el globito ☀ muestre cómo va.
/// Shell y Galicia corren a la vez (robots separados); Galicia hace movimientos y después cheques.
/// </summary>
public class TraerBancosService
{
    private readonly ApiClient _api;
    public TraerBancosService(ApiClient api) => _api = api;

    public bool Corriendo { get; private set; }
    public string? Shell { get; private set; }
    public string? GaliciaMovimientos { get; private set; }
    public string? GaliciaCheques { get; private set; }
    public DateTime? TerminoAt { get; private set; }
    /// <summary>Alguno de los tres terminó con error.</summary>
    public bool HuboError { get; private set; }

    public event Action? OnChange;
    private void Avisar() => OnChange?.Invoke();

    public async Task TraerTodoAsync()
    {
        if (Corriendo) return;
        Corriendo = true;
        HuboError = false;
        TerminoAt = null;
        Shell = "trayendo…";
        GaliciaMovimientos = "trayendo…";
        GaliciaCheques = "esperando a los movimientos…";
        Avisar();

        await Task.WhenAll(TraerShellAsync(), TraerGaliciaAsync());

        Corriendo = false;
        TerminoAt = DateTime.Now;
        Avisar();
    }

    private async Task TraerShellAsync()
    {
        try
        {
            var r = await _api.SincronizarShellAsync();
            if (r?.Ok == true) Shell = "listo · saldo " + r.Saldo;
            else { Shell = "no se pudo · " + (r?.Error ?? "sin respuesta"); HuboError = true; }
        }
        catch (Exception ex) { Shell = "no se pudo · " + ex.Message; HuboError = true; }
        Avisar();
    }

    private async Task TraerGaliciaAsync()
    {
        try
        {
            var r = await _api.SincronizarGaliciaAsync();
            if (r?.Ok == true) GaliciaMovimientos = $"listo · {r.Nuevos} nuevos";
            else { GaliciaMovimientos = "no se pudo · " + (r?.Error ?? "sin respuesta"); HuboError = true; }
        }
        catch (Exception ex) { GaliciaMovimientos = "no se pudo · " + ex.Message; HuboError = true; }

        GaliciaCheques = "trayendo…";
        Avisar();
        try
        {
            var r = await _api.SincronizarChequesGaliciaAsync();
            if (r?.Ok == true) GaliciaCheques = $"listo · {r.Nuevos} nuevos, {r.Actualizados} cambiaron";
            else { GaliciaCheques = "no se pudo · " + (r?.Error ?? "sin respuesta"); HuboError = true; }
        }
        catch (Exception ex) { GaliciaCheques = "no se pudo · " + ex.Message; HuboError = true; }
        Avisar();
    }
}
