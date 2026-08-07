using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Api.Controllers;

/// <summary>2026-08-06: candado de 4 dígitos para la pantalla de WhatsApp del celu (/whatsapp-movil).
/// Solo entran Osmar/Germán/Gabriel con su año de nacimiento. Identifica quién entró.
/// Los códigos se guardan en AppSettings (key "wamovil.codigos", JSON) y son editables; si no
/// existe todavía, usa los valores por defecto. Es una barrera liviana ADEMÁS del login del sistema
/// (la pantalla sigue siendo [Authorize]); la huella se sumará como método principal más adelante.</summary>
[ApiController]
[Route("api/wa-movil")]
[Authorize]
public class WaMovilAccesoController : ControllerBase
{
    private readonly AppDbContext _db;
    public WaMovilAccesoController(AppDbContext db) { _db = db; }

    private const string SettingKey = "wamovil.codigos";

    public record AccesoDto(string Nombre, string Codigo);
    public record VerificarRequest(string Codigo);

    // Valores por defecto (si todavía no se editaron desde la config).
    private static List<AccesoDto> Defaults() => new()
    {
        new("Osmar", "1988"),
        new("Germán", "1990"),
        new("Gabriel", "1983"),
    };

    private async Task<List<AccesoDto>> LeerAsync()
    {
        var s = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(x => x.Key == SettingKey);
        if (s == null || string.IsNullOrWhiteSpace(s.Value)) return Defaults();
        try
        {
            var list = JsonSerializer.Deserialize<List<AccesoDto>>(s.Value);
            return (list != null && list.Count > 0) ? list : Defaults();
        }
        catch { return Defaults(); }
    }

    /// <summary>Verifica el código de 4 dígitos. Devuelve { ok, nombre } si coincide con alguien.</summary>
    [HttpPost("verificar")]
    public async Task<IActionResult> Verificar([FromBody] VerificarRequest req)
    {
        var codigo = (req?.Codigo ?? "").Trim();
        if (string.IsNullOrWhiteSpace(codigo)) return Ok(new { ok = false });
        var lista = await LeerAsync();
        var match = lista.FirstOrDefault(a => a.Codigo == codigo);
        if (match == null) return Ok(new { ok = false });
        return Ok(new { ok = true, nombre = match.Nombre });
    }

    /// <summary>Lista los accesos (para una pantalla de edición). Solo admin.</summary>
    [HttpGet("codigos")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Codigos() => Ok(await LeerAsync());

    /// <summary>Guarda/edita los accesos (nombre + código de 4 dígitos). Solo admin.</summary>
    [HttpPut("codigos")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> GuardarCodigos([FromBody] List<AccesoDto> accesos)
    {
        accesos ??= new();
        // Normalizamos: solo los que tengan nombre y código de 4 dígitos numéricos.
        var limpios = accesos
            .Where(a => !string.IsNullOrWhiteSpace(a.Nombre)
                        && !string.IsNullOrWhiteSpace(a.Codigo)
                        && a.Codigo.Trim().Length == 4
                        && a.Codigo.Trim().All(char.IsDigit))
            .Select(a => new AccesoDto(a.Nombre.Trim(), a.Codigo.Trim()))
            .ToList();

        var json = JsonSerializer.Serialize(limpios);
        var s = await _db.AppSettings.FirstOrDefaultAsync(x => x.Key == SettingKey);
        if (s == null)
        {
            s = new AppSetting { Key = SettingKey, Value = json, UpdatedAt = DateTime.UtcNow };
            _db.AppSettings.Add(s);
        }
        else
        {
            s.Value = json;
            s.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, count = limpios.Count });
    }
}
