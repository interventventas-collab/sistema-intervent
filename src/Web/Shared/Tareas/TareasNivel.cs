namespace Web.Shared.Tareas;

/// <summary>2026-11-03: cuánto se aleja lo contado de lo que decía el sistema. Mismos márgenes que
/// DepositoTareasController.Nivel: OK = igual; REVISA = hasta 2 unidades o hasta el 10%;
/// MUCHA = más del 10% y más de 2 unidades (rojo y parpadeando).</summary>
public static class TareasNivel
{
    public static string Nivel(int antes, int ahora)
    {
        var dif = Math.Abs(ahora - antes);
        if (dif == 0) return "OK";
        if (dif <= 2 || dif <= 0.10m * Math.Max(Math.Abs(antes), 1)) return "REVISA";
        return "MUCHA";
    }

    /// <summary>Estilos compartidos: verde (coincide), naranja (revisá), rojo que parpadea (mucha).</summary>
    public const string Css = @"
.tn-OK { color:#15803d; font-weight:700; }
.tn-REVISA { color:#c2410c; font-weight:700; }
.tn-MUCHA { color:#b91c1c; font-weight:800; border-radius:4px; padding:0 0.2rem; animation: tn-parpadeo 0.9s ease-in-out infinite alternate; }
@keyframes tn-parpadeo { from { background:#fee2e2; } to { background:transparent; } }
.tn-alerta { border:2px solid #b91c1c; background:#fef2f2; border-radius:10px; padding:0.7rem 0.8rem; }";

    public static string Texto(string nivel, int antes, int ahora) => nivel switch
    {
        "OK" => $"✓ Coincide ({ahora})",
        "REVISA" => $"Revisá: decía {antes}, ahora {ahora}",
        _ => $"⚠ Mucha diferencia: decía {antes}, ahora {ahora}"
    };
}
