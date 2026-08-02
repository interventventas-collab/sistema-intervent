using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Camino al picking (escalon 1, 2026-08-02) — lado ADMIN (requiere login).
/// Crea listas de picking CONGELADAS a partir de los pedidos tildados en /cafe/preparacion
/// y administra cual es el celular del deposito habilitado para armar.
/// El checklist en si vive en el controller publico <c>CafePickingPublicController</c>.
/// </summary>
[ApiController]
[Route("api/cafe/picking")]
[Authorize]
public class CafePickingController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafePickingController(AppDbContext db) { _db = db; }

    public record CrearPickingRequest(List<int> VentaIds, string? Operador);

    /// <summary>Crea una lista congelada sumando los productos de los pedidos indicados,
    /// agrupando por nombre + formato + molienda (decision del usuario 2026-08-02).</summary>
    [HttpPost("crear")]
    public async Task<IActionResult> Crear([FromBody] CrearPickingRequest req)
    {
        if (req?.VentaIds is null || req.VentaIds.Count == 0)
            return BadRequest(new { error = "No hay pedidos seleccionados" });

        var ventas = await _db.CafeVentas
            .Include(v => v.Items)
            .Where(v => req.VentaIds.Contains(v.Id))
            .ToListAsync();
        if (ventas.Count == 0) return BadRequest(new { error = "No se encontraron los pedidos" });

        // SKU por producto (solo informativo, para reconocer el producto en el checklist)
        var prodIds = ventas.SelectMany(v => v.Items)
            .Where(i => i.ProductoId != null).Select(i => i.ProductoId!.Value).Distinct().ToList();
        var skuPorProducto = await _db.CafeProductos
            .Where(p => prodIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku })
            .ToDictionaryAsync(x => x.Id, x => x.Sku);

        // Agrupar por nombre + formato + molienda, sumando cantidades
        var grupos = ventas.SelectMany(v => v.Items)
            .GroupBy(i => new
            {
                Nombre = i.ProductoNombreSnapshot ?? "",
                Formato = i.Formato ?? "",
                Molienda = i.Molienda ?? ""
            })
            .Select(g => new
            {
                g.Key.Nombre,
                g.Key.Formato,
                g.Key.Molienda,
                Categoria = g.Select(x => x.Categoria).FirstOrDefault(),
                Sku = g.Select(x => x.ProductoId != null && skuPorProducto.ContainsKey(x.ProductoId.Value)
                        ? skuPorProducto[x.ProductoId.Value] : null)
                       .FirstOrDefault(s => !string.IsNullOrEmpty(s)),
                Cantidad = g.Sum(x => x.Cantidad)
            })
            .OrderBy(g => g.Categoria)
            .ThenBy(g => g.Nombre)
            .ThenBy(g => g.Formato)
            .ToList();

        var lista = new CafePickingLista
        {
            Token = Guid.NewGuid().ToString("N"),
            CreadaPor = req.Operador,
            CantidadPedidos = ventas.Count,
            VentaNumeros = string.Join(", ", ventas.OrderBy(v => v.Numero).Select(v => v.Numero))
        };
        int orden = 0;
        foreach (var g in grupos)
        {
            lista.Items.Add(new CafePickingItem
            {
                Orden = orden++,
                ProductoNombre = g.Nombre,
                Formato = string.IsNullOrEmpty(g.Formato) ? null : g.Formato,
                Molienda = string.IsNullOrEmpty(g.Molienda) ? null : g.Molienda,
                Sku = g.Sku,
                Categoria = g.Categoria,
                Cantidad = g.Cantidad
            });
        }
        _db.CafePickingListas.Add(lista);
        await _db.SaveChangesAsync();

        return Ok(new { token = lista.Token, cantidadPedidos = lista.CantidadPedidos, cantidadItems = lista.Items.Count });
    }

    /// <summary>Devuelve el celu del deposito habilitado (si hay), para mostrarlo en /cafe/preparacion.</summary>
    [HttpGet("dispositivo")]
    public async Task<IActionResult> GetDispositivo()
    {
        var d = await _db.CafePickingDispositivos.Where(x => x.Activo).OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (d is null) return Ok(new { registrado = false });
        return Ok(new { registrado = true, nombre = d.Nombre, createdAt = d.CreatedAt, lastSeenAt = d.LastSeenAt });
    }

    /// <summary>"Olvida" el celu del deposito actual — para cuando cambian de aparato.
    /// El proximo celu que abra una lista podra habilitarse.</summary>
    [HttpDelete("dispositivo")]
    public async Task<IActionResult> OlvidarDispositivo()
    {
        var activos = await _db.CafePickingDispositivos.Where(x => x.Activo).ToListAsync();
        foreach (var d in activos) d.Activo = false;
        await _db.SaveChangesAsync();
        return Ok(new { olvidados = activos.Count });
    }
}
