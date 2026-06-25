using Microsoft.JSInterop;

namespace Web.Services;

/// <summary>
/// Mantiene quien es el operador (persona fisica) que esta trabajando ahora.
/// Lo guarda en localStorage. El nombre se manda en cada request al backend
/// como header X-Operator-Name para auditoria.
///
/// 2026-06-15: integrado con PIN por operador. Después del login admin, la app
/// se muestra BLOQUEADA hasta que se valida un PIN. Se desbloquea por 60 min;
/// pasada la inactividad se vuelve a bloquear. Persiste validatedAt en localStorage.
/// </summary>
public class OperatorService
{
    private const string StorageKey = "current_operator";
    private const string ValidatedAtKey = "operator_validated_at"; // 2026-06-15
    private const int InactivityMinutes = 60; // 2026-06-15 (subido de 30 a 60 el 24/06)
    private readonly IJSRuntime _js;
    private string? _current;
    private DateTime? _validatedAtUtc;

    // 2026-06-15: MIGUEL/MAXI eliminados (ya no trabajan). Sumados BENJAMIN, GONZALO.
    // 2026-06-25: "FERMAN" sacado — era un typo viejo, no existe esa persona.
    // Lista global = todos los que SÍ trabajan hoy.
    public static readonly string[] Operators =
    {
        "OSMAR", "GERMAN", "GABRIEL",
        "ALEXIS", "WALTER", "RODRIGO", "BENJAMIN", "GONZALO"
    };

    // Operadores por rol (para mostrar solo los del equipo correspondiente en el modal).
    // OSMAR vuelve a la lista normal — el sistema de PIN unificado lo cubre.
    public static readonly string[] OperatorsOficina = { "GERMAN", "GABRIEL", "OSMAR" };
    // 2026-06-15: depósito ahora son 5 personas (Alexis, Walter, Rodrigo, Benjamin, Gonzalo)
    // 2026-06-25: "Ferman" sacado de la lista — era un typo, no existe.
    public static readonly string[] OperatorsDeposito = { "ALEXIS", "WALTER", "RODRIGO", "BENJAMIN", "GONZALO" };

    /// <summary>2026-06-15: ya no se usa la lista de "Protected" — todos los operadores
    /// se autentican con PIN individual. Se deja vacío por compat con pantallas viejas.</summary>
    public static readonly string[] ProtectedOperators = Array.Empty<string>();

    /// <summary>Devuelve la lista de operadores que el usuario logueado puede elegir, en base
    /// a sus permisos. 2026-06-15: admin/oficina ven OSMAR + GERMAN + GABRIEL (todos con PIN).
    /// DEPOSITO ve ALEXIS+WALTER+RODRIGO.</summary>
    public static string[] ForPermissions(List<string> perms)
    {
        if (perms.Contains("cafe") || perms.Contains("oficina")) return OperatorsOficina;
        if (perms.Contains("deposito")) return OperatorsDeposito;
        return OperatorsOficina;
    }

    /// <summary>2026-06-15: queda vacío — todos requieren PIN, no hay "protegidos" aparte.</summary>
    public static string[] ProtectedForPermissions(List<string> perms) => Array.Empty<string>();

    /// <summary>2026-06-05: Inicial corta (2 chars uppercase) para mostrar en chips/listados.</summary>
    public static string ShortLabel(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var n = name.Trim().ToUpperInvariant();
        return n.Length >= 2 ? n.Substring(0, 2) : n;
    }

    public static string ShortBgColor(string? name)
    {
        var n = (name ?? "").Trim().ToUpperInvariant();
        return n switch
        {
            "OSMAR"   => "#2563eb",   // azul
            "GERMAN"  => "#16a34a",   // verde
            "GABRIEL" => "#f59e0b",   // ámbar
            "ALEXIS"  => "#dc2626",   // rojo
            "WALTER"  => "#7c3aed",   // violeta
            "RODRIGO" => "#0891b2",   // cyan
            "BENJAMIN" => "#65a30d",  // lima
            "GONZALO" => "#0d9488",   // teal
            _         => "#9ca3af"    // gris fallback
        };
    }

    public string? Current => _current;
    public bool HasOperator => !string.IsNullOrWhiteSpace(_current);

    /// <summary>2026-06-15: true si HAY operador Y el último PIN fue validado hace menos de 60 min.
    /// Mientras esto sea false, la app debe mostrarse BLOQUEADA (pantalla del PIN).</summary>
    public bool IsValidated
    {
        get
        {
            if (!HasOperator || _validatedAtUtc is null) return false;
            return (DateTime.UtcNow - _validatedAtUtc.Value).TotalMinutes < InactivityMinutes;
        }
    }

    public DateTime? ValidatedAtUtc => _validatedAtUtc;

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
            var validatedStr = await _js.InvokeAsync<string?>("localStorage.getItem", ValidatedAtKey);
            if (!string.IsNullOrWhiteSpace(validatedStr) && DateTime.TryParse(validatedStr, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                _validatedAtUtc = dt.ToUniversalTime();
        }
        catch { }
    }

    /// <summary>2026-06-15: cambia operador (sin marcar validado — el PIN se valida aparte).</summary>
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

    /// <summary>2026-06-15: marca al operador como validado en este momento (después de un PIN correcto).
    /// Combina set del operador + timestamp para no tener carrera entre los dos pasos.</summary>
    public async Task SetValidatedAsync(string nombre)
    {
        _current = nombre;
        _validatedAtUtc = DateTime.UtcNow;
        try
        {
            await _js.InvokeVoidAsync("localStorage.setItem", StorageKey, _current);
            await _js.InvokeVoidAsync("localStorage.setItem", ValidatedAtKey, _validatedAtUtc.Value.ToString("o"));
        }
        catch { }
        OnChange?.Invoke();
    }

    /// <summary>2026-06-15: marca actividad reciente — refresca el timestamp para que no se desloguee
    /// por inactividad mientras el operador esté trabajando. Se llama en cada acción importante.</summary>
    public async Task TouchAsync()
    {
        if (!HasOperator) return;
        _validatedAtUtc = DateTime.UtcNow;
        try { await _js.InvokeVoidAsync("localStorage.setItem", ValidatedAtKey, _validatedAtUtc.Value.ToString("o")); }
        catch { }
    }

    /// <summary>2026-06-15: bloquea la sesión (deja el nombre pero quita el validado).
    /// Se usa al pasar 60 min de inactividad o al cambiar de operador.</summary>
    public async Task LockAsync()
    {
        _validatedAtUtc = null;
        try { await _js.InvokeVoidAsync("localStorage.removeItem", ValidatedAtKey); }
        catch { }
        OnChange?.Invoke();
    }

    public async Task ClearAsync()
    {
        _current = null;
        _validatedAtUtc = null;
        try
        {
            await _js.InvokeVoidAsync("localStorage.removeItem", StorageKey);
            await _js.InvokeVoidAsync("localStorage.removeItem", ValidatedAtKey);
        }
        catch { }
        OnChange?.Invoke();
    }
}
