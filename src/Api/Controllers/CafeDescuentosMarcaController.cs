using System.Security.Claims;
using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-06 — Descuento fijo por MARCA para un cliente (Cafe_DescuentosMarcaCliente).
///
/// Pedido de Osmar: *"poder elegir en un cliente, que con una marca ya sea toda la marca o
/// productos seleccionados, tengan un descuento especial siempre para esa marca, por ejemplo
/// quiero que Dulce Lugar tenga un descuento x con marcas como Cremuccino siempre"*.
///
/// Decisiones suyas (06/11, sobre la maqueta):
///  - Vive dentro de Ventas → "Precios y descuentos especiales", pestaña "Descuentos por marca".
///  - En la venta entra como DESCUENTO DEL RENGLÓN (−10%): en la factura sale en "Bonif.".
///    Lo pone la pantalla de Nueva Venta al cargar el renglón; este controller no toca el cotizador.
///  - Si el producto tiene PRECIO PACTADO con el cliente, manda el pactado (no se suma el %).
///  - Al cargarlo, ofrece aplicarlo también a las otras sucursales (clientes con nombre parecido).
///  - Si deja menos de 40% sobre costo: AVISA, pero deja guardar y vender.
/// </summary>
[ApiController]
[Route("api/cafe/descuentos-marca")]
[Authorize]
public class CafeDescuentosMarcaController : ControllerBase
{
    private const decimal PisoGananciaPct = 40m;

    private readonly AppDbContext _db;
    private static bool _tablaOk;
    private static readonly SemaphoreSlim _lock = new(1, 1);

    public CafeDescuentosMarcaController(AppDbContext db) => _db = db;

    // ─────────────────────────────────────────────────────────────────────
    //  DTOs
    // ─────────────────────────────────────────────────────────────────────

    public record ProductoDescDto(int ProductoId, string? Sku, string Nombre,
        decimal PrecioHoy, decimal PrecioConDesc, decimal Costo, decimal? DejaPct, bool BajoPiso);

    public record DescuentoMarcaDto(
        int ClienteId, string ClienteNombre, string? ClienteCodigo,
        int MarcaId, string MarcaNombre, decimal DescuentoPct,
        bool TodaLaMarca, int ProductosMarca, List<ProductoDescDto> Productos,
        int BajoPiso, string? PeorNombre, decimal? PeorDejaPct);

    public record PreviewDto(int MarcaId, string MarcaNombre, bool BloqueaDescuento,
        decimal DescuentoPct, decimal MaxPctSinRomperPiso, List<ProductoDescDto> Productos);

    /// <summary>Lo que necesita Nueva Venta: % por producto (ya resuelto marca entera / elegidos).</summary>
    public record VentaProductoDto(int ProductoId, decimal DescuentoPct, string MarcaNombre);
    public record VentaResumenDto(string MarcaNombre, decimal DescuentoPct, bool TodaLaMarca, int Productos);
    public record VentaDto(List<VentaResumenDto> Resumen, List<VentaProductoDto> Productos);

    public record ClienteSimpleDto(int Id, string Nombre, string? Codigo);

    public record GuardarRequest(List<int> ClienteIds, int MarcaId, decimal DescuentoPct, List<int>? ProductoIds);

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────

