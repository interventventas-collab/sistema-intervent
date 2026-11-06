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
