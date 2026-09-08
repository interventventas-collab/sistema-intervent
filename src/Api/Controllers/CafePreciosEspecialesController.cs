using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-09-08 — Precios pactados con un cliente puntual (Cafe_PreciosEspecialesCliente).
///
/// Pedido de Osmar: *"algunos clientes tienen precios especiales en productos puntuales,
/// por ejemplo Núcleo si compra vasos VT120 x bulto"*. Se cargan desde la ficha del cliente
/// y pisan el precio de catálogo cuando se carga una venta para ESE cliente.
///
/// El pacto es por (cliente, producto, FORMATO): "x bulto" no es lo mismo que "suelto".
/// Los precios se guardan SIN IVA, igual que PrecioBar / PrecioOtro del producto.
/// </summary>
[ApiController]
[Route("api/cafe/precios-especiales")]
[Authorize]
public class CafePreciosEspecialesController : ControllerBase
{
    private readonly AppDbContext _db;

    public CafePreciosEspecialesController(AppDbContext db) => _db = db;

    // ─────────────────────────────────────────────────────────────────────
    //  DTOs
    // ─────────────────────────────────────────────────────────────────────

    public record PrecioEspecialDto(
        int Id, int ProductoId, string? Sku, string ProductoNombre, string? Marca,
        string Formato, string FormatoLabel,
        decimal Precio, decimal PrecioConIva,
        decimal PrecioLista, decimal PrecioListaConIva,
        decimal DiferenciaPct, bool ListaMasBarata,
        string? Notas, DateTime CreatedAt, DateTime? UpdatedAt, string? CreatedBy);

    public record FormatoOpcionDto(string Formato, string Label, decimal PrecioLista, decimal PrecioListaConIva);

    public record OpcionesProductoDto(
        int ProductoId, string? Sku, string Nombre, string? Marca, string Categoria,
        List<FormatoOpcionDto> Formatos);

    public record GuardarRequest(int ClienteId, int ProductoId, string Formato, decimal Precio, string? Notas);

    public record ActualizarRequest(decimal Precio, string? Notas);

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────

    private async Task<CafeSetting> SettingsAsync()
        => await _db.CafeSettings.AsNoTracking().FirstOrDefaultAsync() ?? new CafeSetting { Id = 1 };

    /// <summary>Formatos que tienen sentido para un producto, con el precio de catálogo que
    /// hoy le saldría a ESE cliente. Sirve para que la ficha muestre "hoy paga $X" al lado
    /// del precio que se está pactando.</summary>
    private static List<FormatoOpcionDto> FormatosDe(CafeProducto p, string tipo, CafeSetting settings)
    {
        var res = new List<FormatoOpcionDto>();

        void Add(string formato)
        {
            var lista = CafePricingService.CalcularPrecioBreakdown(p, formato, tipo, settings).PrecioLista;
            res.Add(new FormatoOpcionDto(
                formato,
                CafePricingService.FormatoLabel(formato),
                lista,
                Math.Round(lista * (1m + p.IvaPct / 100m), 2, MidpointRounding.AwayFromZero)));
        }

        if (p.Categoria == "CAFE")
        {
            Add(CafePricingService.FORMATO_1KG);
            Add(CafePricingService.FORMATO_MEDIO);
            Add(CafePricingService.FORMATO_CUARTO);
            return res;
        }

        Add(CafePricingService.FORMATO_UNIT);

        // El bulto sólo se ofrece si el producto está configurado para venderse así:
        // sin UxB o sin precio de bulto, el motor ni siquiera deja cargar esa línea en la venta.
        if (p.UxB.HasValue && p.UxB.Value > 0 && (p.PrecioBulto.HasValue || p.PrecioBultoOtro.HasValue))
            Add(CafePricingService.FORMATO_BULTO);

        foreach (var pack in (p.Packs ?? new List<CafeProductoPack>())
                 .Where(x => x.IsActive).OrderBy(x => x.SortOrder).ThenBy(x => x.Cantidad))
            Add(CafePricingService.FORMATO_PACK_PREFIX + pack.Cantidad);

        return res;
    }

    private static string NormFormato(string? f)
        => string.IsNullOrWhiteSpace(f) ? CafePricingService.FORMATO_UNIT : f.Trim().ToUpperInvariant();

