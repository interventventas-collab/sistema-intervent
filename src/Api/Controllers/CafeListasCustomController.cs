using Api.Data;
using Api.Models;
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

    public CafeListasCustomController(AppDbContext db)
    {
        _db = db;
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
                    CreatedAt = DateTime.UtcNow
                });
            }
        }
        await _db.SaveChangesAsync();
        return Ok(new { id = nueva.Id });
    }
}