    private async Task AsegurarTablaAsync()
    {
        if (_tablaOk) return;
        await _lock.WaitAsync();
        try
        {
            if (_tablaOk) return;
            await _db.Database.ExecuteSqlRawAsync(@"
IF OBJECT_ID('Cafe_DescuentosMarcaCliente') IS NULL
BEGIN
    CREATE TABLE Cafe_DescuentosMarcaCliente (
        Id INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ClienteId INT NOT NULL,
        MarcaId INT NOT NULL,
        ProductoId INT NULL,
        DescuentoPct DECIMAL(5,2) NOT NULL,
        CreatedAt DATETIME2 NOT NULL CONSTRAINT DF_DescMarcaCli_At DEFAULT SYSUTCDATETIME(),
        CreatedBy NVARCHAR(100) NULL
    );
    CREATE INDEX IX_DescMarcaCli_Cliente ON Cafe_DescuentosMarcaCliente (ClienteId, MarcaId);
END");
            _tablaOk = true;
        }
        finally { _lock.Release(); }
    }

    private async Task<CafeSetting> SettingsAsync()
        => await _db.CafeSettings.AsNoTracking().FirstOrDefaultAsync() ?? new CafeSetting { Id = 1 };

    /// <summary>Precio que hoy le sale a ese tipo de cliente en el formato "normal" del producto
    /// (1 kg para café, la unidad para el resto) y cuánto deja sobre costo con el descuento.</summary>
    private static ProductoDescDto Calcular(CafeProducto p, string tipo, CafeSetting settings, decimal pct)
    {
        var formato = p.Categoria == "CAFE" ? CafePricingService.FORMATO_1KG : CafePricingService.FORMATO_UNIT;
        var hoy = CafePricingService.CalcularPrecioBreakdown(p, formato, tipo, settings).PrecioLista;
        var con = Math.Round(hoy * (1m - pct / 100m), 2, MidpointRounding.AwayFromZero);
        decimal? deja = p.Costo > 0m && hoy > 0m
            ? Math.Round((con / p.Costo - 1m) * 100m, 1, MidpointRounding.AwayFromZero)
            : null;
        return new ProductoDescDto(p.Id, p.Sku, p.Nombre, hoy, con, p.Costo, deja, deja.HasValue && deja.Value < PisoGananciaPct);
    }

    private async Task<List<CafeProducto>> ProductosDeMarcaAsync(int marcaId)
        => await _db.CafeProductos.AsNoTracking()
            .Include(p => p.OemNav).Include(p => p.Packs)
            .Where(p => p.MarcaId == marcaId && p.IsActive)
            .OrderBy(p => p.Nombre)
            .ToListAsync();

    // ─────────────────────────────────────────────────────────────────────
    //  GET — todos los descuentos por marca (pestaña de la pantalla)
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        await AsegurarTablaAsync();
        var filas = await _db.CafeDescuentosMarcaCliente.AsNoTracking().ToListAsync();
        if (filas.Count == 0) return Ok(new List<DescuentoMarcaDto>());

        var settings = await SettingsAsync();
        var clienteIds = filas.Select(f => f.ClienteId).Distinct().ToList();
        var marcaIds = filas.Select(f => f.MarcaId).Distinct().ToList();
        var clientes = await _db.CafeClientes.AsNoTracking().Where(c => clienteIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre, c.Codigo, c.Tipo }).ToDictionaryAsync(c => c.Id);
        var marcas = await _db.CafeMarcas.AsNoTracking().Where(m => marcaIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Nombre);
        var productos = await _db.CafeProductos.AsNoTracking()
            .Include(p => p.OemNav).Include(p => p.Packs)
            .Where(p => p.MarcaId.HasValue && marcaIds.Contains(p.MarcaId.Value) && p.IsActive)
            .ToListAsync();

