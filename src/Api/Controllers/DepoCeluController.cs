using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-10: inicio único del celu del depósito (/depo). Sin usuario: pide la clave del día
/// (ClaveDiaCeluService). Desde acá salen los links de Cargar stock y Fotos, y lo que se carga
/// producto por producto desde la lista: ubicación, vencimiento y foto. Lo que se guarda queda
/// anotado como "Celu depósito" (la pantalla no sabe quién es).
/// </summary>
[ApiController]
[Route("api/public/depo-celu")]
[AllowAnonymous]
public class DepoCeluController : ControllerBase
{
    private const string QUIEN = "Celu depósito";
    private readonly AppDbContext _db;
    private readonly ClaveDiaCeluService _clave;

    public DepoCeluController(AppDbContext db, ClaveDiaCeluService clave)
    {
        _db = db;
        _clave = clave;
    }

    private ObjectResult PaseVencido() => StatusCode(403, new { error = "La clave cambió. Pedí la de hoy." });

    /// <summary>Tokens de las pantallas que ya existían (Cargar stock y Fotos), para los botones del inicio.</summary>
    [HttpGet("links")]
    public async Task<IActionResult> Links()
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        var stock = await _db.AppSettings.AsNoTracking()
            .Where(x => x.Key == "stock.public_token").Select(x => x.Value).FirstOrDefaultAsync();
        var fotos = await FotoGaleriaService.LinkTokenAsync(_db, crear: true);
        return Ok(new { stock = string.IsNullOrWhiteSpace(stock) ? null : stock, fotos });
    }

    /// <summary>Plantas y lugares ya creados, para elegir la ubicación (desde el celu no se crean lugares nuevos).</summary>
    [HttpGet("zonas")]
    public async Task<IActionResult> Zonas()
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        var zonas = await _db.CafeUbicacionZonas.AsNoTracking()
            .OrderBy(z => z.Planta).ThenBy(z => z.Codigo)
            .Select(z => new { z.Planta, z.Codigo, z.Nombre }).ToListAsync();
        return Ok(new
        {
            plantas = UbicacionHelper.Plantas.Select(p => new { codigo = p.Codigo, nombre = p.Nombre }),
            zonas,
        });
    }

    public record UbicacionRequest(string? Planta, string? Zona);

    /// <summary>Pone (o saca, con planta vacía) la ubicación de un producto. Misma regla que Ubicaciones.</summary>
    [HttpPut("producto/{id:int}/ubicacion")]
    public async Task<IActionResult> Ubicacion(int id, [FromBody] UbicacionRequest? req)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        var planta = string.IsNullOrWhiteSpace(req?.Planta) ? null : req!.Planta!.Trim().ToUpperInvariant();
        if (planta is not null && !UbicacionHelper.PlantaValida(planta))
            return BadRequest(new { error = "Esa planta no existe." });
        var zona = planta is null ? null : UbicacionHelper.NormalizarZona(req?.Zona);
        if (zona is not null && !await _db.CafeUbicacionZonas.AnyAsync(z => z.Planta == planta && z.Codigo == zona))
            return BadRequest(new { error = "Ese lugar no existe. Pedí que lo creen desde la compu." });

        var p = await _db.CafeProductos.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });
        p.UbicacionPlanta = planta;
        p.UbicacionZona = zona;
        p.UbicacionAt = planta is null ? null : DateTime.UtcNow;
        p.UbicacionPor = planta is null ? null : QUIEN;
        p.UbicacionDudosaAt = null;
        p.UbicacionDudosaPor = null;
        await _db.SaveChangesAsync();
        return Ok(new { lugar = UbicacionHelper.Texto(planta, zona) });
    }

    public record VencimientoRequest(DateTime FechaVence, decimal Cantidad);

    /// <summary>Suma una fecha de vencimiento con su cantidad (como "Asignar vencimiento" de la compu).</summary>
    [HttpPost("producto/{id:int}/vencimiento")]
    public async Task<IActionResult> Vencimiento(int id, [FromBody] VencimientoRequest? req)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        if (req is null || req.Cantidad <= 0 || req.Cantidad > 1_000_000)
            return BadRequest(new { error = "Poné cuántos vencen en esa fecha." });
        var hoy = DateTime.UtcNow.AddHours(-3).Date;
        if (req.FechaVence.Date < hoy.AddYears(-1) || req.FechaVence.Date > hoy.AddYears(15))
            return BadRequest(new { error = "Revisá la fecha de vencimiento." });
        var p = await _db.CafeProductos.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });

        _db.CafeProductoVencimientos.Add(new CafeProductoVencimiento
        {
            ProductoId = id, Cantidad = req.Cantidad, FechaVence = req.FechaVence.Date,
            CreatedAt = DateTime.UtcNow, CreatedBy = QUIEN,
        });
        await _db.SaveChangesAsync();
        var r = (await VencimientosService.ResumenAsync(_db, new[] { id })).GetValueOrDefault(id);
        return Ok(new { vence = r?.ProximoVence, quedan = r?.ProximoQuedan ?? 0 });
    }

    /// <summary>Abre la subida de foto que ya existe (/subir-foto-producto/{token}, 30 minutos).</summary>
    [HttpPost("producto/{id:int}/foto")]
    public async Task<IActionResult> Foto(int id)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        if (!await _db.CafeProductos.AnyAsync(p => p.Id == id)) return NotFound(new { error = "Producto no encontrado" });
        var token = Guid.NewGuid().ToString("N");
        _db.CafeProductoFotoTokens.Add(new CafeProductoFotoToken
        {
            Token = token, CafeProductoId = id, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddMinutes(30),
        });
        await _db.SaveChangesAsync();
        return Ok(new { token });
    }
}