    // ─────────────────────────────────────────────────────────────────────
    //  GET — los pactos de un cliente
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet("cliente/{clienteId:int}")]
    public async Task<IActionResult> ListarPorCliente(int clienteId)
    {
        var cliente = await _db.CafeClientes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clienteId);
        if (cliente is null) return NotFound(new { error = "Cliente no encontrado" });

        var tipo = CafePricingService.ResolverTipo(cliente.Tipo);
        var settings = await SettingsAsync();

        var filas = await _db.CafePreciosEspecialesCliente.AsNoTracking()
            .Where(x => x.ClienteId == clienteId && x.IsActive)
            .ToListAsync();
        if (filas.Count == 0) return Ok(new List<PrecioEspecialDto>());

        var prodIds = filas.Select(f => f.ProductoId).Distinct().ToList();
        var productos = await _db.CafeProductos.AsNoTracking()
            .Include(p => p.OemNav)
            .Include(p => p.Packs)
            .Where(p => prodIds.Contains(p.Id))
            .ToListAsync();

        var res = new List<PrecioEspecialDto>();
        foreach (var f in filas)
        {
            var p = productos.FirstOrDefault(x => x.Id == f.ProductoId);
            if (p is null) continue;

            // Precio que le saldría HOY sin el pacto — para que se vea la diferencia.
            var lista = CafePricingService.CalcularPrecioBreakdown(p, f.Formato, tipo, settings).PrecioLista;
            var iva = 1m + p.IvaPct / 100m;
            var difPct = lista > 0m ? Math.Round((f.Precio - lista) / lista * 100m, 1) : 0m;

            res.Add(new PrecioEspecialDto(
                f.Id, p.Id, p.Sku, p.Nombre, p.Marca,
                f.Formato, CafePricingService.FormatoLabel(f.Formato),
                f.Precio, Math.Round(f.Precio * iva, 2, MidpointRounding.AwayFromZero),
                lista, Math.Round(lista * iva, 2, MidpointRounding.AwayFromZero),
                difPct, lista > 0m && lista < f.Precio,
                f.Notas, f.CreatedAt, f.UpdatedAt, f.CreatedBy));
        }

        return Ok(res.OrderBy(r => r.ProductoNombre).ThenBy(r => r.Formato).ToList());
    }

    // ─────────────────────────────────────────────────────────────────────
    //  GET — mapa liviano (producto + formato + precio) para la pantalla de Ventas.
    //  La venta lo pide una sola vez al elegir el cliente y con eso el preview del panel
    //  "agregar producto" muestra el precio pactado antes de agregar la linea.
    // ─────────────────────────────────────────────────────────────────────

    public record PrecioEspecialMapaDto(int ProductoId, string Formato, decimal Precio);

    [HttpGet("mapa/{clienteId:int}")]
    public async Task<IActionResult> Mapa(int clienteId)
    {
        var filas = await _db.CafePreciosEspecialesCliente.AsNoTracking()
            .Where(x => x.ClienteId == clienteId && x.IsActive)
            .Select(x => new PrecioEspecialMapaDto(x.ProductoId, x.Formato, x.Precio))
            .ToListAsync();
        return Ok(filas);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  GET — formatos disponibles de un producto + qué paga hoy ese cliente
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet("opciones")]
    public async Task<IActionResult> Opciones([FromQuery] int clienteId, [FromQuery] int productoId)
    {
        var cliente = await _db.CafeClientes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clienteId);
        if (cliente is null) return NotFound(new { error = "Cliente no encontrado" });

        var p = await _db.CafeProductos.AsNoTracking()
            .Include(x => x.OemNav)
            .Include(x => x.Packs)
            .FirstOrDefaultAsync(x => x.Id == productoId);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });

        var tipo = CafePricingService.ResolverTipo(cliente.Tipo);
        var settings = await SettingsAsync();

        return Ok(new OpcionesProductoDto(
            p.Id, p.Sku, p.Nombre, p.Marca, p.Categoria,
            FormatosDe(p, tipo, settings)));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  POST — pactar un precio (o repactar uno que ya existía)
    // ─────────────────────────────────────────────────────────────────────

    [HttpPost]
    public async Task<IActionResult> Guardar([FromBody] GuardarRequest req)
    {
        if (req.Precio < 0m) return BadRequest(new { error = "El precio no puede ser negativo" });

        var cliente = await _db.CafeClientes.FirstOrDefaultAsync(c => c.Id == req.ClienteId);
        if (cliente is null) return NotFound(new { error = "Cliente no encontrado" });

        var prod = await _db.CafeProductos
            .Include(p => p.Packs)
            .FirstOrDefaultAsync(p => p.Id == req.ProductoId);
        if (prod is null) return NotFound(new { error = "Producto no encontrado" });

        var formato = NormFormato(req.Formato);

        // Validar que el formato exista para este producto: un pacto sobre "bulto" de un
        // producto sin bulto configurado nunca se podría cobrar (la venta rechaza esa línea).
        var settings = await SettingsAsync();
        var tipo = CafePricingService.ResolverTipo(cliente.Tipo);
        var formatosOk = FormatosDe(prod, tipo, settings).Select(f => f.Formato).ToHashSet();
        if (!formatosOk.Contains(formato))
            return BadRequest(new { error = $"Este producto no se puede vender como '{CafePricingService.FormatoLabel(formato)}'" });

        // Si ya había un pacto para (cliente, producto, formato), se repisa en vez de duplicar.
        var fila = await _db.CafePreciosEspecialesCliente
            .FirstOrDefaultAsync(x => x.ClienteId == req.ClienteId
                                   && x.ProductoId == req.ProductoId
                                   && x.Formato == formato
                                   && x.IsActive);

        if (fila is null)
        {
            fila = new CafePrecioEspecialCliente
            {
                ClienteId = req.ClienteId,
                ProductoId = req.ProductoId,
                Formato = formato,
                Precio = Math.Round(req.Precio, 2, MidpointRounding.AwayFromZero),
                Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
                CreatedBy = User?.Identity?.Name
            };
            _db.CafePreciosEspecialesCliente.Add(fila);
        }
        else
        {
            fila.Precio = Math.Round(req.Precio, 2, MidpointRounding.AwayFromZero);
            fila.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
            fila.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        return Ok(new { id = fila.Id });
    }

    // ─────────────────────────────────────────────────────────────────────
    //  PUT — cambiar el precio pactado
    // ─────────────────────────────────────────────────────────────────────

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Actualizar(int id, [FromBody] ActualizarRequest req)
    {
        if (req.Precio < 0m) return BadRequest(new { error = "El precio no puede ser negativo" });

        var fila = await _db.CafePreciosEspecialesCliente.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (fila is null) return NotFound(new { error = "Precio especial no encontrado" });

        fila.Precio = Math.Round(req.Precio, 2, MidpointRounding.AwayFromZero);
        fila.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        fila.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ─────────────────────────────────────────────────────────────────────
    //  DELETE — sacar el pacto (queda la fila apagada, no se borra)
    // ─────────────────────────────────────────────────────────────────────

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var fila = await _db.CafePreciosEspecialesCliente.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (fila is null) return NotFound(new { error = "Precio especial no encontrado" });

        fila.IsActive = false;
        fila.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
