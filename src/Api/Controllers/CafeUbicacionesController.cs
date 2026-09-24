using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-09-24: ubicación de los productos en el depósito (/cafe/ubicaciones). Se carga de a poco,
/// a mano: primero la planta (PB / P1 / otro depósito) y, si se sabe, una zona corta (TOST).
/// Se puede asignar a un grupo entero (todo el café → PB · TOST) mirando antes qué agarra.
/// Solo guarda DÓNDE está, no cuánto hay en cada lugar (eso lo sigue llevando el stock).
/// </summary>
[ApiController]
[Route("api/cafe/ubicaciones")]
[Authorize]
public class CafeUbicacionesController : ControllerBase
{
    private readonly AppDbContext _db;

    public CafeUbicacionesController(AppDbContext db) { _db = db; }

    private string Usuario => User.Identity?.Name ?? "?";

    /// <summary>Avance + lugares usados (con cuántos productos tiene cada uno) + zonas conocidas.</summary>
    [HttpGet("resumen")]
    public async Task<IActionResult> Resumen()
    {
        var prods = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => new { p.UbicacionPlanta, p.UbicacionZona, p.UbicacionDudosaAt })
            .ToListAsync();
        var zonas = await _db.CafeUbicacionZonas.AsNoTracking()
            .OrderBy(z => z.Planta).ThenBy(z => z.Codigo).ToListAsync();

        var lugares = prods
            .Where(p => p.UbicacionPlanta != null)
            .GroupBy(p => new { p.UbicacionPlanta, p.UbicacionZona })
            .Select(g => new
            {
                planta = g.Key.UbicacionPlanta,
                zona = g.Key.UbicacionZona,
                texto = UbicacionHelper.Texto(g.Key.UbicacionPlanta, g.Key.UbicacionZona),
                nombre = NombreLargo(g.Key.UbicacionPlanta!, g.Key.UbicacionZona, zonas),
                cantidad = g.Count()
            })
            .OrderBy(l => Array.FindIndex(UbicacionHelper.Plantas, x => x.Codigo == l.planta))
            .ThenBy(l => l.zona)
            .ToList();

