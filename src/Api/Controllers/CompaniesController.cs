using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CompaniesController : ControllerBase
{
    private readonly AppDbContext _db;

    // IDs estables. Se usan internamente para tagear marcas, comprobantes, etc.
    // No se pueden renombrar — solo cambian sus DisplayName.
    private static readonly string[] CompanyIds = { "INTERVENT", "INTEREVENTOS", "FRIKAF", "PALANICA" };

    private static string SettingKeyFor(string id) => $"company.display.{id.ToLowerInvariant()}";

    public CompaniesController(AppDbContext db)
    {
        _db = db;
    }

    public record CompanyNameDto(string Id, string DisplayName);
    public record UpdateCompanyNamesRequest(string Password, Dictionary<string, string>? Names);

    /// <summary>Devuelve los 4 IDs con su DisplayName actual (default = ID si no hay setting).</summary>
    [HttpGet("names")]
    public async Task<IActionResult> GetNames()
    {
        var keys = CompanyIds.Select(SettingKeyFor).ToArray();
        var settings = await _db.AppSettings
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        var result = CompanyIds.Select(id => new CompanyNameDto(
            id,
            settings.GetValueOrDefault(SettingKeyFor(id), id)
        )).ToList();

        return Ok(result);
    }

    /// <summary>Actualiza los DisplayName. Requiere clave de OSMAR (la misma que usa para eliminar comprobantes).</summary>
    [HttpPost("names")]
    public async Task<IActionResult> UpdateNames([FromBody] UpdateCompanyNamesRequest request)
    {
        if (string.IsNullOrEmpty(request.Password))
            return BadRequest(new { error = "Falta la clave." });

        // Validar operador actual contra el operador autorizado (mismo que delete de ventas).
        var allowedOp = (await _db.AppSettings.FindAsync("sales.delete_allowed_operator"))?.Value ?? "OSMAR";
        var currentOp = HttpContext.Request.Headers["X-Operator-Name"].ToString();
        if (string.IsNullOrEmpty(currentOp))
            return StatusCode(403, new { error = "No hay operador en sesion. Elegi tu nombre arriba antes de continuar." });
        if (!string.Equals(currentOp, allowedOp, StringComparison.OrdinalIgnoreCase))
            return StatusCode(403, new { error = $"Solo {allowedOp} puede cambiar los nombres de las empresas." });

        // Validar contraseña contra el setting global.
        var expectedPassword = (await _db.AppSettings.FindAsync("sales.delete_password"))?.Value ?? "";
        if (string.IsNullOrEmpty(expectedPassword))
            return StatusCode(500, new { error = "No hay clave configurada en el servidor." });
        if (!string.Equals(request.Password, expectedPassword, StringComparison.Ordinal))
            return StatusCode(403, new { error = "Clave incorrecta." });

        if (request.Names is null || request.Names.Count == 0)
            return BadRequest(new { error = "No se enviaron nombres a actualizar." });

        // Aplicar updates: solo los IDs conocidos. Vacio = volver al default (borra el setting).
        foreach (var id in CompanyIds)
        {
            if (!request.Names.TryGetValue(id, out var newName)) continue;
            newName = (newName ?? "").Trim();

            var key = SettingKeyFor(id);
            var setting = await _db.AppSettings.FindAsync(key);
            if (string.IsNullOrEmpty(newName))
            {
                // Sin nombre custom: borramos el setting para que vuelva al default
                if (setting is not null) _db.AppSettings.Remove(setting);
                continue;
            }
            if (newName.Length > 100) newName = newName[..100];
            if (setting is null)
            {
                _db.AppSettings.Add(new AppSetting { Key = key, Value = newName });
            }
            else
            {
                setting.Value = newName;
                setting.UpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();

        // Devolver el estado actualizado (mismo formato que GET) re-consultando.
        var refreshedKeys = CompanyIds.Select(SettingKeyFor).ToArray();
        var refreshed = await _db.AppSettings
            .Where(s => refreshedKeys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        var updated = CompanyIds.Select(id => new CompanyNameDto(
            id,
            refreshed.GetValueOrDefault(SettingKeyFor(id), id)
        )).ToList();

        return Ok(updated);
    }
}
