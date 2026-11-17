using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.Json;

namespace Api.Controllers;

/// <summary>
/// 2026-11-06: el orden del menú nuevo (qué pantalla va en qué sección) lo acomoda el dueño
/// desde la pantalla ("✏ Acomodar"). Se guarda acá como un JSON único para todos los que usan
/// el menú nuevo. Sin fila = el orden de fábrica (MenuNuevoCatalogo.cs en la web).
/// </summary>
[ApiController]
[Route("api/menu-config")]
[Authorize]
public class MenuConfigController : ControllerBase
{
    private readonly AppDbContext _db;
    private static bool _tablaOk;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public MenuConfigController(AppDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        await AsegurarTablaAsync();
        var json = (await _db.Database
            .SqlQueryRaw<string>("SELECT TOP 1 Json AS Value FROM Menu_Config WHERE Id = 1")
            .ToListAsync()).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(json)) return NoContent();
        return Content(json, "application/json");
    }

    [HttpPut]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Put([FromBody] JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return BadRequest("Formato inválido");
        var json = body.GetRawText();
        if (json.Length > 100_000) return BadRequest("Demasiado grande");
        await AsegurarTablaAsync();
        var quien = User.FindFirstValue(ClaimTypes.Name) ?? "";
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
MERGE Menu_Config AS t USING (SELECT 1 AS Id) AS s ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET Json = {json}, ActualizadoAt = SYSUTCDATETIME(), ActualizadoPor = {quien}
WHEN NOT MATCHED THEN INSERT (Id, Json, ActualizadoPor) VALUES (1, {json}, {quien});");
        return Ok(new { ok = true });
    }

    /// <summary>"Volver al orden original": borra lo acomodado.</summary>
    [HttpDelete]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> Delete()
    {
        await AsegurarTablaAsync();
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM Menu_Config WHERE Id = 1");
        return NoContent();
    }

    // ───── 2026-11-07: pestañas del depósito ("✏ Acomodar" de ellos): orden y colores. ─────
    // Fila Id = 2 de la misma tabla. La puede cambiar cualquiera que entre (el depósito usa un
    // solo usuario para todos): solo cambia el orden y el color de sus propias pestañas.
    [HttpGet("deposito")]
    public async Task<IActionResult> GetDeposito()
    {
        await AsegurarTablaAsync();
        var json = (await _db.Database
            .SqlQueryRaw<string>("SELECT TOP 1 Json AS Value FROM Menu_Config WHERE Id = 2")
            .ToListAsync()).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(json)) return NoContent();
        return Content(json, "application/json");
    }

    [HttpPut("deposito")]
    public async Task<IActionResult> PutDeposito([FromBody] JsonElement body)
    {
        if (body.ValueKind != JsonValueKind.Object) return BadRequest("Formato inválido");
        var json = body.GetRawText();
        if (json.Length > 10_000) return BadRequest("Demasiado grande");
        await AsegurarTablaAsync();
        var quien = User.FindFirstValue(ClaimTypes.Name) ?? "";
        await _db.Database.ExecuteSqlInterpolatedAsync($@"
MERGE Menu_Config AS t USING (SELECT 2 AS Id) AS s ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET Json = {json}, ActualizadoAt = SYSUTCDATETIME(), ActualizadoPor = {quien}
WHEN NOT MATCHED THEN INSERT (Id, Json, ActualizadoPor) VALUES (2, {json}, {quien});");
        return Ok(new { ok = true });
    }

    [HttpDelete("deposito")]
    public async Task<IActionResult> DeleteDeposito()
    {
        await AsegurarTablaAsync();
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM Menu_Config WHERE Id = 2");
        return NoContent();
    }

    // ───── 2026-11-11: botón de abajo (flotante) de cada persona: qué tiene (hasta 10) y dónde está. ─────
    // Fila Id = 3: { "<persona>": { Items, Derecha, Abajo, LabDer, Acostado } }. Persona "" = la base de Osmar.
    // Los admin (Osmar, Gabriel, Germán comparten usuario y se distinguen por el PIN) mandan la persona;
    // los demás usuarios guardan siempre en la suya ("U:" + usuario), así nadie pisa lo de otro.
    public const int MAX_FLOTANTE = 10;
    /// <summary>Acciones (13/11/2026): lo que muestra el botón ⚡ Acciones ("venta", "cobranza"…); null = las de fábrica.</summary>
    public record FlotantePersona(List<string>? Items, int? Derecha, int? Abajo, bool LabDer, bool Acostado, bool Compacto = false, List<string>? Acciones = null);
    public record FlotantePut(string? Persona, FlotantePersona? Config);

    [HttpGet("flotante")]
    public async Task<IActionResult> GetFlotante()
    {
        await AsegurarTablaAsync();
        return Ok(await LeerFlotanteAsync());
    }

    [HttpPut("flotante")]
    public async Task<IActionResult> PutFlotante([FromBody] FlotantePut req)
    {
        await AsegurarTablaAsync();
        var quien = User.FindFirstValue(ClaimTypes.Name) ?? "";
        var clave = User.IsInRole("admin")
            ? (req.Persona ?? "").Trim().ToUpperInvariant()
            : "U:" + quien.Trim().ToUpperInvariant();
        if (clave.Length > 60) return BadRequest("Persona inválida");
        await _lockFlot.WaitAsync();
        try
        {
            var todo = await LeerFlotanteAsync();
            if (req.Config is null) todo.Remove(clave);   // "Volver a los de Osmar"
            else
            {
                var items = (req.Config.Items ?? new())
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
                    .Where(x => x.Length <= 600)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(MAX_FLOTANTE).ToList();
                int? Lim(int? v) => v is null ? null : Math.Clamp(v.Value, 0, 5000);
                var acciones = req.Config.Acciones?
                    .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Where(x => x.Length <= 40)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(30).ToList();
                todo[clave] = req.Config with { Items = items, Derecha = Lim(req.Config.Derecha), Abajo = Lim(req.Config.Abajo), Acciones = acciones };
            }
            var json = JsonSerializer.Serialize(todo);
            if (json.Length > 100_000) return BadRequest("Demasiado grande");
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
MERGE Menu_Config AS t USING (SELECT 3 AS Id) AS s ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET Json = {json}, ActualizadoAt = SYSUTCDATETIME(), ActualizadoPor = {quien}
WHEN NOT MATCHED THEN INSERT (Id, Json, ActualizadoPor) VALUES (3, {json}, {quien});");
            return Ok(todo);
        }
        finally { _lockFlot.Release(); }
    }

    private static readonly SemaphoreSlim _lockFlot = new(1, 1);

    // ───── 2026-11-17: NOTAS FRECUENTES de la venta, cada persona las suyas. ─────
    // Fila Id = 4: { "<persona>": ["Carga Gabriel hoy", ...] }. Persona = operador (admin, por PIN) o "U:usuario".
    // Quien todavía no armó su lista ve las de fábrica (NOTAS_FABRICA).
    public static readonly List<string> NOTAS_FABRICA = new() { "Carga Gabriel hoy" };
    public const int MAX_NOTAS = 30;
    public record NotasPut(string? Persona, List<string>? Notas);

    private string ClaveNotas(string? persona)
    {
        var quien = User.FindFirstValue(ClaimTypes.Name) ?? "";
        return User.IsInRole("admin")
            ? (persona ?? "").Trim().ToUpperInvariant()
            : "U:" + quien.Trim().ToUpperInvariant();
    }

    [HttpGet("notas")]
    public async Task<IActionResult> GetNotas([FromQuery] string? persona)
    {
        await AsegurarTablaAsync();
        var todo = await LeerNotasAsync();
        return Ok(todo.TryGetValue(ClaveNotas(persona), out var l) ? l : NOTAS_FABRICA);
    }

    [HttpPut("notas")]
    public async Task<IActionResult> PutNotas([FromBody] NotasPut req)
    {
        await AsegurarTablaAsync();
        var clave = ClaveNotas(req.Persona);
        if (clave.Length > 60) return BadRequest("Persona inválida");
        var notas = (req.Notas ?? new())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim())
            .Where(x => x.Length <= 300)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(MAX_NOTAS).ToList();
        await _lockFlot.WaitAsync();
        try
        {
            var todo = await LeerNotasAsync();
            todo[clave] = notas;
            var json = JsonSerializer.Serialize(todo);
            if (json.Length > 200_000) return BadRequest("Demasiado grande");
            var quien = User.FindFirstValue(ClaimTypes.Name) ?? "";
            await _db.Database.ExecuteSqlInterpolatedAsync($@"
MERGE Menu_Config AS t USING (SELECT 4 AS Id) AS s ON t.Id = s.Id
WHEN MATCHED THEN UPDATE SET Json = {json}, ActualizadoAt = SYSUTCDATETIME(), ActualizadoPor = {quien}
WHEN NOT MATCHED THEN INSERT (Id, Json, ActualizadoPor) VALUES (4, {json}, {quien});");
            return Ok(notas);
        }
        finally { _lockFlot.Release(); }
    }

    private async Task<Dictionary<string, List<string>>> LeerNotasAsync()
    {
        var json = (await _db.Database
            .SqlQueryRaw<string>("SELECT TOP 1 Json AS Value FROM Menu_Config WHERE Id = 4")
            .ToListAsync()).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json) ?? new(); }
        catch { return new(); }
    }

    private async Task<Dictionary<string, FlotantePersona>> LeerFlotanteAsync()
    {
        var json = (await _db.Database
            .SqlQueryRaw<string>("SELECT TOP 1 Json AS Value FROM Menu_Config WHERE Id = 3")
            .ToListAsync()).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, FlotantePersona>>(json) ?? new(); }
        catch { return new(); }
    }

    private async Task AsegurarTablaAsync()
    {
        if (_tablaOk) return;
        await _lock.WaitAsync();
        try
        {
            if (_tablaOk) return;
            await _db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('Menu_Config') IS NULL
    CREATE TABLE Menu_Config (
        Id INT NOT NULL PRIMARY KEY,
        Json NVARCHAR(MAX) NOT NULL,
        ActualizadoAt DATETIME2 NOT NULL CONSTRAINT DF_MenuConfig_At DEFAULT SYSUTCDATETIME(),
        ActualizadoPor NVARCHAR(100) NOT NULL CONSTRAINT DF_MenuConfig_Por DEFAULT ''
    );");
            _tablaOk = true;
        }
        finally { _lock.Release(); }
    }
}
