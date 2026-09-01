using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Control de precios de packs (2026-09-01).
///
/// Un pack (Cafe_ProductoPacks) puede tener PrecioOverride — un precio escrito a mano que
/// NO sigue al del producto. Es lo normal: al 01/09/2026, 83 de 87 packs activos lo tienen.
/// El problema es que cuando sube el precio del producto, esos packs quedan con el precio
/// viejo y nadie se entera: se sigue vendiendo por debajo.
///
/// Esta pantalla compara, para cada pack, el precio a mano contra lo que daria siguiendo al
/// producto (precio_unitario x cantidad) y muestra el desvio. No decide nada sola: el usuario
/// mira y corrige, porque un pack mas barato que la suma de sus unidades tambien puede ser
/// intencional (descuento por volumen).
/// </summary>
[ApiController]
[Route("api/cafe/packs-control")]
[Authorize]
public class CafePacksControlController : ControllerBase
{
    private readonly AppDbContext _db;

    public CafePacksControlController(AppDbContext db)
    {
        _db = db;
    }

    public class PackControlDto
    {
        public int PackId { get; set; }
        public int ProductoId { get; set; }
        public string? Sku { get; set; }
        public string ProductoNombre { get; set; } = "";
        public string? Marca { get; set; }
        public int Cantidad { get; set; }
        public string PackNombre { get; set; } = "";
        /// <summary>Precio escrito a mano. null = el pack sigue al producto.</summary>
        public decimal? PrecioAMano { get; set; }
        /// <summary>Precio unitario vigente del producto (PrecioOtro, o PrecioBar si no hay).</summary>
        public decimal? PrecioUnitario { get; set; }
        /// <summary>Lo que costaria el pack si siguiera al producto: PrecioUnitario x Cantidad.</summary>
        public decimal? PrecioCalculado { get; set; }
        /// <summary>Diferencia en % entre el precio a mano y el calculado. null si no se puede comparar.
        /// Negativo = el pack se vende MAS BARATO que la suma de sus unidades.</summary>
        public decimal? DesvioPct { get; set; }
    }

    public class ActualizarPrecioPackRequest
    {
        /// <summary>Nuevo precio a mano. null + SeguirAlProducto=true borra el override.</summary>
        public decimal? PrecioAMano { get; set; }
        /// <summary>Si es true, se borra el precio a mano y el pack pasa a seguir al producto.</summary>
        public bool SeguirAlProducto { get; set; }
    }

    /// <summary>Todos los packs activos de productos activos, con la comparacion ya calculada.</summary>
    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        var packs = await _db.CafeProductoPacks
            .Where(pk => pk.IsActive && pk.Producto != null && pk.Producto.IsActive)
            .Select(pk => new
            {
                pk.Id,
                pk.ProductoId,
                pk.Cantidad,
                pk.Nombre,
                pk.PrecioOverride,
                pk.Producto!.Sku,
                ProdNombre = pk.Producto.Nombre,
                Marca = pk.Producto.Marca ?? (pk.Producto.MarcaNav != null ? pk.Producto.MarcaNav.Nombre : null),
                pk.Producto.PrecioOtro,
                pk.Producto.PrecioBar,
            })
            .ToListAsync();

        var items = packs.Select(pk =>
        {
            // Mismo criterio que el resto del sistema para un producto OTROS: manda PrecioOtro,
            // y si no esta cargado se cae a PrecioBar.
            var unitario = pk.PrecioOtro ?? pk.PrecioBar;
            decimal? calculado = unitario.HasValue && pk.Cantidad > 0
                ? Math.Round(unitario.Value * pk.Cantidad, 2)
                : null;
            decimal? desvio = pk.PrecioOverride.HasValue && calculado.HasValue && calculado.Value > 0
                ? Math.Round((pk.PrecioOverride.Value - calculado.Value) * 100m / calculado.Value, 1)
                : null;

            return new PackControlDto
            {
                PackId = pk.Id,
                ProductoId = pk.ProductoId,
                Sku = pk.Sku,
                ProductoNombre = pk.ProdNombre,
                Marca = pk.Marca,
                Cantidad = pk.Cantidad,
                PackNombre = pk.Nombre,
                PrecioAMano = pk.PrecioOverride,
                PrecioUnitario = unitario,
                PrecioCalculado = calculado,
                DesvioPct = desvio,
            };
        })
        // Los mas baratos respecto del producto arriba: son los candidatos a "quedo viejo".
        // Los que no se pueden comparar van al final.
        .OrderBy(x => x.DesvioPct ?? decimal.MaxValue)
        .ThenBy(x => x.Sku)
        .ThenBy(x => x.Cantidad)
        .ToList();

        return Ok(items);
    }

    /// <summary>Cambia el precio a mano de UN pack, o se lo saca para que vuelva a seguir al producto.</summary>
    [HttpPut("{packId:int}")]
    public async Task<IActionResult> ActualizarPrecio(int packId, [FromBody] ActualizarPrecioPackRequest req)
    {
        var pack = await _db.CafeProductoPacks.FirstOrDefaultAsync(x => x.Id == packId);
        if (pack is null) return NotFound(new { error = "No encontre ese pack" });

        if (req.SeguirAlProducto)
        {
            pack.PrecioOverride = null;
        }
        else
        {
            if (!req.PrecioAMano.HasValue)
                return BadRequest(new { error = "Falta el precio" });
            if (req.PrecioAMano.Value < 0)
                return BadRequest(new { error = "El precio no puede ser negativo" });
            pack.PrecioOverride = Math.Round(req.PrecioAMano.Value, 2);
        }

        await _db.SaveChangesAsync();
        return Ok(new { pack.Id, pack.PrecioOverride });
    }
}
