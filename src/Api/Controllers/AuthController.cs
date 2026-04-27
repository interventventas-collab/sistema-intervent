using System.Security.Claims;
using Api.Data;
using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    // Nombre de la cookie httpOnly que transporta el JWT.
    public const string AccessTokenCookieName = "aiml_token";

    private readonly AuthService _authService;
    private readonly AppDbContext _db;

    public AuthController(AuthService authService, AppDbContext db)
    {
        _authService = authService;
        _db = db;
    }

    [HttpPost("login")]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        try
        {
            var result = await _authService.Login(request.Username, request.Password);
            if (result is null)
                return Unauthorized(new { message = "Invalid username or password" });

            // Setear el JWT en una cookie httpOnly para evitar exfiltracion via XSS.
            // El frontend ya no necesita guardar el token en localStorage.
            SetAccessTokenCookie(result.Token, result.ExpiresAt);

            return Ok(result);
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "An error occurred during login" });
        }
    }

    // Solo el admin puede crear usuarios nuevos. Los registros publicos
    // estan deshabilitados a proposito para prevenir abuso del endpoint.
    [Authorize(Roles = "admin")]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        try
        {
            var result = await _authService.Register(request.Username, request.Email, request.Password);
            if (result is null)
                return Conflict(new { message = "Username or email already exists" });

            return Created("", result);
        }
        catch (Exception)
        {
            return StatusCode(500, new { message = "An error occurred during registration" });
        }
    }

    // Cierra la sesion eliminando la cookie del JWT.
    // Es seguro llamarlo aunque no haya sesion: solo borra la cookie.
    [HttpPost("logout")]
    [AllowAnonymous]
    public IActionResult Logout()
    {
        Response.Cookies.Delete(AccessTokenCookieName, new CookieOptions
        {
            Path = "/",
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Strict
        });
        return Ok(new { message = "Logged out" });
    }

    private void SetAccessTokenCookie(string token, DateTime expiresAt)
    {
        Response.Cookies.Append(AccessTokenCookieName, token, new CookieOptions
        {
            HttpOnly = true,
            // Secure se activa solo en HTTPS para que dev por http://localhost siga funcionando.
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = expiresAt
        });
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> Me()
    {
        try
        {
            var userId = GetUserId();
            if (userId is null)
                return Unauthorized();

            var user = await _db.Users.Include(u => u.RoleNav).FirstOrDefaultAsync(u => u.Id == userId.Value);
            if (user is null)
                return NotFound(new { message = "User not found" });

            return Ok(new UserDto(
                Id: user.Id,
                Username: user.Username,
                Email: user.Email,
                Role: user.RoleNav?.Name ?? user.Role,
                CreatedAt: user.CreatedAt,
                IsActive: user.IsActive
            ));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "An error occurred", error = ex.Message });
        }
    }

    [Authorize]
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        try
        {
            var userId = GetUserId();
            if (userId is null)
                return Unauthorized();

            var user = await _db.Users.Include(u => u.RoleNav).FirstOrDefaultAsync(u => u.Id == userId.Value);
            if (user is null)
                return NotFound(new { message = "User not found" });

            return Ok(new ProfileDto(
                Id: user.Id,
                Username: user.Username,
                Email: user.Email,
                FirstName: user.FirstName,
                LastName: user.LastName,
                Phone: user.Phone,
                Role: user.RoleNav?.Name ?? user.Role,
                CreatedAt: user.CreatedAt
            ));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "An error occurred", error = ex.Message });
        }
    }

    [Authorize]
    [HttpPut("profile")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        try
        {
            var userId = GetUserId();
            if (userId is null)
                return Unauthorized();

            var user = await _db.Users.Include(u => u.RoleNav).FirstOrDefaultAsync(u => u.Id == userId.Value);
            if (user is null)
                return NotFound(new { message = "User not found" });

            if (request.Email is not null && request.Email != user.Email)
            {
                if (await _db.Users.AnyAsync(u => u.Email == request.Email && u.Id != userId.Value))
                    return Conflict(new { message = "Email already in use" });
                user.Email = request.Email;
            }

            if (request.FirstName is not null) user.FirstName = request.FirstName;
            if (request.LastName is not null) user.LastName = request.LastName;
            if (request.Phone is not null) user.Phone = request.Phone;

            await _db.SaveChangesAsync();

            return Ok(new ProfileDto(
                Id: user.Id,
                Username: user.Username,
                Email: user.Email,
                FirstName: user.FirstName,
                LastName: user.LastName,
                Phone: user.Phone,
                Role: user.RoleNav?.Name ?? user.Role,
                CreatedAt: user.CreatedAt
            ));
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "An error occurred", error = ex.Message });
        }
    }

    [Authorize]
    [HttpPut("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request)
    {
        try
        {
            var userId = GetUserId();
            if (userId is null)
                return Unauthorized();

            var user = await _db.Users.FindAsync(userId.Value);
            if (user is null)
                return NotFound(new { message = "User not found" });

            if (!BCrypt.Net.BCrypt.Verify(request.CurrentPassword, user.PasswordHash))
                return BadRequest(new { message = "Current password is incorrect" });

            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.NewPassword);
            await _db.SaveChangesAsync();

            return Ok(new { message = "Password changed successfully" });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { message = "An error occurred", error = ex.Message });
        }
    }

    private int? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        return claim is not null && int.TryParse(claim.Value, out var id) ? id : null;
    }
}
