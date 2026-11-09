namespace Web.Services;

/// <summary>2026-11-09: une la pantalla de armado (/cafe/preparacion) con el layout general.
/// La pantalla avisa que está abierta y si va "enorme"; el layout esconde sus barras y sus botones
/// flotantes, y le abre el chat del equipo o el WhatsApp cuando se tocan desde las pastillas.</summary>
public class PantallaDepositoState
{
    /// <summary>La pantalla de armado con pastillas está abierta.</summary>
    public bool Activa { get; private set; }
    /// <summary>Sin las barras del sistema: casi toda la pantalla es para armar.</summary>
    public bool Enorme { get; private set; }

    public event Action? Cambio;
    public event Action? PedirChatEquipo;
    public event Action? PedirWhatsApp;

    public void Entrar() { Activa = true; Cambio?.Invoke(); }
    public void Salir() { Activa = false; Enorme = false; Cambio?.Invoke(); }
    public void PonerEnorme(bool enorme) { Enorme = enorme; Cambio?.Invoke(); }

    public void AbrirChatEquipo() => PedirChatEquipo?.Invoke();
    public void AbrirWhatsApp() => PedirWhatsApp?.Invoke();
}