        var res = new List<DescuentoMarcaDto>();
        foreach (var g in filas.GroupBy(f => (f.ClienteId, f.MarcaId)))
        {
            if (!clientes.TryGetValue(g.Key.ClienteId, out var cli)) continue;
            var pct = g.First().DescuentoPct;
            var tipo = CafePricingService.ResolverTipo(cli.Tipo);
            var todaLaMarca = g.Any(f => f.ProductoId == null);
            var deMarca = productos.Where(p => p.MarcaId == g.Key.MarcaId).ToList();
            var elegidos = todaLaMarca ? deMarca
                : deMarca.Where(p => g.Any(f => f.ProductoId == p.Id)).ToList();
            var calc = elegidos.Select(p => Calcular(p, tipo, settings, pct)).OrderBy(x => x.Nombre).ToList();
            var peor = calc.Where(c => c.DejaPct.HasValue).OrderBy(c => c.DejaPct).FirstOrDefault();
            res.Add(new DescuentoMarcaDto(
                cli.Id, cli.Nombre, cli.Codigo,
                g.Key.MarcaId, marcas.TryGetValue(g.Key.MarcaId, out var mn) ? mn : $"Marca #{g.Key.MarcaId}",
                pct, todaLaMarca, deMarca.Count,
                todaLaMarca ? new List<ProductoDescDto>() : calc,
                calc.Count(c => c.BajoPiso), peor?.Nombre, peor?.DejaPct));
        }
        return Ok(res.OrderBy(r => r.ClienteNombre).ThenBy(r => r.MarcaNombre).ToList());
    }

    // ─────────────────────────────────────────────────────────────────────
    //  GET — productos de la marca con "hoy / con N% / deja" para el formulario
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet("preview")]
    public async Task<IActionResult> Preview([FromQuery] int marcaId, [FromQuery] decimal pct, [FromQuery] int? clienteId)
    {
        var marca = await _db.CafeMarcas.AsNoTracking().FirstOrDefaultAsync(m => m.Id == marcaId);
        if (marca is null) return NotFound(new { error = "Marca no encontrada" });
        pct = Math.Clamp(pct, 0m, 100m);

        var tipo = "BAR";
        if (clienteId is int cid && cid > 0)
        {
            var t = await _db.CafeClientes.AsNoTracking().Where(c => c.Id == cid).Select(c => c.Tipo).FirstOrDefaultAsync();
            if (t != null) tipo = CafePricingService.ResolverTipo(t);
        }
        var settings = await SettingsAsync();
        var prods = await ProductosDeMarcaAsync(marcaId);
        var calc = prods.Select(p => Calcular(p, tipo, settings, pct)).ToList();

        // Hasta qué % se puede llegar sin que NINGÚN producto de la marca baje del 40%.
        // precio×(1−d) ≥ costo×1,4  →  d ≤ 1 − costo×1,4/precio
        var limites = calc.Where(c => c.Costo > 0m && c.PrecioHoy > 0m)
            .Select(c => (1m - c.Costo * (1m + PisoGananciaPct / 100m) / c.PrecioHoy) * 100m).ToList();
        var max = limites.Count == 0 ? 0m : Math.Max(0m, Math.Floor(limites.Min()));

        return Ok(new PreviewDto(marca.Id, marca.Nombre, marca.BloqueaDescuento, pct, max, calc));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  GET — para Nueva Venta: % por producto de este cliente
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet("cliente/{clienteId:int}")]
    public async Task<IActionResult> DeCliente(int clienteId)
    {
        await AsegurarTablaAsync();
        var filas = await _db.CafeDescuentosMarcaCliente.AsNoTracking()
            .Where(f => f.ClienteId == clienteId).ToListAsync();
        if (filas.Count == 0) return Ok(new VentaDto(new(), new()));

        var marcaIds = filas.Select(f => f.MarcaId).Distinct().ToList();
        var marcas = await _db.CafeMarcas.AsNoTracking().Where(m => marcaIds.Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Nombre);
        var prods = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.MarcaId.HasValue && marcaIds.Contains(p.MarcaId.Value))
            .Select(p => new { p.Id, MarcaId = p.MarcaId!.Value }).ToListAsync();

        var resumen = new List<VentaResumenDto>();
        var porProducto = new List<VentaProductoDto>();
        foreach (var g in filas.GroupBy(f => f.MarcaId))
        {
            var nombre = marcas.TryGetValue(g.Key, out var mn) ? mn : $"Marca #{g.Key}";
            var pct = g.First().DescuentoPct;
            var toda = g.Any(f => f.ProductoId == null);
            var ids = toda
                ? prods.Where(p => p.MarcaId == g.Key).Select(p => p.Id).ToList()
                : g.Where(f => f.ProductoId.HasValue).Select(f => f.ProductoId!.Value).ToList();
            resumen.Add(new VentaResumenDto(nombre, pct, toda, ids.Count));
            porProducto.AddRange(ids.Select(id => new VentaProductoDto(id, pct, nombre)));
        }
        return Ok(new VentaDto(resumen.OrderBy(r => r.MarcaNombre).ToList(), porProducto));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  GET — otras sucursales del mismo cliente (nombre que empieza con las mismas 2 palabras)
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet("similares/{clienteId:int}")]
    public async Task<IActionResult> Similares(int clienteId)
    {
        var cli = await _db.CafeClientes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clienteId);
        if (cli is null) return NotFound();
        var palabras = (cli.Nombre ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (palabras.Length < 2) return Ok(new List<ClienteSimpleDto>());
        var prefijo = palabras[0] + " " + palabras[1];
        var res = await _db.CafeClientes.AsNoTracking()
            .Where(c => c.Id != clienteId && c.IsActive && c.Nombre.StartsWith(prefijo))
            .OrderBy(c => c.Nombre)
            .Select(c => new ClienteSimpleDto(c.Id, c.Nombre, c.Codigo))
            .Take(20).ToListAsync();
        return Ok(res);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  POST — guardar (reemplaza lo que ese cliente tenía con esa marca)
    // ─────────────────────────────────────────────────────────────────────

    [HttpPost]
    public async Task<IActionResult> Guardar([FromBody] GuardarRequest req)
    {
        await AsegurarTablaAsync();
        var clienteIds = (req.ClienteIds ?? new()).Where(i => i > 0).Distinct().ToList();
        if (clienteIds.Count == 0) return BadRequest(new { error = "Elegí el cliente." });
        if (req.DescuentoPct <= 0m || req.DescuentoPct >= 100m)
            return BadRequest(new { error = "El descuento tiene que ser mayor a 0 y menor a 100." });

        var marca = await _db.CafeMarcas.AsNoTracking().FirstOrDefaultAsync(m => m.Id == req.MarcaId);
        if (marca is null) return BadRequest(new { error = "Elegí la marca." });
        if (marca.BloqueaDescuento)
            return BadRequest(new { error = $"La marca {marca.Nombre} tiene los descuentos bloqueados (es marca propia)." });

        var existen = await _db.CafeClientes.AsNoTracking().Where(c => clienteIds.Contains(c.Id)).Select(c => c.Id).ToListAsync();
        if (existen.Count != clienteIds.Count) return BadRequest(new { error = "Algún cliente no existe." });

        List<int>? productoIds = null;
        if (req.ProductoIds is { Count: > 0 })
        {
            productoIds = await _db.CafeProductos.AsNoTracking()
                .Where(p => req.ProductoIds.Contains(p.Id) && p.MarcaId == req.MarcaId)
                .Select(p => p.Id).ToListAsync();
            if (productoIds.Count == 0) return BadRequest(new { error = "Tildá al menos un producto de la marca." });
        }

        var quien = User.FindFirstValue(ClaimTypes.Name);
        var pct = Math.Round(req.DescuentoPct, 2);
        // Un solo SaveChanges = todo o nada (la estrategia de reintentos no acepta transacción manual).
        var viejas = await _db.CafeDescuentosMarcaCliente
            .Where(f => clienteIds.Contains(f.ClienteId) && f.MarcaId == req.MarcaId).ToListAsync();
        _db.CafeDescuentosMarcaCliente.RemoveRange(viejas);
        foreach (var cid in clienteIds)
        {
            if (productoIds is null)
                _db.CafeDescuentosMarcaCliente.Add(new CafeDescuentoMarcaCliente
                { ClienteId = cid, MarcaId = req.MarcaId, ProductoId = null, DescuentoPct = pct, CreatedBy = quien });
            else
                foreach (var pid in productoIds)
                    _db.CafeDescuentosMarcaCliente.Add(new CafeDescuentoMarcaCliente
                    { ClienteId = cid, MarcaId = req.MarcaId, ProductoId = pid, DescuentoPct = pct, CreatedBy = quien });
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, clientes = clienteIds.Count });
    }

    [HttpDelete]
    public async Task<IActionResult> Borrar([FromQuery] int clienteId, [FromQuery] int marcaId)
    {
        await AsegurarTablaAsync();
        var filas = await _db.CafeDescuentosMarcaCliente
            .Where(f => f.ClienteId == clienteId && f.MarcaId == marcaId).ToListAsync();
        if (filas.Count == 0) return NotFound();
        _db.CafeDescuentosMarcaCliente.RemoveRange(filas);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }
}
