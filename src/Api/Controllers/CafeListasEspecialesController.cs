using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-10-26 — Pantalla Ventas → Precios especiales.
///
/// Pedido de Osmar: *"abajo de Productos un botón Precios especiales y poder ver el listado,
/// a quién le aplica cada uno, agregar clientes y productos fácilmente, y seleccionar
/// productos para descargar la propuesta y enviarla al cliente"*. También quiso dejarlo
/// listo para "crear categorías de precios": cada lista es una categoría.
///
/// Una LISTA tiene productos (con formato y precio SIN IVA) y clientes. La venta no lee
/// estas tablas: cada cambio se copia a Cafe_PreciosEspecialesCliente (una fila por
/// cliente, con ListaId), que es lo que ya usa el motor de precios. Así el cobro no cambia.
/// </summary>
[ApiController]
[Route("api/cafe/listas-especiales")]
[Authorize]
public class CafeListasEspecialesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CafeListaPreciosPdfService _pdfSvc;

    public CafeListasEspecialesController(AppDbContext db, CafeListaPreciosPdfService pdfSvc)
    {
        _db = db;
        _pdfSvc = pdfSvc;
    }

    // ─────────────────────────────────────────────────────────────────────
    //  DTOs
    // ─────────────────────────────────────────────────────────────────────

    public record ItemDto(
        int Id, int ProductoId, string? Sku, string ProductoNombre, string? Marca,
        string Formato, string FormatoLabel,
        decimal Precio, decimal PrecioLista, bool ListaMasBarata);

    public record ClienteDto(int Id, string? Codigo, string Nombre, string? Telefono, string Tipo);

    public record ListaDto(
        int Id, string Nombre, string? Notas, DateTime? Actualizada,
        List<ClienteDto> Clientes, List<ItemDto> Items, string TipoReferencia);

    /// <summary>Un precio pactado (de lista o suelto) — para las vistas Por cliente y Por producto.</summary>
    public record PactoDto(
        int Id, int ClienteId, string? ClienteCodigo, string ClienteNombre,
        int ProductoId, string? Sku, string ProductoNombre, string FormatoLabel,
        decimal Precio, int? ListaId, string? ListaNombre);

    public record ListaRequest(string Nombre, string? Notas);
    public record ClienteRequest(int ClienteId);
    public record ItemRequest(int ProductoId, string Formato, decimal Precio);
    public record PrecioRequest(decimal Precio);
    public record PdfRequest(List<int> ItemIds, int? ClienteId);

    // ─────────────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────────────

    private async Task<CafeSetting> SettingsAsync()
        => await _db.CafeSettings.AsNoTracking().FirstOrDefaultAsync() ?? new CafeSetting { Id = 1 };

    /// <summary>Tipo de cliente contra el que se compara "lista general": BAR (la más barata,
    /// la que avisa si lo pactado quedó caro) salvo que todos los clientes de la lista sean
    /// de otro tipo. Sin clientes, BAR.</summary>
    private static string TipoReferencia(IEnumerable<CafeCliente> clientes)
    {
        var tipos = clientes.Select(c => CafePricingService.ResolverTipo(c.Tipo)).Distinct().ToList();
        return tipos.Count == 1 ? tipos[0] : "BAR";
    }

    /// <summary>Copia un renglón de la lista al cliente. Si el cliente ya tenía ese producto y
    /// formato pactado (suelto o de otra lista), lo reemplaza. Devuelve true si reemplazó.</summary>
    private async Task<bool> AplicarAsync(CafeListaEspecial lista, int clienteId, CafeListaEspecialItem item)
    {
        var fila = await _db.CafePreciosEspecialesCliente
            .FirstOrDefaultAsync(x => x.ClienteId == clienteId && x.ProductoId == item.ProductoId
                                   && x.Formato == item.Formato && x.IsActive);
        if (fila is null)
        {
            _db.CafePreciosEspecialesCliente.Add(new CafePrecioEspecialCliente
            {
                ClienteId = clienteId,
                ProductoId = item.ProductoId,
                Formato = item.Formato,
                Precio = item.Precio,
                Notas = lista.Nombre,
                ListaId = lista.Id,
                CreatedBy = User?.Identity?.Name
            });
            return false;
        }
        var reemplaza = fila.ListaId != lista.Id && fila.Precio != item.Precio;
        fila.Precio = item.Precio;
        fila.Notas = lista.Nombre;
        fila.ListaId = lista.Id;
        fila.UpdatedAt = DateTime.UtcNow;
        return reemplaza;
    }

    private async Task<List<int>> ClientesDeAsync(int listaId)
        => await _db.CafeListasEspecialesClientes.Where(c => c.ListaId == listaId)
            .Select(c => c.ClienteId).ToListAsync();

    // ─────────────────────────────────────────────────────────────────────
    //  GET — todas las listas con sus clientes y productos
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> Listar()
    {
        var listas = await _db.CafeListasEspeciales.AsNoTracking()
            .Where(l => l.IsActive).OrderBy(l => l.Nombre).ToListAsync();
        if (listas.Count == 0) return Ok(new List<ListaDto>());

        var ids = listas.Select(l => l.Id).ToList();
        var items = await _db.CafeListasEspecialesItems.AsNoTracking()
            .Where(i => ids.Contains(i.ListaId)).ToListAsync();
        var clis = await _db.CafeListasEspecialesClientes.AsNoTracking()
            .Include(c => c.ClienteNav)
            .Where(c => ids.Contains(c.ListaId)).ToListAsync();

        var prodIds = items.Select(i => i.ProductoId).Distinct().ToList();
        var productos = await _db.CafeProductos.AsNoTracking()
            .Include(p => p.OemNav).Include(p => p.Packs)
            .Where(p => prodIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);
        var settings = await SettingsAsync();

        var res = new List<ListaDto>();
        foreach (var l in listas)
        {
            var clientes = clis.Where(c => c.ListaId == l.Id && c.ClienteNav is not null)
                .Select(c => c.ClienteNav!).ToList();
            var tipo = TipoReferencia(clientes);

            var its = new List<ItemDto>();
            foreach (var i in items.Where(x => x.ListaId == l.Id))
            {
                if (!productos.TryGetValue(i.ProductoId, out var p)) continue;
                var lista = CafePricingService.CalcularPrecioBreakdown(p, i.Formato, tipo, settings).PrecioLista;
                its.Add(new ItemDto(i.Id, p.Id, p.Sku, p.Nombre, p.Marca, i.Formato,
                    CafePreciosEspecialesController.LabelFormato(p, i.Formato),
                    i.Precio, lista, lista > 0m && lista < i.Precio));
            }

            var actualizada = items.Where(x => x.ListaId == l.Id)
                .Select(x => (DateTime?)(x.UpdatedAt ?? x.CreatedAt))
                .Append(l.UpdatedAt ?? l.CreatedAt).Max();

            res.Add(new ListaDto(l.Id, l.Nombre, l.Notas, actualizada,
                clientes.OrderBy(c => c.Nombre)
                    .Select(c => new ClienteDto(c.Id, c.Codigo, c.Nombre, c.Telefono, CafePricingService.ResolverTipo(c.Tipo)))
                    .ToList(),
                its.OrderBy(x => x.Sku).ThenBy(x => x.ProductoNombre).ToList(),
                tipo));
        }
        return Ok(res);
    }

    // ─────────────────────────────────────────────────────────────────────
    //  GET — todos los precios pactados (de lista y sueltos), para Por cliente / Por producto
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet("pactos")]
    public async Task<IActionResult> Pactos()
    {
        var filas = await _db.CafePreciosEspecialesCliente.AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => new
            {
                x.Id, x.ClienteId, CliCodigo = x.ClienteNav!.Codigo, CliNombre = x.ClienteNav!.Nombre,
                x.ProductoId, x.ProductoNav!.Sku, ProdNombre = x.ProductoNav!.Nombre,
                x.Formato, x.Precio, x.ListaId
            })
            .ToListAsync();

        var prodIds = filas.Select(f => f.ProductoId).Distinct().ToList();
        var productos = await _db.CafeProductos.AsNoTracking()
            .Where(p => prodIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id);
        var listas = await _db.CafeListasEspeciales.AsNoTracking()
            .ToDictionaryAsync(l => l.Id, l => l.Nombre);

        return Ok(filas.Select(f => new PactoDto(
                f.Id, f.ClienteId, f.CliCodigo, f.CliNombre ?? "",
                f.ProductoId, f.Sku, f.ProdNombre ?? "",
                productos.TryGetValue(f.ProductoId, out var p)
                    ? CafePreciosEspecialesController.LabelFormato(p, f.Formato)
                    : CafePricingService.FormatoLabel(f.Formato),
                f.Precio, f.ListaId,
                f.ListaId is int lid && listas.TryGetValue(lid, out var ln) ? ln : null))
            .OrderBy(x => x.ClienteNombre).ThenBy(x => x.Sku)
            .ToList());
    }

    // ─────────────────────────────────────────────────────────────────────
    //  GET — formatos de un producto para agregarlo a la lista
    // ─────────────────────────────────────────────────────────────────────

    [HttpGet("{id:int}/opciones")]
    public async Task<IActionResult> Opciones(int id, [FromQuery] int productoId)
    {
        var p = await _db.CafeProductos.AsNoTracking()
            .Include(x => x.OemNav).Include(x => x.Packs)
            .FirstOrDefaultAsync(x => x.Id == productoId);
        if (p is null) return NotFound(new { error = "Producto no encontrado" });

        var clientes = await _db.CafeListasEspecialesClientes.AsNoTracking()
            .Where(c => c.ListaId == id).Select(c => c.ClienteNav!).ToListAsync();
        var formatos = CafePreciosEspecialesController.FormatosDe(p, TipoReferencia(clientes), await SettingsAsync());
        return Ok(new CafePreciosEspecialesController.OpcionesProductoDto(
            p.Id, p.Sku, p.Nombre, p.Marca, p.Categoria, formatos));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Listas: crear, renombrar, borrar
    // ─────────────────────────────────────────────────────────────────────

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] ListaRequest req)
    {
        var nombre = (req.Nombre ?? "").Trim();
        if (nombre.Length == 0) return BadRequest(new { error = "Ponele un nombre a la lista" });
        if (await _db.CafeListasEspeciales.AnyAsync(l => l.IsActive && l.Nombre == nombre))
            return BadRequest(new { error = "Ya hay una lista con ese nombre" });

        var l = new CafeListaEspecial
        {
            Nombre = nombre,
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            CreatedBy = User?.Identity?.Name
        };
        _db.CafeListasEspeciales.Add(l);
        await _db.SaveChangesAsync();
        return Ok(new { id = l.Id });
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Renombrar(int id, [FromBody] ListaRequest req)
    {
        var l = await _db.CafeListasEspeciales.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (l is null) return NotFound(new { error = "Lista no encontrada" });
        var nombre = (req.Nombre ?? "").Trim();
        if (nombre.Length == 0) return BadRequest(new { error = "Ponele un nombre a la lista" });

        l.Nombre = nombre;
        l.Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim();
        l.UpdatedAt = DateTime.UtcNow;
        // Las notas de los pactos copiados dicen de qué lista vienen.
        await _db.CafePreciosEspecialesCliente
            .Where(x => x.ListaId == id && x.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Notas, nombre));
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Borra la lista: sus clientes dejan de tener esos precios (vuelven a la lista
    /// general). Los pactos sueltos de cada cliente no se tocan.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var l = await _db.CafeListasEspeciales.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (l is null) return NotFound(new { error = "Lista no encontrada" });

        l.IsActive = false;
        l.UpdatedAt = DateTime.UtcNow;
        await _db.CafePreciosEspecialesCliente
            .Where(x => x.ListaId == id && x.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false).SetProperty(x => x.UpdatedAt, DateTime.UtcNow));
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Clientes de la lista
    // ─────────────────────────────────────────────────────────────────────

    [HttpPost("{id:int}/clientes")]
    public async Task<IActionResult> AgregarCliente(int id, [FromBody] ClienteRequest req)
    {
        var l = await _db.CafeListasEspeciales.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (l is null) return NotFound(new { error = "Lista no encontrada" });
        if (!await _db.CafeClientes.AnyAsync(c => c.Id == req.ClienteId))
            return NotFound(new { error = "Cliente no encontrado" });
        if (await _db.CafeListasEspecialesClientes.AnyAsync(c => c.ListaId == id && c.ClienteId == req.ClienteId))
            return BadRequest(new { error = "Ese cliente ya está en la lista" });

        _db.CafeListasEspecialesClientes.Add(new CafeListaEspecialCliente { ListaId = id, ClienteId = req.ClienteId });

        var reemplazados = 0;
        foreach (var it in await _db.CafeListasEspecialesItems.Where(i => i.ListaId == id).ToListAsync())
            if (await AplicarAsync(l, req.ClienteId, it)) reemplazados++;

        l.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { reemplazados });
    }

    [HttpDelete("{id:int}/clientes/{clienteId:int}")]
    public async Task<IActionResult> QuitarCliente(int id, int clienteId)
    {
        var rel = await _db.CafeListasEspecialesClientes.FirstOrDefaultAsync(c => c.ListaId == id && c.ClienteId == clienteId);
        if (rel is null) return NotFound(new { error = "Ese cliente no está en la lista" });

        _db.CafeListasEspecialesClientes.Remove(rel);
        await _db.CafePreciosEspecialesCliente
            .Where(x => x.ListaId == id && x.ClienteId == clienteId && x.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false).SetProperty(x => x.UpdatedAt, DateTime.UtcNow));
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ─────────────────────────────────────────────────────────────────────
    //  Productos de la lista
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Agrega un producto (o le cambia el precio si ya estaba) y lo copia a todos los
    /// clientes de la lista.</summary>
    [HttpPost("{id:int}/items")]
    public async Task<IActionResult> GuardarItem(int id, [FromBody] ItemRequest req)
    {
        if (req.Precio <= 0m) return BadRequest(new { error = "Poné un precio mayor a cero" });
        var l = await _db.CafeListasEspeciales.FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (l is null) return NotFound(new { error = "Lista no encontrada" });

        var prod = await _db.CafeProductos.Include(p => p.Packs).FirstOrDefaultAsync(p => p.Id == req.ProductoId);
        if (prod is null) return NotFound(new { error = "Producto no encontrado" });

        var formato = CafePreciosEspecialesController.NormFormato(req.Formato);
        var formatosOk = CafePreciosEspecialesController.FormatosDe(prod, "BAR", await SettingsAsync())
            .Select(f => f.Formato).ToHashSet();
        if (!formatosOk.Contains(formato))
            return BadRequest(new { error = $"Este producto no se puede vender como '{CafePreciosEspecialesController.LabelFormato(prod, formato)}'" });

        var precio = Math.Round(req.Precio, 2, MidpointRounding.AwayFromZero);
        var item = await _db.CafeListasEspecialesItems
            .FirstOrDefaultAsync(i => i.ListaId == id && i.ProductoId == req.ProductoId && i.Formato == formato);
        if (item is null)
        {
            item = new CafeListaEspecialItem { ListaId = id, ProductoId = req.ProductoId, Formato = formato, Precio = precio };
            _db.CafeListasEspecialesItems.Add(item);
        }
        else
        {
            item.Precio = precio;
            item.UpdatedAt = DateTime.UtcNow;
        }

        var reemplazados = 0;
        foreach (var cid in await ClientesDeAsync(id))
            if (await AplicarAsync(l, cid, item)) reemplazados++;

        await _db.SaveChangesAsync();
        return Ok(new { id = item.Id, reemplazados });
    }

    [HttpPut("items/{itemId:int}")]
    public async Task<IActionResult> CambiarPrecio(int itemId, [FromBody] PrecioRequest req)
    {
        if (req.Precio <= 0m) return BadRequest(new { error = "Poné un precio mayor a cero" });
        var item = await _db.CafeListasEspecialesItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return NotFound(new { error = "Producto no encontrado en la lista" });
        var l = await _db.CafeListasEspeciales.FirstAsync(x => x.Id == item.ListaId);

        item.Precio = Math.Round(req.Precio, 2, MidpointRounding.AwayFromZero);
        item.UpdatedAt = DateTime.UtcNow;
        foreach (var cid in await ClientesDeAsync(item.ListaId))
            await AplicarAsync(l, cid, item);

        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    [HttpDelete("items/{itemId:int}")]
    public async Task<IActionResult> QuitarItem(int itemId)
    {
        var item = await _db.CafeListasEspecialesItems.FirstOrDefaultAsync(i => i.Id == itemId);
        if (item is null) return NotFound(new { error = "Producto no encontrado en la lista" });

        _db.CafeListasEspecialesItems.Remove(item);
        await _db.CafePreciosEspecialesCliente
            .Where(x => x.ListaId == item.ListaId && x.ProductoId == item.ProductoId
                     && x.Formato == item.Formato && x.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false).SetProperty(x => x.UpdatedAt, DateTime.UtcNow));
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ─────────────────────────────────────────────────────────────────────
    //  PDF de la propuesta: los productos tildados, con el mismo formato que las listas
    //  de precios (logo, encabezado y "LOS PRECIOS NO INCLUYEN IVA"). Precios SIN IVA.
    // ─────────────────────────────────────────────────────────────────────

    [HttpPost("{id:int}/pdf")]
    public async Task<IActionResult> Pdf(int id, [FromBody] PdfRequest req)
    {
        var l = await _db.CafeListasEspeciales.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.IsActive);
        if (l is null) return NotFound(new { error = "Lista no encontrada" });

        var ids = req.ItemIds ?? new();
        var items = await _db.CafeListasEspecialesItems.AsNoTracking()
            .Include(i => i.ProductoNav)
            .Where(i => i.ListaId == id && ids.Contains(i.Id))
            .ToListAsync();
        if (items.Count == 0) return BadRequest(new { error = "Tildá al menos un producto" });

        var settings = await SettingsAsync();
        var negocio = new CafeListaPreciosNegocioDto(
            settings.NegocioNombre, settings.NegocioTelefono,
            settings.NegocioWhatsappNumero, settings.NegocioDireccion, settings.NegocioCuit,
            settings.NegocioEmail, settings.NegocioWeb, settings.NegocioLogoUrl,
            settings.ListaPreciosHeaderImageUrl);

        CafeListaPreciosClienteDto? clienteDto = null;
        var tipo = "BAR";
        if (req.ClienteId is int cid && cid > 0)
        {
            var c = await _db.CafeClientes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == cid);
            if (c is not null)
            {
                tipo = CafePricingService.ResolverTipo(c.Tipo);
                clienteDto = new CafeListaPreciosClienteDto(c.Id, c.Codigo, c.Nombre, tipo, c.Telefono, c.Email);
            }
        }

        var otros = items
            .Where(i => i.ProductoNav is not null)
            .OrderBy(i => i.ProductoNav!.Sku).ThenBy(i => i.ProductoNav!.Nombre)
            .Select(i => new CafeListaPreciosItemOtroDto(
                i.ProductoId, i.ProductoNav!.Sku,
                $"{i.ProductoNav!.Nombre} · {CafePreciosEspecialesController.LabelFormato(i.ProductoNav!, i.Formato)}",
                i.Precio, i.Precio, 0m))
            .ToList();

        var hoy = PanoramaService.AhoraAr().Date;
        var preview = new CafeListaPreciosPreviewDto(
            hoy, hoy.AddDays(7), tipo, negocio, clienteDto,
            new List<CafeListaPreciosMarcaGroupDto> { new(null, l.Nombre, null, new(), otros) },
            null);

        var bytes = _pdfSvc.GenerarPdf(preview,
            clienteDto is null ? "Propuesta de precios especiales"
                               : $"Propuesta de precios especiales para {clienteDto.Nombre} · {hoy:dd/MM/yyyy}");
        var quien = clienteDto?.Nombre ?? l.Nombre;
        var slug = new string(quien.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        return File(bytes, "application/pdf", $"propuesta-{slug}-{hoy:yyyyMMdd}.pdf");
    }
}
