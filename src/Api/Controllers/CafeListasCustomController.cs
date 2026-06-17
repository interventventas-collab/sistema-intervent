using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// CRUD de listas de precios personalizadas (Fase 1).
/// Fase 1 cubre: listar, crear, renombrar, borrar. Fase 2 sumara items + secciones.
/// </summary>
[ApiController]
[Route("api/cafe/listas-custom")]
[Authorize]
public class CafeListasCustomController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CafeListaCustomPdfService _pdfService;

    public CafeListasCustomController(AppDbContext db, CafeListaCustomPdfService pdfService)
    {
        _db = db;
        _pdfService = pdfService;
    }

    // ─────────────────────────────────────────────────────────────────────
    // DTOs
    // ─────────────────────────────────────────────────────────────────────
    public class ListaCustomDto
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public int? ClienteId { get; set; }
        public string? ClienteNombre { get; set; }
        public string? ClienteCodigo { get; set; }
        public string? TipoCliente { get; set; }
        public string? Observaciones { get; set; }
        public string? NumeroLista { get; set; }
        public int CantidadSecciones { get; set; }
        public int CantidadItems { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }
    }

    public class CrearListaRequest
    {
        public string Nombre { get; set; } = "";
        public int? ClienteId { get; set; }
        public string? TipoCliente { get; set; }
        public string? Observaciones { get; set; }
        public string? NumeroLista { get; set; }
    }

    public class ActualizarListaRequest
    {
        public string Nombre { get; set; } = "";
        public int? ClienteId { get; set; }
        public string? TipoCliente { get; set; }
        public string? Observaciones { get; set; }
        public string? NumeroLista { get; set; }
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET listar todas las listas activas
    // ─────────────────────────────────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> ListAll()
    {
        var listas = await _db.CafeListasPreciosCustom
            .Where(l => l.IsActive)
            .Include(l => l.ClienteNav)
            .OrderByDescending(l => l.UpdatedAt)
            .Select(l => new ListaCustomDto
            {
                Id = l.Id,
                Nombre = l.Nombre,
                ClienteId = l.ClienteId,
                ClienteNombre = l.ClienteNav != null ? l.ClienteNav.Nombre : null,
                ClienteCodigo = l.ClienteNav != null ? l.ClienteNav.Codigo : null,
                TipoCliente = l.TipoCliente,
                Observaciones = l.Observaciones,
                NumeroLista = l.NumeroLista,
                CantidadSecciones = l.Secciones.Count,
                CantidadItems = l.Secciones.SelectMany(s => s.Items).Count(),
                CreatedAt = l.CreatedAt,
                UpdatedAt = l.UpdatedAt
            })
            .ToListAsync();

        return Ok(listas);
    }

    // ─────────────────────────────────────────────────────────────────────
    // GET una lista puntual
    // ─────────────────────────────────────────────────────────────────────
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetOne(int id)
    {
        var lista = await _db.CafeListasPreciosCustom
            .Include(l => l.ClienteNav)
            .FirstOrDefaultAsync(l => l.Id == id && l.IsActive);
        if (lista is null) return NotFound(new { error = "Lista no encontrada" });

        var cantSec = await _db.CafeListasPreciosCustomSecciones.CountAsync(s => s.ListaId == id);
        var cantItems = await _db.CafeListasPreciosCustomItems
            .Where(i => _db.CafeListasPreciosCustomSecciones
                .Where(s => s.ListaId == id).Select(s => s.Id).Contains(i.SeccionId))
            .CountAsync();

        return Ok(new ListaCustomDto
        {
            Id = lista.Id,
            Nombre = lista.Nombre,
            ClienteId = lista.ClienteId,
            ClienteNombre = lista.ClienteNav?.Nombre,
            ClienteCodigo = lista.ClienteNav?.Codigo,
            TipoCliente = lista.TipoCliente,
            Observaciones = lista.Observaciones,
            NumeroLista = lista.NumeroLista,
            CantidadSecciones = cantSec,
            CantidadItems = cantItems,
            CreatedAt = lista.CreatedAt,
            UpdatedAt = lista.UpdatedAt
        });
    }

    // ─────────────────────────────────────────────────────────────────────
    // POST crear lista vacia
    // ─────────────────────────────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearListaRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "Falta el nombre de la lista" });

        // Validar cliente si vino
        if (req.ClienteId.HasValue)
        {
            var existeCliente = await _db.CafeClientes.AnyAsync(c => c.Id == req.ClienteId.Value);
            if (!existeCliente) return BadRequest(new { error = "Cliente no encontrado" });
        }

        // Normalizar tipo
        var tipo = req.TipoCliente?.Trim().ToUpperInvariant();
        if (tipo != null && tipo != "BAR" && tipo != "OTRO") tipo = null;

        var lista = new CafeListaPreciosCustom
        {
            Nombre = req.Nombre.Trim(),
            ClienteId = req.ClienteId,
            TipoCliente = tipo,
            Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim(),
            NumeroLista = string.IsNullOrWhiteSpace(req.NumeroLista) ? null : req.NumeroLista.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _db.CafeListasPreciosCustom.Add(lista);
        await _db.SaveChangesAsync();

        return Ok(new { id = lista.Id });
    }

    // ─────────────────────────────────────────────────────────────────────
    // PUT actualizar metadata de lista (nombre, cliente, tipo, etc)
    // ─────────────────────────────────────────────────────────────────────
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Actualizar(int id, [FromBody] ActualizarListaRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Nombre))
            return BadRequest(new { error = "Falta el nombre" });

        var lista = await _db.CafeListasPreciosCustom.FirstOrDefaultAsync(l => l.Id == id && l.IsActive);
        if (lista is null) return NotFound(new { error = "Lista no encontrada" });

        if (req.ClienteId.HasValue)
        {
            var existeCliente = await _db.CafeClientes.AnyAsync(c => c.Id == req.ClienteId.Value);
            if (!existeCliente) return BadRequest(new { error = "Cliente no encontrado" });
        }

        var tipo = req.TipoCliente?.Trim().ToUpperInvariant();
        if (tipo != null && tipo != "BAR" && tipo != "OTRO") tipo = null;

        lista.Nombre = req.Nombre.Trim();
        lista.ClienteId = req.ClienteId;
        lista.TipoCliente = tipo;
        lista.Observaciones = string.IsNullOrWhiteSpace(req.Observaciones) ? null : req.Observaciones.Trim();
        lista.NumeroLista = string.IsNullOrWhiteSpace(req.NumeroLista) ? null : req.NumeroLista.Trim();
        lista.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ─────────────────────────────────────────────────────────────────────
    // DELETE soft-delete (IsActive=false)
    // ─────────────────────────────────────────────────────────────────────
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var lista = await _db.CafeListasPreciosCustom.FirstOrDefaultAsync(l => l.Id == id && l.IsActive);
        if (lista is null) return NotFound(new { error = "Lista no encontrada" });

        lista.IsActive = false;
        lista.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ─────────────────────────────────────────────────────────────────────
    // POST duplicar lista (clona nombre + items)
    // ─────────────────────────────────────────────────────────────────────
    [HttpPost("{id:int}/duplicar")]
    public async Task<IActionResult> Duplicar(int id)
    {
        var orig = await _db.CafeListasPreciosCustom
            .Include(l => l.Secciones).ThenInclude(s => s.Items)
            .FirstOrDefaultAsync(l => l.Id == id && l.IsActive);
        if (orig is null) return NotFound(new { error = "Lista no encontrada" });

        var nueva = new CafeListaPreciosCustom
        {
            Nombre = $"{orig.Nombre} (copia)",
            ClienteId = orig.ClienteId,
            TipoCliente = orig.TipoCliente,
            Observaciones = orig.Observaciones,
            NumeroLista = null,
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.CafeListasPreciosCustom.Add(nueva);
        await _db.SaveChangesAsync();

        foreach (var sec in orig.Secciones.OrderBy(s => s.Orden))
        {
            var ns = new CafeListaPreciosCustomSeccion
            {
                ListaId = nueva.Id,
                Titulo = sec.Titulo,
                Orden = sec.Orden,
                CreatedAt = DateTime.UtcNow
            };
            _db.CafeListasPreciosCustomSecciones.Add(ns);
            await _db.SaveChangesAsync();

            foreach (var it in sec.Items.OrderBy(i => i.Orden))
            {
                _db.CafeListasPreciosCustomItems.Add(new CafeListaPreciosCustomItem
                {
                    SeccionId = ns.Id,
                    TipoItem = it.TipoItem,
                    RefId = it.RefId,
                    Orden = it.Orden,
                    Notas = it.Notas,
                    EsNovedad = it.EsNovedad,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }
        await _db.SaveChangesAsync();
        return Ok(new { id = nueva.Id });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // FASE 2 — Secciones + Items
    // ═══════════════════════════════════════════════════════════════════════

    public class SeccionDto
    {
        public int Id { get; set; }
        public string Titulo { get; set; } = "";
        public int Orden { get; set; }
        public List<ItemDto> Items { get; set; } = new();
    }

    public class ItemDto
    {
        public int Id { get; set; }
        public string TipoItem { get; set; } = ""; // PRODUCTO | COMBO | PACK
        public int RefId { get; set; }
        public int Orden { get; set; }
        public string? Notas { get; set; }
        public bool EsNovedad { get; set; }
        // Datos del item resuelto (no se almacenan, se calculan en el GET)
        public string? Nombre { get; set; }
        public string? Sku { get; set; }
        public string? Marca { get; set; }    // 2026-06-16: marca para mostrar
        public decimal? Precio { get; set; }
        public string? Detalle { get; set; }  // ej "Pack x 100" o "Combo 3 items"
        // 2026-06-17: para CAFE — 3 precios (1 kg / ½ kg / ¼ kg) ya calculados.
        public decimal? Precio1Kg { get; set; }
        public decimal? PrecioMedio { get; set; }
        public decimal? PrecioCuarto { get; set; }
    }

    public class ContenidoListaDto
    {
        public ListaCustomDto Lista { get; set; } = new();
        public List<SeccionDto> Secciones { get; set; } = new();
    }

    public class ItemDisponibleDto
    {
        public string Tipo { get; set; } = ""; // PRODUCTO | COMBO | PACK
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
        public string? Sku { get; set; }
        public string? Marca { get; set; }   // 2026-06-16
        public decimal? PrecioBar { get; set; }
        public decimal? PrecioOtro { get; set; }
        public string? Detalle { get; set; }
    }

    // ─── GET contenido (lista + secciones + items con datos resueltos) ───
    [HttpGet("{listaId:int}/contenido")]
    public async Task<IActionResult> GetContenido(int listaId)
    {
        var lista = await _db.CafeListasPreciosCustom
            .Include(l => l.ClienteNav)
            .FirstOrDefaultAsync(l => l.Id == listaId && l.IsActive);
        if (lista is null) return NotFound(new { error = "Lista no encontrada" });

        var secciones = await _db.CafeListasPreciosCustomSecciones
            .Where(s => s.ListaId == listaId)
            .OrderBy(s => s.Orden).ThenBy(s => s.Id)
            .Include(s => s.Items)
            .ToListAsync();

        // Cargar info de cada item resuelta (producto / combo / pack)
        var productoIds = secciones.SelectMany(s => s.Items).Where(i => i.TipoItem == "PRODUCTO").Select(i => i.RefId).Distinct().ToList();
        var comboIds = secciones.SelectMany(s => s.Items).Where(i => i.TipoItem == "COMBO").Select(i => i.RefId).Distinct().ToList();
        var packIds = secciones.SelectMany(s => s.Items).Where(i => i.TipoItem == "PACK").Select(i => i.RefId).Distinct().ToList();

        // 2026-06-17: traemos productos enteros (Costo, OEM, Pvp1, packs) para poder calcular
        // los 3 precios (1 kg / ½ / ¼) usando CafePricingService cuando es CAFE.
        var productosEntidad = await _db.CafeProductos.Where(p => productoIds.Contains(p.Id))
            .Include(p => p.MarcaNav)
            .Include(p => p.OemNav)
            .Include(p => p.Packs)
            .ToListAsync();
        var combos = await _db.CafeCombos.Where(c => comboIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre, c.Sku, c.Marca, Precio = (decimal?)c.PrecioReferencia }).ToListAsync();
        var packs = await _db.CafeProductoPacks.Where(p => packIds.Contains(p.Id))
            .Include(p => p.Producto).ThenInclude(p => p!.MarcaNav)
            .Select(p => new { p.Id, p.Nombre, p.Cantidad, p.PrecioOverride, ProdNombre = p.Producto!.Nombre, p.Producto.PrecioBar, p.Producto.PrecioOtro, p.Producto.Sku, Marca = p.Producto.Marca ?? (p.Producto.MarcaNav != null ? p.Producto.MarcaNav.Nombre : null) }).ToListAsync();

        var negocio = await _db.CafeSettings.FirstOrDefaultAsync() ?? new CafeSetting();
        var esBar = string.Equals(lista.TipoCliente, "BAR", StringComparison.OrdinalIgnoreCase);
        var tipoCli = esBar ? Services.CafePricingService.TIPO_BAR : Services.CafePricingService.TIPO_OTRO;

        var seccionesDto = secciones.Select(s => new SeccionDto
        {
            Id = s.Id,
            Titulo = s.Titulo,
            Orden = s.Orden,
            Items = s.Items.OrderBy(i => i.Orden).ThenBy(i => i.Id).Select(i =>
            {
                string? nombre = null;
                string? sku = null;
                string? marca = null;
                decimal? precio = null;
                string? detalle = null;
                decimal? p1Kg = null, pMedio = null, pCuarto = null;

                if (i.TipoItem == "PRODUCTO")
                {
                    var p = productosEntidad.FirstOrDefault(x => x.Id == i.RefId);
                    if (p != null)
                    {
                        nombre = p.Nombre;
                        sku = p.Sku;
                        marca = p.Marca ?? p.MarcaNav?.Nombre;
                        if (string.Equals(p.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase))
                        {
                            p1Kg = Services.CafePricingService.CalcularPrecioUnitario(p, Services.CafePricingService.FORMATO_1KG, tipoCli, negocio);
                            pMedio = Services.CafePricingService.CalcularPrecioUnitario(p, Services.CafePricingService.FORMATO_MEDIO, tipoCli, negocio);
                            pCuarto = Services.CafePricingService.CalcularPrecioUnitario(p, Services.CafePricingService.FORMATO_CUARTO, tipoCli, negocio);
                            precio = p1Kg; // fallback para UI que aún no muestre las 3 columnas
                        }
                        else
                        {
                            precio = esBar ? (p.PrecioBar ?? p.PrecioOtro) : (p.PrecioOtro ?? p.PrecioBar);
                        }
                    }
                }
                else if (i.TipoItem == "COMBO")
                {
                    var c = combos.FirstOrDefault(x => x.Id == i.RefId);
                    if (c != null) { nombre = c.Nombre; sku = c.Sku; marca = c.Marca; precio = c.Precio; detalle = "Combo"; }
                }
                else if (i.TipoItem == "PACK")
                {
                    var p = packs.FirstOrDefault(x => x.Id == i.RefId);
                    if (p != null)
                    {
                        nombre = p.ProdNombre;
                        sku = p.Sku;
                        marca = p.Marca;
                        var precioBase = esBar ? (p.PrecioBar ?? p.PrecioOtro) : (p.PrecioOtro ?? p.PrecioBar);
                        precio = p.PrecioOverride ?? (precioBase * p.Cantidad);
                        detalle = $"Pack x {p.Cantidad}";
                    }
                }
                return new ItemDto
                {
                    Id = i.Id, TipoItem = i.TipoItem, RefId = i.RefId, Orden = i.Orden,
                    Notas = i.Notas, EsNovedad = i.EsNovedad,
                    Nombre = nombre, Sku = sku, Marca = marca, Precio = precio, Detalle = detalle,
                    Precio1Kg = p1Kg, PrecioMedio = pMedio, PrecioCuarto = pCuarto
                };
            }).ToList()
        }).ToList();

        var dto = new ContenidoListaDto
        {
            Lista = new ListaCustomDto
            {
                Id = lista.Id, Nombre = lista.Nombre, ClienteId = lista.ClienteId,
                ClienteNombre = lista.ClienteNav?.Nombre, ClienteCodigo = lista.ClienteNav?.Codigo,
                TipoCliente = lista.TipoCliente, Observaciones = lista.Observaciones,
                NumeroLista = lista.NumeroLista, CantidadSecciones = secciones.Count,
                CantidadItems = secciones.Sum(s => s.Items.Count),
                CreatedAt = lista.CreatedAt, UpdatedAt = lista.UpdatedAt
            },
            Secciones = seccionesDto
        };
        return Ok(dto);
    }

    // ─── GET items disponibles para agregar (con buscador + filtros marca/categoria) ───
    [HttpGet("items-disponibles")]
    public async Task<IActionResult> GetItemsDisponibles([FromQuery] string tipo = "PRODUCTO", [FromQuery] string? q = null, [FromQuery] string? marca = null, [FromQuery] string? categoria = null)
    {
        tipo = (tipo ?? "PRODUCTO").Trim().ToUpperInvariant();
        var search = (q ?? "").Trim().ToLowerInvariant();
        var marcaFiltro = (marca ?? "").Trim();
        var categoriaFiltro = (categoria ?? "").Trim().ToUpperInvariant();
        // 2026-06-16 v2: cuando hay filtro de marca o categoria, sacamos el limite Take(100) para "Agregar todos" traiga todo.
        int limite = (string.IsNullOrEmpty(marcaFiltro) && string.IsNullOrEmpty(categoriaFiltro)) ? 100 : 5000;
        var result = new List<ItemDisponibleDto>();

        if (tipo == "PRODUCTO")
        {
            var qry = _db.CafeProductos.Include(p => p.MarcaNav).Where(p => p.IsActive);
            if (!string.IsNullOrEmpty(search))
                qry = qry.Where(p => p.Nombre.ToLower().Contains(search) || (p.Sku != null && p.Sku.ToLower().Contains(search)) || (p.Marca != null && p.Marca.ToLower().Contains(search)));
            if (!string.IsNullOrEmpty(marcaFiltro))
                qry = qry.Where(p => (p.Marca != null && p.Marca == marcaFiltro) || (p.MarcaNav != null && p.MarcaNav.Nombre == marcaFiltro));
            if (!string.IsNullOrEmpty(categoriaFiltro))
                qry = qry.Where(p => p.Categoria == categoriaFiltro);
            // 2026-06-16 v3: orden por SKU con length-first para que F1, F2 ... F10, F11 salga natural.
            result = await qry.OrderBy(p => p.Sku!.Length).ThenBy(p => p.Sku).Take(limite)
                .Select(p => new ItemDisponibleDto
                {
                    Tipo = "PRODUCTO", Id = p.Id, Nombre = p.Nombre, Sku = p.Sku,
                    Marca = p.Marca ?? (p.MarcaNav != null ? p.MarcaNav.Nombre : null),
                    PrecioBar = p.PrecioBar, PrecioOtro = p.PrecioOtro
                }).ToListAsync();
        }
        else if (tipo == "COMBO")
        {
            var qry = _db.CafeCombos.Where(c => c.IsActive);
            if (!string.IsNullOrEmpty(search))
                qry = qry.Where(c => c.Nombre.ToLower().Contains(search) || (c.Sku != null && c.Sku.ToLower().Contains(search)) || (c.Marca != null && c.Marca.ToLower().Contains(search)));
            if (!string.IsNullOrEmpty(marcaFiltro))
                qry = qry.Where(c => c.Marca == marcaFiltro);
            if (!string.IsNullOrEmpty(categoriaFiltro))
                qry = qry.Where(c => c.Categoria == categoriaFiltro);
            result = await qry.OrderBy(c => c.Sku!.Length).ThenBy(c => c.Sku).Take(limite)
                .Select(c => new ItemDisponibleDto
                {
                    Tipo = "COMBO", Id = c.Id, Nombre = c.Nombre, Sku = c.Sku,
                    Marca = c.Marca,
                    PrecioBar = c.PrecioReferencia, PrecioOtro = c.PrecioReferencia,
                    Detalle = "Combo"
                }).ToListAsync();
        }
        else if (tipo == "PACK")
        {
            var qry = _db.CafeProductoPacks.Include(p => p.Producto).ThenInclude(p => p!.MarcaNav).Where(p => p.IsActive);
            if (!string.IsNullOrEmpty(search))
                qry = qry.Where(p =>
                    (p.Nombre.ToLower().Contains(search))
                    || (p.Producto != null && p.Producto.Nombre.ToLower().Contains(search))
                    || (p.Producto != null && p.Producto.Sku != null && p.Producto.Sku.ToLower().Contains(search))
                    || (p.Producto != null && p.Producto.Marca != null && p.Producto.Marca.ToLower().Contains(search))
                );
            if (!string.IsNullOrEmpty(marcaFiltro))
                qry = qry.Where(p => p.Producto != null && ((p.Producto.Marca == marcaFiltro) || (p.Producto.MarcaNav != null && p.Producto.MarcaNav.Nombre == marcaFiltro)));
            if (!string.IsNullOrEmpty(categoriaFiltro))
                qry = qry.Where(p => p.Producto != null && p.Producto.Categoria == categoriaFiltro);
            result = await qry.OrderBy(p => p.Producto!.Sku!.Length).ThenBy(p => p.Producto.Sku).ThenBy(p => p.Cantidad).Take(limite)
                .Select(p => new ItemDisponibleDto
                {
                    Tipo = "PACK", Id = p.Id,
                    Nombre = p.Producto!.Nombre,
                    Sku = p.Producto.Sku,
                    Marca = p.Producto.Marca ?? (p.Producto.MarcaNav != null ? p.Producto.MarcaNav.Nombre : null),
                    PrecioBar = p.PrecioOverride ?? (p.Producto.PrecioBar.HasValue ? p.Producto.PrecioBar.Value * p.Cantidad : (decimal?)null),
                    PrecioOtro = p.PrecioOverride ?? (p.Producto.PrecioOtro.HasValue ? p.Producto.PrecioOtro.Value * p.Cantidad : (decimal?)null),
                    Detalle = $"Pack x {p.Cantidad}"
                }).ToListAsync();
        }
        return Ok(result);
    }

    // ─── POST crear seccion ───
    public class CrearSeccionRequest { public string Titulo { get; set; } = ""; }
    [HttpPost("{listaId:int}/secciones")]
    public async Task<IActionResult> CrearSeccion(int listaId, [FromBody] CrearSeccionRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Titulo))
            return BadRequest(new { error = "Falta el título de la sección" });
        var lista = await _db.CafeListasPreciosCustom.FirstOrDefaultAsync(l => l.Id == listaId && l.IsActive);
        if (lista is null) return NotFound(new { error = "Lista no encontrada" });

        var maxOrden = await _db.CafeListasPreciosCustomSecciones
            .Where(s => s.ListaId == listaId).Select(s => (int?)s.Orden).MaxAsync() ?? -1;
        var seccion = new CafeListaPreciosCustomSeccion
        {
            ListaId = listaId,
            Titulo = req.Titulo.Trim(),
            Orden = maxOrden + 1,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeListasPreciosCustomSecciones.Add(seccion);
        lista.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { id = seccion.Id });
    }

    // ─── PUT renombrar seccion ───
    public class RenombrarSeccionRequest { public string Titulo { get; set; } = ""; }
    [HttpPut("secciones/{seccionId:int}")]
    public async Task<IActionResult> RenombrarSeccion(int seccionId, [FromBody] RenombrarSeccionRequest req)
    {
        if (req is null || string.IsNullOrWhiteSpace(req.Titulo))
            return BadRequest(new { error = "Falta el título" });
        var seccion = await _db.CafeListasPreciosCustomSecciones.FindAsync(seccionId);
        if (seccion is null) return NotFound(new { error = "Sección no encontrada" });
        seccion.Titulo = req.Titulo.Trim();
        var lista = await _db.CafeListasPreciosCustom.FindAsync(seccion.ListaId);
        if (lista != null) lista.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ─── DELETE seccion (cascada borra items) ───
    [HttpDelete("secciones/{seccionId:int}")]
    public async Task<IActionResult> BorrarSeccion(int seccionId)
    {
        var seccion = await _db.CafeListasPreciosCustomSecciones.FindAsync(seccionId);
        if (seccion is null) return NotFound(new { error = "Sección no encontrada" });
        var listaId = seccion.ListaId;
        _db.CafeListasPreciosCustomSecciones.Remove(seccion);
        var lista = await _db.CafeListasPreciosCustom.FindAsync(listaId);
        if (lista != null) lista.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ─── POST mover seccion arriba/abajo ───
    [HttpPost("secciones/{seccionId:int}/mover")]
    public async Task<IActionResult> MoverSeccion(int seccionId, [FromQuery] string direccion)
    {
        var seccion = await _db.CafeListasPreciosCustomSecciones.FindAsync(seccionId);
        if (seccion is null) return NotFound(new { error = "Sección no encontrada" });
        var todas = await _db.CafeListasPreciosCustomSecciones
            .Where(s => s.ListaId == seccion.ListaId)
            .OrderBy(s => s.Orden).ThenBy(s => s.Id).ToListAsync();
        var idx = todas.FindIndex(s => s.Id == seccionId);
        if (direccion == "arriba" && idx > 0)
            (todas[idx].Orden, todas[idx - 1].Orden) = (todas[idx - 1].Orden, todas[idx].Orden);
        else if (direccion == "abajo" && idx < todas.Count - 1)
            (todas[idx].Orden, todas[idx + 1].Orden) = (todas[idx + 1].Orden, todas[idx].Orden);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ─── POST agregar item a seccion ───
    public class AgregarItemRequest
    {
        public string TipoItem { get; set; } = ""; // PRODUCTO | COMBO | PACK
        public int RefId { get; set; }
        public string? Notas { get; set; }
        public bool EsNovedad { get; set; }
    }
    [HttpPost("secciones/{seccionId:int}/items")]
    public async Task<IActionResult> AgregarItem(int seccionId, [FromBody] AgregarItemRequest req)
    {
        var tipo = (req?.TipoItem ?? "").Trim().ToUpperInvariant();
        if (tipo != "PRODUCTO" && tipo != "COMBO" && tipo != "PACK")
            return BadRequest(new { error = "TipoItem inválido (PRODUCTO/COMBO/PACK)" });
        if (req!.RefId <= 0) return BadRequest(new { error = "RefId inválido" });
        var seccion = await _db.CafeListasPreciosCustomSecciones.FindAsync(seccionId);
        if (seccion is null) return NotFound(new { error = "Sección no encontrada" });

        // Validar que el item referenciado exista
        bool existe = tipo switch
        {
            "PRODUCTO" => await _db.CafeProductos.AnyAsync(p => p.Id == req.RefId),
            "COMBO" => await _db.CafeCombos.AnyAsync(c => c.Id == req.RefId),
            "PACK" => await _db.CafeProductoPacks.AnyAsync(p => p.Id == req.RefId),
            _ => false
        };
        if (!existe) return BadRequest(new { error = $"{tipo} #{req.RefId} no existe" });

        var maxOrden = await _db.CafeListasPreciosCustomItems
            .Where(i => i.SeccionId == seccionId).Select(i => (int?)i.Orden).MaxAsync() ?? -1;
        var item = new CafeListaPreciosCustomItem
        {
            SeccionId = seccionId,
            TipoItem = tipo,
            RefId = req.RefId,
            Orden = maxOrden + 1,
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas.Trim(),
            EsNovedad = req.EsNovedad,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeListasPreciosCustomItems.Add(item);
        var lista = await _db.CafeListasPreciosCustom.FindAsync(seccion.ListaId);
        if (lista != null) lista.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { id = item.Id });
    }

    // ─── POST bulk: agregar muchos items a una seccion en una sola llamada (skip duplicados) ───
    public class BulkItemInput { public string TipoItem { get; set; } = ""; public int RefId { get; set; } }
    public class BulkItemsRequest { public List<BulkItemInput> Items { get; set; } = new(); }
    [HttpPost("secciones/{seccionId:int}/items-bulk")]
    public async Task<IActionResult> AgregarItemsBulk(int seccionId, [FromBody] BulkItemsRequest req)
    {
        if (req?.Items == null || req.Items.Count == 0)
            return BadRequest(new { error = "Lista vacia" });
        var seccion = await _db.CafeListasPreciosCustomSecciones.FindAsync(seccionId);
        if (seccion is null) return NotFound(new { error = "Sección no encontrada" });

        // Items ya existentes en TODA la lista (no solo la seccion) para no duplicar.
        var seccionesDeLista = await _db.CafeListasPreciosCustomSecciones
            .Where(s => s.ListaId == seccion.ListaId).Select(s => s.Id).ToListAsync();
        var existentes = await _db.CafeListasPreciosCustomItems
            .Where(i => seccionesDeLista.Contains(i.SeccionId))
            .Select(i => new { i.TipoItem, i.RefId })
            .ToListAsync();
        var existentesSet = existentes.Select(e => $"{e.TipoItem}:{e.RefId}").ToHashSet();

        int maxOrden = await _db.CafeListasPreciosCustomItems
            .Where(i => i.SeccionId == seccionId).Select(i => (int?)i.Orden).MaxAsync() ?? -1;

        int agregados = 0, salteados = 0;
        foreach (var inp in req.Items)
        {
            var tipo = (inp.TipoItem ?? "").Trim().ToUpperInvariant();
            if (tipo != "PRODUCTO" && tipo != "COMBO" && tipo != "PACK") { salteados++; continue; }
            if (inp.RefId <= 0) { salteados++; continue; }
            if (existentesSet.Contains($"{tipo}:{inp.RefId}")) { salteados++; continue; }
            _db.CafeListasPreciosCustomItems.Add(new CafeListaPreciosCustomItem
            {
                SeccionId = seccionId, TipoItem = tipo, RefId = inp.RefId,
                Orden = ++maxOrden, CreatedAt = DateTime.UtcNow
            });
            existentesSet.Add($"{tipo}:{inp.RefId}");
            agregados++;
        }
        var lista = await _db.CafeListasPreciosCustom.FindAsync(seccion.ListaId);
        if (lista != null) lista.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { agregados, salteados });
    }

    // ─── DELETE item ───
    [HttpDelete("items/{itemId:int}")]
    public async Task<IActionResult> BorrarItem(int itemId)
    {
        var item = await _db.CafeListasPreciosCustomItems.FindAsync(itemId);
        if (item is null) return NotFound(new { error = "Item no encontrado" });
        var seccion = await _db.CafeListasPreciosCustomSecciones.FindAsync(item.SeccionId);
        _db.CafeListasPreciosCustomItems.Remove(item);
        if (seccion != null)
        {
            var lista = await _db.CafeListasPreciosCustom.FindAsync(seccion.ListaId);
            if (lista != null) lista.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ─── PUT toggle NOVEDAD del item ───
    [HttpPut("items/{itemId:int}/novedad")]
    public async Task<IActionResult> ToggleNovedad(int itemId)
    {
        var item = await _db.CafeListasPreciosCustomItems.FindAsync(itemId);
        if (item is null) return NotFound(new { error = "Item no encontrado" });
        item.EsNovedad = !item.EsNovedad;
        var seccion = await _db.CafeListasPreciosCustomSecciones.FindAsync(item.SeccionId);
        if (seccion != null)
        {
            var lista = await _db.CafeListasPreciosCustom.FindAsync(seccion.ListaId);
            if (lista != null) lista.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return Ok(new { esNovedad = item.EsNovedad });
    }

    // ─── POST reordenar items de una seccion en bulk (drag&drop) ───
    public class ReorderItemsRequest { public List<int> ItemIds { get; set; } = new(); }
    [HttpPost("secciones/{seccionId:int}/reorder-items")]
    public async Task<IActionResult> ReorderItems(int seccionId, [FromBody] ReorderItemsRequest req)
    {
        if (req?.ItemIds == null || req.ItemIds.Count == 0)
            return BadRequest(new { error = "Lista vacia" });
        var items = await _db.CafeListasPreciosCustomItems
            .Where(i => i.SeccionId == seccionId && req.ItemIds.Contains(i.Id))
            .ToListAsync();
        var idToOrden = req.ItemIds.Select((id, idx) => new { id, idx }).ToDictionary(x => x.id, x => x.idx);
        foreach (var it in items)
        {
            if (idToOrden.TryGetValue(it.Id, out var nuevoOrden)) it.Orden = nuevoOrden;
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, total = items.Count });
    }

    // ─── POST mover item arriba/abajo dentro de su seccion ───
    [HttpPost("items/{itemId:int}/mover")]
    public async Task<IActionResult> MoverItem(int itemId, [FromQuery] string direccion)
    {
        var item = await _db.CafeListasPreciosCustomItems.FindAsync(itemId);
        if (item is null) return NotFound(new { error = "Item no encontrado" });
        var todos = await _db.CafeListasPreciosCustomItems
            .Where(i => i.SeccionId == item.SeccionId)
            .OrderBy(i => i.Orden).ThenBy(i => i.Id).ToListAsync();
        var idx = todos.FindIndex(i => i.Id == itemId);
        if (direccion == "arriba" && idx > 0)
            (todos[idx].Orden, todos[idx - 1].Orden) = (todos[idx - 1].Orden, todos[idx].Orden);
        else if (direccion == "abajo" && idx < todos.Count - 1)
            (todos[idx].Orden, todos[idx + 1].Orden) = (todos[idx + 1].Orden, todos[idx].Orden);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    // ═══════════════════════════════════════════════════════════════════════
    // FASE 3 — Descargar PDF (estilo TAKE AWAY)
    // ═══════════════════════════════════════════════════════════════════════
    [HttpGet("{listaId:int}/pdf")]
    public async Task<IActionResult> DescargarPdf(int listaId)
    {
        var lista = await _db.CafeListasPreciosCustom
            .Include(l => l.ClienteNav)
            .FirstOrDefaultAsync(l => l.Id == listaId && l.IsActive);
        if (lista is null) return NotFound();

        var negocio = await _db.CafeSettings.FirstOrDefaultAsync() ?? new CafeSetting();

        var secciones = await _db.CafeListasPreciosCustomSecciones
            .Where(s => s.ListaId == listaId)
            .OrderBy(s => s.Orden).ThenBy(s => s.Id)
            .Include(s => s.Items)
            .ToListAsync();

        var productoIds = secciones.SelectMany(s => s.Items).Where(i => i.TipoItem == "PRODUCTO").Select(i => i.RefId).Distinct().ToList();
        var comboIds = secciones.SelectMany(s => s.Items).Where(i => i.TipoItem == "COMBO").Select(i => i.RefId).Distinct().ToList();
        var packIds = secciones.SelectMany(s => s.Items).Where(i => i.TipoItem == "PACK").Select(i => i.RefId).Distinct().ToList();

        // 2026-06-17: para CAFE necesitamos el producto entero (Costo, OEM, Packs, Pvp1, etc.) +
        // los settings, asi podemos calcular 1 kg / ½ kg / ¼ kg con CafePricingService.
        var productosEntidad = await _db.CafeProductos
            .Include(p => p.MarcaNav)
            .Include(p => p.OemNav)
            .Include(p => p.Packs)
            .Where(p => productoIds.Contains(p.Id))
            .ToListAsync();
        var combos = await _db.CafeCombos.Where(c => comboIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Nombre, c.Sku, c.Marca, Precio = (decimal?)c.PrecioReferencia }).ToListAsync();
        var packs = await _db.CafeProductoPacks.Include(p => p.Producto).ThenInclude(p => p!.MarcaNav)
            .Where(p => packIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Nombre, p.Cantidad, p.PrecioOverride, ProdNombre = p.Producto!.Nombre, p.Producto.PrecioBar, p.Producto.PrecioOtro, p.Producto.Sku, Marca = p.Producto.Marca ?? (p.Producto.MarcaNav != null ? p.Producto.MarcaNav.Nombre : null) }).ToListAsync();

        var esBar = string.Equals(lista.TipoCliente, "BAR", StringComparison.OrdinalIgnoreCase);
        var tipoCli = esBar ? Services.CafePricingService.TIPO_BAR : Services.CafePricingService.TIPO_OTRO;

        var seccionesPdf = secciones.Select(s => new CafeListaCustomPdfService.SeccionInfo(
            s.Titulo,
            s.Items.OrderBy(i => i.Orden).ThenBy(i => i.Id).Select(i =>
            {
                string? sku = null; string? nombre = $"#{i.RefId}"; string? marca = null; decimal? precio = null; string? detalle = null;
                decimal? p1Kg = null, pMedio = null, pCuarto = null;
                if (i.TipoItem == "PRODUCTO")
                {
                    var p = productosEntidad.FirstOrDefault(x => x.Id == i.RefId);
                    if (p != null)
                    {
                        sku = p.Sku;
                        nombre = p.Nombre;
                        marca = p.Marca ?? p.MarcaNav?.Nombre;
                        if (string.Equals(p.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase))
                        {
                            // Para CAFE: calcular precio 1 kg / ½ kg / ¼ kg con CafePricingService.
                            p1Kg = Services.CafePricingService.CalcularPrecioUnitario(p, Services.CafePricingService.FORMATO_1KG, tipoCli, negocio);
                            pMedio = Services.CafePricingService.CalcularPrecioUnitario(p, Services.CafePricingService.FORMATO_MEDIO, tipoCli, negocio);
                            pCuarto = Services.CafePricingService.CalcularPrecioUnitario(p, Services.CafePricingService.FORMATO_CUARTO, tipoCli, negocio);
                        }
                        else
                        {
                            precio = esBar ? (p.PrecioBar ?? p.PrecioOtro) : (p.PrecioOtro ?? p.PrecioBar);
                        }
                    }
                }
                else if (i.TipoItem == "COMBO")
                {
                    var c = combos.FirstOrDefault(x => x.Id == i.RefId);
                    if (c != null) { sku = c.Sku; nombre = c.Nombre; marca = c.Marca; precio = c.Precio; detalle = "Combo"; }
                }
                else if (i.TipoItem == "PACK")
                {
                    var p = packs.FirstOrDefault(x => x.Id == i.RefId);
                    if (p != null)
                    {
                        sku = p.Sku;
                        nombre = p.ProdNombre;
                        marca = p.Marca;
                        var precioBase = esBar ? (p.PrecioBar ?? p.PrecioOtro) : (p.PrecioOtro ?? p.PrecioBar);
                        precio = p.PrecioOverride ?? (precioBase * p.Cantidad);
                        detalle = $"Pack x {p.Cantidad}";
                    }
                }
                return new CafeListaCustomPdfService.ItemInfo(sku, nombre ?? "", marca, detalle, precio, i.EsNovedad, p1Kg, pMedio, pCuarto);
            }).ToList()
        )).ToList();

        var input = new CafeListaCustomPdfService.PdfInput(
            negocio,
            new CafeListaCustomPdfService.ListaInfo(lista.Nombre, lista.NumeroLista, lista.Observaciones, lista.TipoCliente, lista.ClienteNav?.Nombre, lista.BackgroundUrl),
            seccionesPdf
        );

        var pdf = _pdfService.GenerarPdf(input);
        var safeName = string.Concat((lista.Nombre ?? "lista").Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray()).Trim();
        if (string.IsNullOrEmpty(safeName)) safeName = $"lista-{listaId}";
        return File(pdf, "application/pdf", $"{safeName}.pdf");
    }
}
