using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace Api.Services;

public class AuthService
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AuthService(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    public async Task<AuthResponse?> Login(string username, string password)
    {
        var user = await _db.Users.Include(u => u.RoleNav).FirstOrDefaultAsync(u => u.Username == username);
        if (user is null || !user.IsActive)
            return null;

        if (!BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
            return null;

        return await GenerateAuthResponseAsync(user);
    }

    public async Task<AuthResponse?> Register(string username, string email, string password)
    {
        if (await _db.Users.AnyAsync(u => u.Username == username))
            return null;

        if (await _db.Users.AnyAsync(u => u.Email == email))
            return null;

        var user = new User
        {
            Username = username,
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
            Role = "usuario",
            RoleId = 2,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return await GenerateAuthResponseAsync(user);
    }

    private async Task<AuthResponse> GenerateAuthResponseAsync(User user)
    {
        var expirationHours = _config.GetValue<int>("Jwt:ExpirationHours", 24);
        var expiresAt = DateTime.UtcNow.AddHours(expirationHours);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.RoleNav?.Name ?? user.Role)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
            _config["Jwt:Secret"] ?? throw new InvalidOperationException("JWT Secret not configured")));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: expiresAt,
            signingCredentials: creds
        );

        // Get permissions for this role
        var roleName = user.RoleNav?.Name ?? user.Role;
        List<string> permissions;
        if (roleName.Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            permissions = MenuDefinition.AllMenuKeys;
        }
        else
        {
            permissions = await _db.RolePermissions
                .Where(rp => rp.RoleId == user.RoleId)
                .Select(rp => rp.MenuKey)
                .ToListAsync();
        }

        return new AuthResponse(
            Token: new JwtSecurityTokenHandler().WriteToken(token),
            Username: user.Username,
            Role: roleName,
            ExpiresAt: expiresAt,
            Permissions: permissions
        );
    }
}
