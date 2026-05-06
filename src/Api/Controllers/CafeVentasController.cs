using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/ventas")]
[Authorize]
public class CafeVentasController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly string[] FormatosValidos = { "1KG", "MEDIO", "CUARTO", "UNIT" };

    public CafeVentasController(AppDbContext db) { _db = db; }

    private static CafeVentaDto Map(CafeVenta v) => new(
        v.Id, v.Numero, v.Fecha,
        v.ClienteId, v.ClienteNombreSnapshot, v.ClienteTipoSnapshot, v.ClienteTelefonoSnapshot,
        v.Subtotal, v.Descuento, v.Total, v.CostoTotal, v.Margen,
        v.Observaciones, v.Estado,
        v.WeekDays, v.IsPaid,
        v.TipoComprobante, v.CondicionIva, v.CondicionPago,
        v.CreatedAt,
        v.Items.Select(i => new CafeVentaItemDto(
            i.Id, i.ProductoId, i.ProductoNombreSnapshot, i.Categoria,
            i.Formato, i.Cantidad,
            i.PrecioUnitario, i.CostoUnitario, i.Subtotal,
            i.GramosDescontados,
            i.Molienda, i.EsDoyPack,
            i.DescuentoPct)).ToList());

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] DateTime? from = null, [FromQuery] DateTime? to = null)
    {
        var q = _db.CafeVentas.Include(v => v.Items).AsQueryable();
        if (from.HasValue) q = q.Where(v => v.Fecha >= from.Value.Date);
        if (to.HasValue) q = q.Where(v => v.Fecha <= to.Value.Date);
        var list = await q.OrderByDescending(v => v.Fecha).ThenByDescending(v => v.Id).Take(200).ToListAsync();
        return Ok(list.Select(Map).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        return Ok(Map(v));
    }

    /// <summary>Devuelve los productos que mas compro un cliente (combinacion ProductoId+Formato),
    /// ordenados por cantidad de comprobantes en los que aparecio. Solo cuenta ventas no anuladas.</summary>
    [HttpGet("top-productos-cliente/{clienteId:int}")]
    public async Task<IActionResult> GetTopProductosByCliente(int clienteId, [FromQuery] int count = 10)
    {
        if (clienteId <= 0) return Ok(new List<CafeTopProductoClienteDto>());
        if (count <= 0) count = 10;

        var grouped = await _db.CafeVentaItems
            .Where(i => i.VentaNav != null
                        && i.VentaNav.ClienteId == clienteId
                        && i.VentaNav.Estado != "anulado")
            .GroupBy(i => new { i.ProductoId, i.Formato })
            .Select(g => new
            {
                g.Key.ProductoId,
                g.Key.Formato,
                TimesOrdered = g.Select(x => x.VentaId).Distinct().Count(),
                TotalQuantity = g.Sum(x => x.Cantidad),
                LastPurchase = g.Max(x => x.VentaNav!.Fecha)
            })
            .OrderByDescending(x => x.TimesOrdered)
            .ThenByDescending(x => x.TotalQuantity)
            .ThenByDescending(x => x.LastPurchase)
            .Take(count)
            .ToListAsync();

        if (grouped.Count == 0) return Ok(new List<CafeTopProductoClienteDto>());

        var ids = grouped.Select(x => x.ProductoId).Distinct().ToList();
        var productos = await _db.CafeProductos
            .Where(p => ids.Contains(p.Id) && p.IsActive)
            .ToListAsync();

        var cliente = await _db.CafeClientes.FindAsync(clienteId);
        var tipo = CafePricingService.ResolverTipo(cliente?.Tipo);
        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };

        var result = new List<CafeTopProductoClienteDto>();
        foreach (var g in grouped)
        {
            var p = productos.FirstOrDefault(x => x.Id == g.ProductoId);
            if (p is null) continue;
            var precio = CafePricingService.CalcularPrecioUnitario(p, g.Formato, tipo, settings);
            result.Add(new CafeTopProductoClienteDto(
                p.Id, p.Sku, p.Nombre, p.Categoria, p.Marca,
                g.Formato,
                g.TimesOrdered, g.TotalQuantity, g.LastPurchase,
                p.StockGramos, p.StockUnidades, precio));
        }
        return Ok(result);
    }

    /// <summary>Cotización en vivo: NO crea la venta, solo calcula precios + verifica stock.</summary>
    [HttpPost("cotizar")]
    public async Task<IActionResult> Cotizar([FromBody] CafeCotizarRequest req)
    {
        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        var tipo = await ResolverTipoAsync(req.ClienteId, req.ClienteTipo);
        return Ok(await CotizarInternoAsync(req.Items, tipo, req.Descuento, settings));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeVentaRequest req)
    {
        if (req.Items is null || req.Items.Count == 0)
            return BadRequest(new { error = "La venta debe tener al menos un item" });

        var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
        var tipo = await ResolverTipoAsync(req.ClienteId, req.ClienteTipoOverride);

        var cot = await CotizarInternoAsync(req.Items, tipo, req.Descuento, settings);
        if (!cot.TodoOk)
            return BadRequest(new { error = "No hay stock suficiente para alguno de los items. Revisá la cotización." });

        // Resolver datos del cliente
        string? clienteNombre = null;
        string? clienteTelefono = null;
        if (req.ClienteId.HasValue && req.ClienteId.Value > 0)
        {
            var cli = await _db.CafeClientes.FindAsync(req.ClienteId.Value);
            if (cli is null) return BadRequest(new { error = "Cliente no encontrado" });
            clienteNombre = cli.Nombre;
            clienteTelefono = cli.Telefono;
            tipo = CafePricingService.ResolverTipo(cli.Tipo);
        }
        else
        {
            clienteNombre = string.IsNullOrWhiteSpace(req.ClienteNombreOverride) ? "Consumidor final" : req.ClienteNombreOverride.Trim();
        }

        // Persistir: crear venta + items + descontar stock
        var venta = new CafeVenta
        {
            Numero = await GenerarNumeroAsync(),
            Fecha = (req.Fecha ?? DateTime.Today).Date,
            ClienteId = req.ClienteId.HasValue && req.ClienteId.Value > 0 ? req.ClienteId.Value : null,
            ClienteNombreSnapshot = clienteNombre,
            ClienteTipoSnapshot = tipo,
            ClienteTelefonoSnapshot = clienteTelefono,
            Subtotal = cot.Subtotal,
            Descuento = cot.Descuento,
            Total = cot.Total,
            CostoTotal = cot.CostoTotal,
            Margen = cot.Margen,
            Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim(),
            Estado = "emitido",
            WeekDays = NormWeekDays(req.WeekDays),
            IsPaid = req.IsPaid,
            TipoComprobante = NormTipoComprobante(req.TipoComprobante),
            CondicionIva = NormCondicionIva(req.CondicionIva),
            CondicionPago = NormCondicionPago(req.CondicionPago),
            CreatedAt = DateTime.UtcNow
        };

        // Mapear items + descontar stock fisico
        foreach (var it in cot.Items)
        {
            var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
            if (prod is null) return BadRequest(new { error = $"Producto {it.ProductoId} no encontrado" });

            venta.Items.Add(new CafeVentaItem
            {
                ProductoId = prod.Id,
                ProductoNombreSnapshot = prod.Nombre,
                Categoria = prod.Categoria,
                Formato = it.Formato,
                Cantidad = it.Cantidad,
                PrecioUnitario = it.PrecioUnitario,
                CostoUnitario = it.CostoUnitario,
                Subtotal = it.Subtotal,
                GramosDescontados = it.GramosNecesarios,
                Molienda = NormMolienda(it.Molienda),
                EsDoyPack = it.EsDoyPack && prod.Categoria == "CAFE",  // doy pack solo aplica a cafe
                DescuentoPct = it.DescuentoPct
            });

            // Descontar stock
            if (prod.Categoria == "CAFE")
                prod.StockGramos = Math.Max(0m, prod.StockGramos - it.GramosNecesarios);
            else
                prod.StockUnidades = Math.Max(0, prod.StockUnidades - it.Cantidad);
            prod.UpdatedAt = DateTime.UtcNow;
        }

        _db.CafeVentas.Add(venta);
        await _db.SaveChangesAsync();

        return Ok(Map(venta));
    }

    /// <summary>Toggle de "pagado" y/o "dias de la semana" sobre una venta ya emitida (sin recalcular precios).</summary>
    [HttpPut("{id:int}/flags")]
    public async Task<IActionResult> UpdateFlags(int id, [FromBody] UpdateCafeVentaFlagsRequest req)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        if (req.WeekDays is not null) v.WeekDays = NormWeekDays(req.WeekDays);
        if (req.IsPaid.HasValue) v.IsPaid = req.IsPaid.Value;
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(v));
    }

    [HttpPost("{id:int}/anular")]
    public async Task<IActionResult> Anular(int id)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        if (v.Estado == "anulado") return BadRequest(new { error = "Ya estaba anulada" });

        // Restaurar stock
        foreach (var it in v.Items)
        {
            var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
            if (prod is null) continue;
            if (prod.Categoria == "CAFE")
                prod.StockGramos += it.GramosDescontados;
            else
                prod.StockUnidades += it.Cantidad;
            prod.UpdatedAt = DateTime.UtcNow;
        }
        v.Estado = "anulado";
        v.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(v));
    }

    /// <summary>Devuelve quien puede eliminar y el hint de la clave (sin la clave en si).</summary>
    [HttpGet("delete-settings")]
    public async Task<IActionResult> GetDeleteSettings()
    {
        var keys = new[] { "sales.delete_allowed_operator", "sales.delete_password_hint" };
        var settings = await _db.AppSettings.Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        return Ok(new DeleteCafeVentaSettingsDto(
            settings.GetValueOrDefault("sales.delete_allowed_operator", "OSMAR"),
            settings.GetValueOrDefault("sales.delete_password_hint", "")
        ));
    }

    /// <summary>Eliminacion definitiva de UNA venta. Requiere operador permitido + clave.</summary>
    [HttpPost("{id:int}/delete")]
    public async Task<IActionResult> Delete(int id, [FromBody] DeleteCafeVentaRequest req)
    {
        var op = HttpContext.Request.Headers["X-Operator-Name"].ToString();
        try
        {
            await ValidateDeletePermissionAsync(op, req.Password);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }

        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        await DeleteVentaInternalAsync(v);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true });
    }

    /// <summary>Eliminacion masiva. Requiere operador permitido + clave.</summary>
    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteCafeVentasRequest req)
    {
        if (req.Ids is null || req.Ids.Count == 0)
            return BadRequest(new { error = "No hay ventas seleccionadas" });

        var op = HttpContext.Request.Headers["X-Operator-Name"].ToString();
        try
        {
            await ValidateDeletePermissionAsync(op, req.Password);
        }
        catch (UnauthorizedAccessException ex) { return StatusCode(403, new { error = ex.Message }); }

        var ventas = await _db.CafeVentas.Include(x => x.Items)
            .Where(v => req.Ids.Contains(v.Id))
            .ToListAsync();

        foreach (var v in ventas)
            await DeleteVentaInternalAsync(v);

        await _db.SaveChangesAsync();
        return Ok(new { deleted = ventas.Count });
    }

    /// <summary>Edita una venta. Si Items != null y la venta esta emitida, reemplaza items + recalcula
    /// precios + ajusta stock (devuelve los viejos, descuenta los nuevos). Si Items es null, solo metadata.</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeVentaRequest req)
    {
        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        if (req.Fecha.HasValue) v.Fecha = req.Fecha.Value.Date;
        if (req.Observaciones is not null)
            v.Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim();
        if (req.TipoComprobante is not null) v.TipoComprobante = NormTipoComprobante(req.TipoComprobante);
        if (req.CondicionIva is not null) v.CondicionIva = NormCondicionIva(req.CondicionIva);
        if (req.CondicionPago is not null) v.CondicionPago = NormCondicionPago(req.CondicionPago);
        if (req.WeekDays is not null) v.WeekDays = NormWeekDays(req.WeekDays);
        if (req.IsPaid.HasValue) v.IsPaid = req.IsPaid.Value;

        // Cliente: si mandaron ClienteId valido > 0, vinculo al cliente y refresco snapshot.
        // Si mandaron 0 o null + override, dejo como manual (consumidor final / nombre libre).
        if (req.ClienteId.HasValue)
        {
            if (req.ClienteId.Value > 0)
            {
                var cli = await _db.CafeClientes.FindAsync(req.ClienteId.Value);
                if (cli is null) return BadRequest(new { error = "Cliente no encontrado" });
                v.ClienteId = cli.Id;
                v.ClienteNombreSnapshot = cli.Nombre;
                v.ClienteTipoSnapshot = CafePricingService.ResolverTipo(cli.Tipo);
                v.ClienteTelefonoSnapshot = cli.Telefono;
            }
            else
            {
                v.ClienteId = null;
                v.ClienteNombreSnapshot = string.IsNullOrWhiteSpace(req.ClienteNombreOverride)
                    ? "Consumidor final" : req.ClienteNombreOverride.Trim();
                v.ClienteTipoSnapshot = CafePricingService.ResolverTipo(req.ClienteTipoOverride);
                v.ClienteTelefonoSnapshot = null;
            }
        }
        else if (!v.ClienteId.HasValue && !string.IsNullOrWhiteSpace(req.ClienteNombreOverride))
        {
            v.ClienteNombreSnapshot = req.ClienteNombreOverride.Trim();
            if (!string.IsNullOrWhiteSpace(req.ClienteTipoOverride))
                v.ClienteTipoSnapshot = CafePricingService.ResolverTipo(req.ClienteTipoOverride);
        }

        // Items: si se envian, reemplazar + ajustar stock + recalcular totales.
        // Solo aplicable si la venta no esta anulada.
        if (req.Items is not null)
        {
            if (v.Estado != "emitido")
                return BadRequest(new { error = "No se pueden modificar los items de una venta anulada" });
            if (req.Items.Count == 0)
                return BadRequest(new { error = "La venta debe tener al menos un item" });

            using var tx = await _db.Database.BeginTransactionAsync();
            try
            {
                // 1. Devolver stock de los items actuales.
                foreach (var item in v.Items)
                {
                    var prod = await _db.CafeProductos.FindAsync(item.ProductoId);
                    if (prod is null) continue;
                    if (prod.Categoria == "CAFE") prod.StockGramos += item.GramosDescontados;
                    else prod.StockUnidades += item.Cantidad;
                    prod.UpdatedAt = DateTime.UtcNow;
                }
                _db.CafeVentaItems.RemoveRange(v.Items);
                v.Items.Clear();
                await _db.SaveChangesAsync();

                // 2. Cotizar items nuevos contra el stock recien restaurado.
                var settings = await _db.CafeSettings.FindAsync(1) ?? new CafeSetting { Id = 1 };
                var tipo = v.ClienteTipoSnapshot ?? "OTRO";
                var descuentoNuevo = req.Descuento ?? v.Descuento;
                var cot = await CotizarInternoAsync(req.Items, tipo, descuentoNuevo, settings);
                if (!cot.TodoOk)
                {
                    await tx.RollbackAsync();
                    return BadRequest(new { error = "No hay stock suficiente para alguno de los items nuevos." });
                }

                // 3. Persistir items nuevos + descontar stock.
                foreach (var ci in cot.Items)
                {
                    var prod = await _db.CafeProductos.FindAsync(ci.ProductoId);
                    if (prod is null) return BadRequest(new { error = $"Producto {ci.ProductoId} no encontrado" });
                    v.Items.Add(new CafeVentaItem
                    {
                        ProductoId = prod.Id,
                        ProductoNombreSnapshot = prod.Nombre,
                        Categoria = prod.Categoria,
                        Formato = ci.Formato,
                        Cantidad = ci.Cantidad,
                        PrecioUnitario = ci.PrecioUnitario,
                        CostoUnitario = ci.CostoUnitario,
                        Subtotal = ci.Subtotal,
                        GramosDescontados = ci.GramosNecesarios,
                        Molienda = NormMolienda(ci.Molienda),
                        EsDoyPack = ci.EsDoyPack && prod.Categoria == "CAFE",
                        DescuentoPct = ci.DescuentoPct
                    });
                    if (prod.Categoria == "CAFE")
                        prod.StockGramos = Math.Max(0m, prod.StockGramos - ci.GramosNecesarios);
                    else
                        prod.StockUnidades = Math.Max(0, prod.StockUnidades - ci.Cantidad);
                    prod.UpdatedAt = DateTime.UtcNow;
                }

                v.Subtotal = cot.Subtotal;
                v.Descuento = cot.Descuento;
                v.Total = cot.Total;
                v.CostoTotal = cot.CostoTotal;
                v.Margen = cot.Margen;
                v.UpdatedAt = DateTime.UtcNow;
                await _db.SaveChangesAsync();
                await tx.CommitAsync();
            }
            catch
            {
                await tx.RollbackAsync();
                throw;
            }
        }
        else if (req.Descuento.HasValue)
        {
            // Solo cambio el descuento global sin tocar items.
            var d = Math.Max(0m, req.Descuento.Value);
            v.Descuento = d;
            v.Total = Math.Max(0m, v.Subtotal - d);
            v.Margen = v.Total - v.CostoTotal;
            v.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        else
        {
            v.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }

        return Ok(Map(v));
    }

    private async Task ValidateDeletePermissionAsync(string operatorName, string password)
    {
        var allowedOp = (await _db.AppSettings.FindAsync("sales.delete_allowed_operator"))?.Value ?? "OSMAR";
        var expectedPassword = (await _db.AppSettings.FindAsync("sales.delete_password"))?.Value ?? "";

        if (!string.Equals(operatorName ?? "", allowedOp, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Solo {allowedOp} puede eliminar comprobantes.");
        if (string.IsNullOrEmpty(expectedPassword) || password != expectedPassword)
            throw new UnauthorizedAccessException("Clave incorrecta.");
    }

    private async Task DeleteVentaInternalAsync(CafeVenta v)
    {
        // Si estaba emitida, restaurar stock antes de borrar.
        if (v.Estado == "emitido")
        {
            foreach (var it in v.Items)
            {
                var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
                if (prod is null) continue;
                if (prod.Categoria == "CAFE") prod.StockGramos += it.GramosDescontados;
                else prod.StockUnidades += it.Cantidad;
            }
        }
        _db.CafeVentas.Remove(v);
    }

    // ============================================================
    // INTERNAS
    // ============================================================

    private async Task<string> ResolverTipoAsync(int? clienteId, string? tipoOverride)
    {
        if (clienteId.HasValue && clienteId.Value > 0)
        {
            var c = await _db.CafeClientes.FindAsync(clienteId.Value);
            if (c is not null) return CafePricingService.ResolverTipo(c.Tipo);
        }
        return CafePricingService.ResolverTipo(tipoOverride);
    }

    private async Task<CafeCotizadoDto> CotizarInternoAsync(List<CafeCotizarItemRequest> items, string tipo, decimal descuento, CafeSetting settings)
    {
        var cotizadoItems = new List<CafeCotizadoItemDto>();
        decimal subtotal = 0m, costoTotal = 0m;
        bool todoOk = true;

        // Pre-cargar marcas (para margen y bloqueo de descuento) y descuentos por canal+marca.
        var marcas = await _db.CafeMarcas.ToDictionaryAsync(m => m.Id);
        var descuentosMatriz = await _db.CafeDescuentosCliente.ToListAsync();

        decimal ResolverDescuentoMatriz(int? marcaId)
        {
            // Si la marca bloquea descuentos, 0.
            if (marcaId.HasValue && marcas.TryGetValue(marcaId.Value, out var ma) && ma.BloqueaDescuento) return 0m;
            // Override por marca.
            var override_ = descuentosMatriz.FirstOrDefault(d => d.TipoCliente == tipo && d.MarcaId == marcaId);
            if (override_ is not null) return override_.DescuentoPct;
            // Default por tipo (MarcaId null).
            var general = descuentosMatriz.FirstOrDefault(d => d.TipoCliente == tipo && d.MarcaId == null);
            return general?.DescuentoPct ?? 0m;
        }

        foreach (var it in items)
        {
            if (it.Cantidad <= 0) continue;
            // Descuento manual override. Si viene 0 desde el request, se calcula automaticamente
            // de la matriz por tipo cliente x marca del producto.
            var descPctManual = Math.Clamp(it.DescuentoPct, 0m, 100m);
            if (!FormatosValidos.Contains(it.Formato))
            {
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    it.ProductoId, "?", "?", it.Formato, it.Cantidad, 0m, 0m, 0m, 0m, 0m, 0,
                    false, "Formato inválido", NormMolienda(it.Molienda), it.EsDoyPack, descPctManual));
                todoOk = false;
                continue;
            }

            var prod = await _db.CafeProductos.FindAsync(it.ProductoId);
            if (prod is null)
            {
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    it.ProductoId, "?", "?", it.Formato, it.Cantidad, 0m, 0m, 0m, 0m, 0m, 0,
                    false, "Producto no encontrado", NormMolienda(it.Molienda), it.EsDoyPack, descPctManual));
                todoOk = false;
                continue;
            }

            // Validar combinación: formato unitario solo para OTROS, formatos kg solo para CAFE.
            var esCafe = prod.Categoria == "CAFE";
            var esFormatoCafe = it.Formato is "1KG" or "MEDIO" or "CUARTO";
            if (esCafe != esFormatoCafe)
            {
                cotizadoItems.Add(new CafeCotizadoItemDto(
                    prod.Id, prod.Nombre, prod.Categoria, it.Formato, it.Cantidad, 0m, 0m, 0m, 0m, prod.StockGramos, prod.StockUnidades,
                    false, esCafe ? "Para café usá 1 kg / 1/2 kg / 1/4 kg" : "Para otros productos usá 'unidad'",
                    NormMolienda(it.Molienda), it.EsDoyPack, descPctManual));
                todoOk = false;
                continue;
            }

            // Resolver margen de la marca (default 100%) para calcular PVP por % en OTROS.
            decimal? marcaMargen = null;
            if (prod.MarcaId.HasValue && marcas.TryGetValue(prod.MarcaId.Value, out var marcaProd))
                marcaMargen = marcaProd.MargenPctSobreCosto;

            // Descuento: el manual del request si lo hay; si no, el de la matriz.
            // Para CAFE, el descuento NO se aplica desde la matriz (sigue manual).
            var descPct = descPctManual;
            if (descPct == 0 && prod.Categoria != "CAFE")
                descPct = ResolverDescuentoMatriz(prod.MarcaId);

            var breakdown = CafePricingService.CalcularPrecioBreakdown(prod, it.Formato, tipo, settings, marcaMargen, descPct);
            var precioUnit = breakdown.PrecioLista;     // lista (sin descuento) — lo que se ve en P. Unitario
            var costoUnit = CafePricingService.CalcularCostoUnitario(prod, it.Formato);
            var subtotalLinea = Math.Round(breakdown.PrecioFinal * it.Cantidad, 2, MidpointRounding.AwayFromZero);
            var gramosNecesarios = esCafe ? CafePricingService.GramosPorUnidad(it.Formato) * it.Cantidad : 0m;
            var stockOk = esCafe ? gramosNecesarios <= prod.StockGramos + 0.001m : it.Cantidad <= prod.StockUnidades;
            string? aviso = null;
            if (!stockOk)
            {
                aviso = esCafe
                    ? $"Stock insuficiente. Disponible: {prod.StockGramos:0} g, necesitás {gramosNecesarios:0} g."
                    : $"Stock insuficiente. Disponible: {prod.StockUnidades} u, necesitás {it.Cantidad}.";
                todoOk = false;
            }

            cotizadoItems.Add(new CafeCotizadoItemDto(
                prod.Id, prod.Nombre, prod.Categoria, it.Formato, it.Cantidad,
                precioUnit, costoUnit, subtotalLinea,
                gramosNecesarios, prod.StockGramos, prod.StockUnidades,
                stockOk, aviso,
                NormMolienda(it.Molienda), it.EsDoyPack && esCafe,
                descPct));

            subtotal += subtotalLinea;
            costoTotal += costoUnit * it.Cantidad;
        }

        var desc = Math.Max(0m, descuento);
        var total = Math.Max(0m, subtotal - desc);
        var margen = total - costoTotal;
        return new CafeCotizadoDto(tipo, subtotal, desc, total, costoTotal, margen, todoOk, cotizadoItems);
    }

    private static readonly string[] TiposComprobanteValidos = { "X", "FA", "FB", "FC" };
    private static string NormTipoComprobante(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "X";
        var v = s.Trim().ToUpperInvariant();
        return TiposComprobanteValidos.Contains(v) ? v : "X";
    }

    private static readonly string[] CondicionesIvaValidas = { "CF", "RI", "MO", "EX" };
    private static string NormCondicionIva(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "CF";
        var v = s.Trim().ToUpperInvariant();
        return CondicionesIvaValidas.Contains(v) ? v : "CF";
    }

    private static readonly string[] CondicionesPagoValidas = { "EFECTIVO", "TRANSFERENCIA", "DEBITO", "CREDITO", "CTA_CORRIENTE", "CHEQUE", "V_PRIVADO" };
    private static string NormCondicionPago(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "EFECTIVO";
        var v = s.Trim().ToUpperInvariant();
        return CondicionesPagoValidas.Contains(v) ? v : "EFECTIVO";
    }

    private static readonly string[] MoliendasValidas = { "EN GRANOS", "MOLIDO FILTRO", "MOLIDO ESPRESS" };
    private static string? NormMolienda(string? m)
    {
        if (string.IsNullOrWhiteSpace(m)) return null;
        var v = m.Trim().ToUpperInvariant();
        return MoliendasValidas.Contains(v) ? v : null;
    }

    /// <summary>Normaliza CSV de dias: solo deja LUN/MAR/MIE/JUE/VIE/SAB/DOM en mayúscula y sin duplicados.</summary>
    private static readonly string[] DiasValidos = { "LUN", "MAR", "MIE", "JUE", "VIE", "SAB", "DOM" };
    private static string? NormWeekDays(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return null;
        var list = csv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim().ToUpperInvariant())
            .Where(s => DiasValidos.Contains(s))
            .Distinct()
            .OrderBy(s => Array.IndexOf(DiasValidos, s))
            .ToList();
        return list.Count == 0 ? null : string.Join(",", list);
    }

    private async Task<string> GenerarNumeroAsync()
    {
        var year = DateTime.UtcNow.Year;
        var prefix = $"CAFE-{year}-";
        var existing = await _db.CafeVentas
            .Where(v => v.Numero.StartsWith(prefix))
            .Select(v => v.Numero)
            .ToListAsync();
        int max = 0;
        foreach (var s in existing)
        {
            if (int.TryParse(s.Substring(prefix.Length), out var n) && n > max) max = n;
        }
        return $"{prefix}{(max + 1):D4}";
    }
}
