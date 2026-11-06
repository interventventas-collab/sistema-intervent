using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace Api.Controllers;

/// <summary>
/// 2026-11-06: anota qué pantalla abre cada uno, para reordenar el menú según el uso real
/// (pedido del dueño: "midamos primero" antes de esconder secciones). Es invisible: el menú
/// manda un aviso cada vez que se cambia de pantalla. Una fila por visita.
/// </summary>
[ApiController]
[Route("api/uso")]
[Authorize]
public class UsoPantallasController : ControllerBase
{
    private readonly AppDbContext _db;
    private static bool _tablaOk;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public UsoPantallasController(AppDbContext db) => _db = db;

    public record VisitaDto(string? Ruta, string? Operador);

    [HttpPost("visita")]
    public async Task<IActionResult> Visita([FromBody] VisitaDto dto)
    {
        var ruta = Normalizar(dto.Ruta);
        if (ruta == null) return NoContent();

        await AsegurarTablaAsync();

        var usuario = User.FindFirstValue(ClaimTypes.Name) ?? User.Identity?.Name ?? "";
        var operador = (dto.Operador ?? "").Trim();
        if (operador.Length > 60) operador = operador[..60];
        if (usuario.Length > 100) usuario = usuario[..100];

        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO Uso_Pantallas (Ruta, Usuario, Operador) VALUES ({ruta}, {usuario}, {operador})");
        return NoContent();
    }

    /// <summary>Saca lo que cambia de visita en visita (números, códigos largos, ?filtros)
    /// para que "/cafe/ventas/123" y "/cafe/ventas/456" cuenten como la misma pantalla.</summary>
    private static string? Normalizar(string? ruta)
    {
        if (string.IsNullOrWhiteSpace(ruta)) return null;
        var r = ruta.Split('?')[0].Split('#')[0].Trim().ToLowerInvariant();
        if (!r.StartsWith('/')) r = "/" + r;
        r = Regex.Replace(r, @"/(\d+|[0-9a-f]{16,}|[0-9a-f\-]{36})(?=/|$)", "/N");
        if (r.Length > 1) r = r.TrimEnd('/');
        return r.Length > 200 ? r[..200] : r;
    }

    private async Task AsegurarTablaAsync()
    {
        if (_tablaOk) return;
        await _lock.WaitAsync();
        try
        {
            if (_tablaOk) return;
            await _db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('Uso_Pantallas') IS NULL
BEGIN
    CREATE TABLE Uso_Pantallas (
        Id INT IDENTITY(1,1) PRIMARY KEY,
        Ruta NVARCHAR(200) NOT NULL,
        Usuario NVARCHAR(100) NOT NULL CONSTRAINT DF_UsoPantallas_Usuario DEFAULT '',
        Operador NVARCHAR(60) NOT NULL CONSTRAINT DF_UsoPantallas_Operador DEFAULT '',
        CuandoAt DATETIME2 NOT NULL CONSTRAINT DF_UsoPantallas_Cuando DEFAULT SYSUTCDATETIME()
    );
    CREATE INDEX IX_UsoPantallas_Cuando ON Uso_Pantallas(CuandoAt);
END");
            _tablaOk = true;
        }
        finally { _lock.Release(); }
    }
}
