using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Web.Services;

// Provee el estado de autenticacion a los componentes Blazor.
//
// Antes parseaba el JWT desde localStorage. Ahora el JWT vive en una cookie
// httpOnly que el frontend no puede leer, asi que reconstruimos los claims
// desde la copia de UserInfo que guardamos en localStorage al login.
//
// La API igualmente valida la cookie en cada request, asi que aunque alguien
// manipule el localStorage para "loguearse" en el cliente, no podra acceder
// a ningun endpoint protegido.
public class JwtAuthStateProvider : AuthenticationStateProvider
{
    private readonly AuthService _authService;

    public JwtAuthStateProvider(AuthService authService)
    {
        _authService = authService;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var anonymous = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()));

        if (!await _authService.IsSessionValidAsync())
            return anonymous;

        var user = await _authService.GetUserAsync();
        if (user is null || string.IsNullOrEmpty(user.Username))
            return anonymous;

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role)
        };

        var identity = new ClaimsIdentity(claims, "cookie");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    public void NotifyAuthenticationStateChanged()
    {
        NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());
    }
}
