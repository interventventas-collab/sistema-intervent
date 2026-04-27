using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;
using Web.Models;

namespace Web.Services;

// AuthService maneja la sesion del usuario.
//
// IMPORTANTE: el JWT vive en una cookie httpOnly seteada por el backend.
// El frontend NO tiene acceso al token (eso es a proposito: si una vulnerabilidad
// de XSS llegara a ejecutarse, no podria robar el token).
//
// En localStorage solo guardamos datos de UI (username, role, permisos, expiry)
// para no tener que ir al backend en cada cambio de pagina. Si alguien manipula
// estos valores, no compromete nada del lado del servidor: la API valida la cookie
// en cada request.
public class AuthService
{
    private const string UserKey = "tm_user";
    private const string ExpiryKey = "tm_expires";

    private readonly HttpClient _http;
    private readonly IJSRuntime _js;

    public AuthService(HttpClient http, IJSRuntime js)
    {
        _http = http;
        _js = js;
    }

    public async Task<AuthResponse> LoginAsync(string username, string password)
    {
        var request = new LoginRequest { Username = username, Password = password };
        var response = await _http.PostAsJsonAsync("/api/auth/login", request);

        if (!response.IsSuccessStatusCode)
        {
            // Manejar 429 (Too Many Requests) por rate limiting de login.
            if ((int)response.StatusCode == 429)
                throw new Exception("Demasiados intentos. Esperá un momento e intentá de nuevo.");

            string? message = null;
            try
            {
                var error = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (error.TryGetProperty("message", out var msg))
                    message = msg.GetString();
            }
            catch { /* respuesta sin JSON */ }
            throw new Exception(message ?? "Error al iniciar sesion");
        }

        var data = await response.Content.ReadFromJsonAsync<AuthResponse>()
            ?? throw new Exception("Respuesta invalida del servidor");

        await SaveSessionAsync(data);
        return data;
    }

    public async Task LogoutAsync()
    {
        // Avisar al backend para que borre la cookie httpOnly.
        try { await _http.PostAsync("/api/auth/logout", null); }
        catch { /* aunque falle el server, igual limpiamos local */ }

        await ClearSessionAsync();
    }

    public async Task<UserInfo?> GetUserAsync()
    {
        var raw = await _js.InvokeAsync<string?>("localStorage.getItem", UserKey);
        if (string.IsNullOrEmpty(raw)) return null;
        try
        {
            return JsonSerializer.Deserialize<UserInfo>(raw, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch
        {
            return null;
        }
    }

    // Sin acceso al JWT, "valido" significa: hay una sesion activa cacheada en
    // localStorage que todavia no expiro. La API igualmente valida la cookie
    // en cada request, asi que esto es solo un check optimista de UI.
    public async Task<bool> IsSessionValidAsync()
    {
        var user = await GetUserAsync();
        if (user is null) return false;

        var expiresAt = await _js.InvokeAsync<string?>("localStorage.getItem", ExpiryKey);
        if (string.IsNullOrEmpty(expiresAt)) return true;

        return DateTime.TryParse(expiresAt, out var expiry) && expiry > DateTime.UtcNow;
    }

    private async Task SaveSessionAsync(AuthResponse data)
    {
        var userJson = JsonSerializer.Serialize(new UserInfo
        {
            Username = data.Username,
            Role = data.Role,
            Permissions = data.Permissions ?? new()
        });
        await _js.InvokeVoidAsync("localStorage.setItem", UserKey, userJson);
        await _js.InvokeVoidAsync("localStorage.setItem", ExpiryKey, data.ExpiresAt.ToString("O"));
    }

    private async Task ClearSessionAsync()
    {
        await _js.InvokeVoidAsync("localStorage.removeItem", UserKey);
        await _js.InvokeVoidAsync("localStorage.removeItem", ExpiryKey);
        // Limpiar tambien el viejo "tm_token" por si quedo de una sesion anterior
        // a la migracion a cookies. Inocuo si no existe.
        await _js.InvokeVoidAsync("localStorage.removeItem", "tm_token");
    }
}

public class UserInfo
{
    public string Username { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public List<string> Permissions { get; set; } = new();
}
