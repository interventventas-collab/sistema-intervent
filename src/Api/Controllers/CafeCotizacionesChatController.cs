using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Presupuestos de productos pasados por el chat (🧮 Cotizar productos). 2026-10-17.
/// Pegados al TELÉFONO, como los de alquiler. Desde el chat se pasan al modo venta.
/// </summary>
[ApiController]
[Route("api/cafe/cotizaciones-chat")]
[Authorize]
public class CafeCotizacionesChatController : ControllerBase
{
    private readonly AppDbContext _db;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public CafeCotizacionesChatController(AppDbContext db) { _db = db; }

    public class ItemDto
    {
        public int ProductoId { get; set; }
        public string Sku { get; set; } = "";
        public string Nombre { get; set; } = "";
        /// <summary>UNIT | 1KG | MEDIO | CUARTO (constantes de CafePricingService).</summary>
        public string Formato { get; set; } = "UNIT";
        public decimal Cantidad { get; set; }
        /// <summary>Precio c/u tal cual figura en el mensaje (con o sin IVA según el presupuesto).</summary>
        public decimal PrecioCu { get; set; }
        /// <summary>El mismo precio SIN IVA: es el que se pone en la venta.</summary>
        public decimal PrecioSinIva { get; set; }
    }

    public class CotizacionDto
    {
        public int Id { get; set; }
        public string Telefono { get; set; } = "";
        public int? ClienteId { get; set; }
        public decimal Total { get; set; }
        public bool ConIva { get; set; }
        public string? Texto { get; set; }
        public string? Operador { get; set; }
        public int? VentaId { get; set; }
        public string? VentaNumero { get; set; }
        public DateTime CreatedAt { get; set; }
        public List<ItemDto> Items { get; set; } = new();
    }

    public class CrearRequest
    {
        public string Telefono { get; set; } = "";
        public int? ClienteId { get; set; }
        public decimal Total { get; set; }
        public bool ConIva { get; set; }
        public string? Texto { get; set; }
        public string? Operador { get; set; }
        public List<ItemDto> Items { get; set; } = new();
    }

    public class MarcarVentaRequest { public int VentaId { get; set; } }

    /// <summary>Los presupuestos de un teléfono, del más nuevo al más viejo.</summary>
    [HttpGet]
    public async Task<IActionResult> GetPorTelefono([FromQuery] string telefono, [FromQuery] int limit = 10)
    {
        var tel = SoloDigitos(telefono);
        if (tel.Length == 0) return BadRequest(new { error = "Falta el teléfono" });

        var filas = await _db.CafeCotizacionesChat.AsNoTracking()
            .Where(c => c.Telefono == tel)
            .OrderByDescending(c => c.CreatedAt)
            .Take(Math.Clamp(limit, 1, 50))
            .Select(c => new
            {
                c,
                // Si la venta se borró después, vuelve null y el presupuesto se puede pasar de nuevo.
                Numero = _db.CafeVentas.Where(v => v.Id == c.VentaId).Select(v => v.Numero).FirstOrDefault()
            })
            .ToListAsync();

        return Ok(filas.Select(f => ADto(f.c, f.Numero)).ToList());
    }

    /// <summary>Se llama solo al tocar "Pasar al chat" en el cotizador.</summary>
    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearRequest req)
    {
        var tel = SoloDigitos(req.Telefono);
        if (tel.Length == 0) return BadRequest(new { error = "Falta el teléfono" });
        var items = (req.Items ?? new()).Where(i => i.Cantidad > 0 && !string.IsNullOrWhiteSpace(i.Nombre)).ToList();
        if (items.Count == 0) return BadRequest(new { error = "El presupuesto está vacío" });

        var c = new CafeCotizacionChat
        {
            Telefono = tel,
            ClienteId = req.ClienteId,
            Total = req.Total,
            ConIva = req.ConIva,
            Texto = req.Texto is { Length: > 4000 } t ? t[..4000] : req.Texto,
            ItemsJson = JsonSerializer.Serialize(items, Json),
            Operador = string.IsNullOrWhiteSpace(req.Operador) ? null : req.Operador.Trim(),
            CreatedAt = DateTime.UtcNow,
        };
        _db.CafeCotizacionesChat.Add(c);
        await _db.SaveChangesAsync();
        return Ok(ADto(c, null));
    }

    /// <summary>Anota en qué venta se convirtió (lo llama el modo venta del chat al crearla).</summary>
    [HttpPost("{id:int}/venta")]
    public async Task<IActionResult> MarcarVenta(int id, [FromBody] MarcarVentaRequest req)
    {
        var c = await _db.CafeCotizacionesChat.FirstOrDefaultAsync(x => x.Id == id);
        if (c is null) return NotFound(new { error = "Presupuesto no encontrado" });
        c.VentaId = req.VentaId;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private static CotizacionDto ADto(CafeCotizacionChat c, string? ventaNumero)
    {
        List<ItemDto> items;
        try { items = JsonSerializer.Deserialize<List<ItemDto>>(c.ItemsJson, Json) ?? new(); }
        catch { items = new(); }
        return new CotizacionDto
        {
            Id = c.Id, Telefono = c.Telefono, ClienteId = c.ClienteId, Total = c.Total, ConIva = c.ConIva,
            Texto = c.Texto, Operador = c.Operador, VentaId = c.VentaId, VentaNumero = ventaNumero,
            CreatedAt = c.CreatedAt, Items = items,
        };
    }

    private static string SoloDigitos(string? s) => new string((s ?? "").Where(char.IsDigit).ToArray());
}
