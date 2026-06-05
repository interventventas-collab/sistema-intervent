using Microsoft.JSInterop;

namespace Web.Services;

/// <summary>
/// Mantiene quien es el operador (persona fisica) que esta trabajando ahora.
/// Lo guarda en localStorage. El nombre se manda en cada request al backend
/// como header X-Operator-Name para auditoria.
/// </summary>
public class OperatorService
{
    private const string StorageKey = "current_operator";
    private readonly IJSRuntime _js;
    private string? _current;

    // 2026-05-28: MAXI eliminado (ya no trabaja). Lista global = todos los que SÍ trabajan.
    public static readonly string[] Operators =
    {
        "OSMAR", "GERMAN", "GABRIEL", "MIGUEL", "ALEXIS", "WALTER", "RODRIGO"
    };

    // Operadores por rol (para mostrar solo los del equipo correspondiente en el modal).
    // 2026-06-05: OSMAR salio de OperatorsOficina y entro a Protected — desde oficina aparece
    // al final con candado y pide clave (la misma de eliminar ventas). Asi evitamos que en
    // oficina dejen OSMAR seleccionado por error y las ventas terminen en su tablero.
    public static readonly string[] OperatorsOficina = { "GERMAN", "GABRIEL" };
    public static readonly string[] OperatorsDeposito = { "ALEXIS", "WALTER", "RODRIGO" };

    /// <summary>2026-06-05: Operadores que requieren clave (PIN) para activarlos.
    /// Aparecen al final del modal con candado. Usa la misma clave de eliminar ventas
    /// (sales.delete_password). Endpoint: POST /api/cafe/ventas/operador-protegido/validar</summary>
    public static readonly string[] ProtectedOperators = { "OSMAR" };

    /// <summary>Devuelve la lista de operadores que el usuario logueado puede elegir, en base
    /// a sus permisos. 2026-06-05: admin (con "cafe") trabaja como oficina (GERMAN/GABRIEL) y
    /// puede activar OSMAR via clave (ProtectedForPermissions). DEPOSITO ve los suyos.</summary>
    public static string[] ForPermissions(List<string> perms)
    {
        // 2026-06-05: admin/oficina ven solo GERMAN+GABRIEL como normales.
        // OSMAR aparece via ProtectedForPermissions (con candado).
        if (perms.Contains("cafe") || perms.Contains("oficina")) return OperatorsOficina;
        if (perms.Contains("deposito")) return OperatorsDeposito;
        return OperatorsOficina;                              // fallback minimo
    }

    /// <summary>2026-06-05: Devuelve los operadores PROTEGIDOS (con candado) que el usuario
    /// puede elegir si tipea la clave. Para admin/oficina/deposito: OSMAR siempre disponible
    /// con clave (la misma de eliminar comprobantes).</summary>
    public static string[] ProtectedForPermissions(List<string> perms)
    {
        if (perms.Contains("cafe") || perms.Contains("oficina") || perms.Contains("deposito"))
            return ProtectedOperators;
        return Array.Empty<string>();
    }

    /// <summary>2026-06-05: Inicial corta (2 chars uppercase) para mostrar en chips/listados.
    /// OSMAR -> OS, GERMAN -> GE, GABRIEL -> GA, ALEXIS -> AL, WALTER -> WA, RODRIGO -> RO, MIGUEL -> MI.</summary>
    public static string ShortLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim().ToUpperInvariant();
        return n.Length >= 2 ? n.Substring(0, 2) : n;
    }

    /// <summary>2026-06-05: Color de fondo del chip de inicial, segun el operador. Cada uno
    /// tiene su tono unico para distinguir de un vistazo en el listado de ventas.</summary>
    public static string ShortBgColor(string? name)
    {
        var n = (name ?? "").Trim().ToUpperInvariant();
        return n switch
        {
            "OSMAR"   => "#2563eb",   // azul
            "GERMAN"  => "#16a34a",   // verde
            "GABRIEL" => "#f59e0b",   // ambar
            "ALEXIS"  => "#dc2626",   // rojo
            "WALTER"  => "#7c3aed",   // violeta
            "RODRIGO" => "#0891b2",   // cyan
            "MIGUEL"  => "#db2777",   // pink
            _         => "#9ca3af"    // gris fallback
        };
    }

    public string? Current => _current;
    public bool HasOperator => !string.IsNullOrWhiteSpace(_current);

    public event Action? OnChange;

    public OperatorService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task LoadAsync()
    {
        try
        {
            var stored = await _js.InvokeAsync<string?>("localStorage.getItem", StorageKey);
            if (!string.IsNullOrWhiteSpace(stored) && Operators.Contains(stored))
                _current = stored;
        }
        catch { /* primer arranque, sin nada */ }
    }

    public async Task SetAsync(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name) && !Operators.Contains(name))
            return;
        _current = string.IsNullOrWhiteSpace(name) ? null : name;
        try
        {
            if (_current is null)
                await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
            else
                await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, _current);
        }
        catch { }
        OnChange?.Invoke();
    }

    public async Task ClearAsync() => await SetAsync(null);
}