        return Ok(new
        {
            total = prods.Count,
            conPlanta = prods.Count(p => p.UbicacionPlanta != null),
            conZona = prods.Count(p => p.UbicacionPlanta != null && p.UbicacionZona != null),
            sinLugar = prods.Count(p => p.UbicacionPlanta == null),
            dudosos = prods.Count(p => p.UbicacionDudosaAt != null),
            plantas = UbicacionHelper.Plantas.Select(p => new { codigo = p.Codigo, nombre = p.Nombre }),
            lugares,
            zonas = zonas.Select(z => new { planta = z.Planta, codigo = z.Codigo, nombre = z.Nombre })
        });
    }

    private static string NombreLargo(string planta, string? zona, List<CafeUbicacionZona> zonas)
    {
        var np = UbicacionHelper.NombrePlanta(planta);
        if (zona is null) return np;
        var z = zonas.FirstOrDefault(x => x.Planta == planta && x.Codigo == zona);
        return $"{np} · {(string.IsNullOrWhiteSpace(z?.Nombre) ? zona : z!.Nombre)}";
    }

    /// <summary>Productos activos con su lugar. Filtros: q (nombre/SKU/marca, varias palabras),
    /// categoria (CAFE|OTROS), lugar ("sin" | "dudosos" | "PB" | "PB|TOST" | "PB|" = planta sin zona).
    /// "pedidos" = en cuántos pedidos salió en los últimos 30 días (ventas propias + MeLi): sirve
    /// para ordenar por "lo que más se busca".</summary>
    [HttpGet("productos")]
    public async Task<IActionResult> Productos([FromQuery] string? q = null, [FromQuery] string? categoria = null,
        [FromQuery] string? lugar = null)
    {
        var query = _db.CafeProductos.AsNoTracking().Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(categoria)) query = query.Where(p => p.Categoria == categoria);

        foreach (var palabra in (q ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var w = palabra;
            query = query.Where(p => p.Nombre.Contains(w) || (p.Sku != null && p.Sku.Contains(w)) || (p.Marca != null && p.Marca.Contains(w)));
        }

        if (lugar == "sin") query = query.Where(p => p.UbicacionPlanta == null);
        else if (lugar == "dudosos") query = query.Where(p => p.UbicacionDudosaAt != null);
        else if (!string.IsNullOrWhiteSpace(lugar))
        {
            var partes = lugar.Split('|');
            var planta = partes[0];
            query = query.Where(p => p.UbicacionPlanta == planta);
            if (partes.Length > 1)
            {
                var zona = UbicacionHelper.NormalizarZona(partes[1]);
                query = zona is null ? query.Where(p => p.UbicacionZona == null) : query.Where(p => p.UbicacionZona == zona);
            }
        }

        var filas = await query
            .Select(p => new
            {
                p.Id, p.Nombre, p.Sku, p.Marca, p.Categoria,
                p.UbicacionPlanta, p.UbicacionZona, p.UbicacionAt, p.UbicacionPor,
                p.UbicacionDudosaAt, p.UbicacionDudosaPor
            })
            .OrderBy(p => p.Nombre)
            .Take(1000)
            .ToListAsync();

        var pedidos = await PedidosUltimos30DiasAsync(filas.Select(f => f.Id).ToList());

        return Ok(filas.Select(p => new
        {
            id = p.Id,
            nombre = p.Nombre,
            sku = p.Sku,
            marca = p.Marca,
            categoria = p.Categoria,
            planta = p.UbicacionPlanta,
            zona = p.UbicacionZona,
            lugar = UbicacionHelper.Texto(p.UbicacionPlanta, p.UbicacionZona),
            cargadoAt = p.UbicacionAt,
            cargadoPor = p.UbicacionPor,
            dudosaAt = p.UbicacionDudosaAt,
            dudosaPor = p.UbicacionDudosaPor,
            pedidos = pedidos.TryGetValue(p.Id, out var n) ? n : 0
        }));
    }

    /// <summary>ProductoId → en cuántos pedidos salió en 30 días (ventas propias + órdenes MeLi).</summary>
    private async Task<Dictionary<int, int>> PedidosUltimos30DiasAsync(List<int> ids)
    {
        var desde = DateTime.UtcNow.AddDays(-30);
        var propias = await _db.CafeVentaItems.AsNoTracking()
            .Where(i => i.ProductoId != null && i.VentaNav!.CreatedAt >= desde && i.VentaNav.Estado != "anulado")
            .GroupBy(i => i.ProductoId!.Value)
            .Select(g => new { Id = g.Key, N = g.Select(x => x.VentaId).Distinct().Count() })
            .ToListAsync();

        var meli = await (from o in _db.MeliOrders.AsNoTracking()
                          join c in _db.MeliItemComponentes.AsNoTracking() on o.ItemId equals c.MeliItemId
                          where o.DateCreated >= desde && o.Status != "cancelled"
                          group o by c.CafeProductoId into g
                          select new { Id = g.Key, N = g.Select(x => x.MeliOrderId).Distinct().Count() })
                         .ToListAsync();

        var set = ids.ToHashSet();
        var res = new Dictionary<int, int>();
        foreach (var r in propias.Concat(meli).Where(r => set.Contains(r.Id)))
            res[r.Id] = res.GetValueOrDefault(r.Id) + r.N;
        return res;
    }

    public record AsignarRequest(List<int> Ids, string? Planta, string? Zona, string? ZonaNombre);

    /// <summary>Pone el mismo lugar a uno o varios productos. Planta vacía = sacarles el lugar.
    /// Si la zona es nueva, la crea con su nombre largo. Asignar limpia el "no estaba acá".</summary>
    [HttpPost("asignar")]
    public async Task<IActionResult> Asignar([FromBody] AsignarRequest req)
    {
        if (req?.Ids is null || req.Ids.Count == 0) return BadRequest(new { error = "No elegiste ningún producto." });
        var planta = string.IsNullOrWhiteSpace(req.Planta) ? null : req.Planta.Trim().ToUpperInvariant();
        if (planta is not null && !UbicacionHelper.PlantaValida(planta))
            return BadRequest(new { error = "Esa planta no existe." });
        var zona = planta is null ? null : UbicacionHelper.NormalizarZona(req.Zona);

        if (zona is not null)
        {
            var z = await _db.CafeUbicacionZonas.FirstOrDefaultAsync(x => x.Planta == planta && x.Codigo == zona);
            var nombre = string.IsNullOrWhiteSpace(req.ZonaNombre) ? null : req.ZonaNombre.Trim();
            if (z is null) _db.CafeUbicacionZonas.Add(new CafeUbicacionZona { Planta = planta!, Codigo = zona, Nombre = nombre });
            else if (nombre is not null) z.Nombre = nombre;
        }

        var prods = await _db.CafeProductos.Where(p => req.Ids.Contains(p.Id)).ToListAsync();
        var ahora = DateTime.UtcNow;
        foreach (var p in prods)
        {
            p.UbicacionPlanta = planta;
            p.UbicacionZona = zona;
            p.UbicacionAt = planta is null ? null : ahora;
            p.UbicacionPor = planta is null ? null : Usuario;
            p.UbicacionDudosaAt = null;
            p.UbicacionDudosaPor = null;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, cambiados = prods.Count, lugar = UbicacionHelper.Texto(planta, zona) });
    }

    public record ZonaNombreRequest(string Planta, string Codigo, string? Nombre);

    /// <summary>Cambia el nombre largo de una zona (el código corto no se toca).</summary>
    [HttpPost("zona-nombre")]
    public async Task<IActionResult> ZonaNombre([FromBody] ZonaNombreRequest req)
    {
        var codigo = UbicacionHelper.NormalizarZona(req?.Codigo);
        if (req is null || codigo is null || !UbicacionHelper.PlantaValida(req.Planta)) return BadRequest();
        var z = await _db.CafeUbicacionZonas.FirstOrDefaultAsync(x => x.Planta == req.Planta && x.Codigo == codigo);
        var nombre = string.IsNullOrWhiteSpace(req.Nombre) ? null : req.Nombre.Trim();
        if (z is null) _db.CafeUbicacionZonas.Add(new CafeUbicacionZona { Planta = req.Planta, Codigo = codigo, Nombre = nombre });
        else z.Nombre = nombre;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>"No está acá": alguien lo fue a buscar y no estaba. El lugar queda, marcado para revisar.</summary>
    [HttpPost("{id:int}/no-esta")]
    public async Task<IActionResult> NoEsta(int id)
    {
        var p = await _db.CafeProductos.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        p.UbicacionDudosaAt = DateTime.UtcNow;
        p.UbicacionDudosaPor = Usuario;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Quita la marca de "no estaba acá" (se revisó y el lugar está bien).</summary>
    [HttpPost("{id:int}/esta-ok")]
    public async Task<IActionResult> EstaOk(int id)
    {
        var p = await _db.CafeProductos.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return NotFound();
        p.UbicacionDudosaAt = null;
        p.UbicacionDudosaPor = null;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
