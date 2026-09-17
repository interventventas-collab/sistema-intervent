using System.Security.Claims;
using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class UsersController : ControllerBase
{
    private readonly UserService _userService;
    private readonly SesionesService _sesiones;

    public UsersController(UserService userService, SesionesService sesiones)
    {
        _userService = userService;
        _sesiones = sesiones;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        if (!IsAdmin()) return Forbid();

        var users = await _userService.GetAllAsync();
        return Ok(users);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        if (!IsAdmin()) return Forbid();

        var user = await _userService.GetByIdAsync(id);
        if (user is null) return NotFound(new { message = "Usuario no encontrado" });
        return Ok(user);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateUserRequest request)
    {
        if (!IsAdmin()) return Forbid();

        var user = await _userService.CreateAsync(request);
        if (user is null) return Conflict(new { message = "El usuario o email ya existe, o el rol es invalido" });
        return Created($"/api/users/{user.Id}", user);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateUserRequest request)
    {
        if (!IsAdmin()) return Forbid();

        var user = await _userService.UpdateAsync(id, request);
        if (user is null) return NotFound(new { message = "Usuario no encontrado o datos invalidos" });

        // 2026-09-17: poner a alguien en Inactivo ahora lo SACA en el momento. Antes el "Inactivo"
        // solo le impedia volver a entrar: el que ya estaba adentro seguia trabajando hasta 24 h.
        if (request.IsActive == false)
            await _sesiones.CerrarTodasDelUsuarioAsync(
                id, Quien(), Api.Models.UserSession.MotivoUsuarioInactivo);

        return Ok(user);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!IsAdmin()) return Forbid();

        // Cerrar primero: despues de borrarlo ya no se puede saber cuales eran sus sesiones.
        await _sesiones.CerrarTodasDelUsuarioAsync(id, Quien(), Api.Models.UserSession.MotivoUsuarioInactivo);

        var result = await _userService.DeleteAsync(id);
        if (!result) return NotFound(new { message = "Usuario no encontrado" });
        return NoContent();
    }

    public record ResetPasswordRequest(string NewPassword);

    /// <summary>Reset administrativo: setea una clave nueva para un usuario que la olvido.</summary>
    [HttpPost("{id:int}/reset-password")]
    public async Task<IActionResult> ResetPassword(int id, [FromBody] ResetPasswordRequest req)
    {
        if (!IsAdmin()) return Forbid();
        if (string.IsNullOrWhiteSpace(req.NewPassword) || req.NewPassword.Length < 6)
            return BadRequest(new { message = "La clave debe tener al menos 6 caracteres" });
        var ok = await _userService.ResetPasswordAsync(id, req.NewPassword);
        if (!ok) return NotFound(new { message = "Usuario no encontrado" });

        // Si le cambiaron la clave desde administracion, lo mas probable es que sea justamente
        // porque hay que sacarlo de donde este. Se cierran todas sus sesiones.
        var cerradas = await _sesiones.CerrarTodasDelUsuarioAsync(
            id, Quien(), Api.Models.UserSession.MotivoCambioClave);

        return Ok(new { ok = true, sesionesCerradas = cerradas });
    }

    private bool IsAdmin()
    {
        return User.FindFirst(ClaimTypes.Role)?.Value == "admin";
    }

    /// <summary>Quien esta haciendo el cambio, para que quede anotado en la sesion cerrada.</summary>
    private string Quien() => User.Identity?.Name ?? "administracion";
}
