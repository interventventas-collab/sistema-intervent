using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-26: datos propios de la tablet del tostadero (/tostadero).
/// - GET  meli  → ventas de MercadoLibre para despachar que llevan café (con tamaño y molienda).
/// - POST listo → marca / desmarca "Café listo" de un pedido (VENTA o MELI). El depósito lo ve.
/// Los pedidos del sistema siguen saliendo de /api/cafe/ventas/preparacion (trae cafeListoAt).
/// </summary>
[ApiController]
[Route("api/tostadero")]
[Authorize]
public class TostaderoController : ControllerBase
{
    private readonly AppDbContext _db;
    public TostaderoController(AppDbContext db) { _db = db; }

    public record CafeMeliDto(string Producto, string? Formato, int Cantidad, string Molienda);
    public record VentaMeliDto(long Numero, DateTime Fecha, string Cuenta, string Comprador, string Tipo,
        bool EtiquetaImpresa, DateTime? CafeListoAt, List<CafeMeliDto> Cafes, List<string> Otros);
    public record ListoReq(string Tipo, long Ref, bool Listo);

    // Palabras para decir QUÉ molido es (la molienda en MeLi está escrita en el título).
    private static readonly (string Clave, string Texto)[] TiposMolido =
    {
        ("espresso", "Molido espresso"), ("expreso", "Molido espresso"), ("express", "Molido espresso"),
        ("espreso", "Molido espresso"), ("expresso", "Molido espresso"),
        ("filtro", "Molido filtro"), ("goteo", "Molido filtro"), ("melita", "Molido filtro"),
        ("moka", "Molido moka"), ("moca", "Molido moka"), ("napolitana", "Molido moka"),
        ("prensa", "Molido prensa francesa"), ("embolo", "Molido prensa francesa"), ("émbolo", "Molido prensa francesa"),
        ("turca", "Molido turco"),
    };

    private static string Molienda(string? variacion, string? titulo)
    {
        var tipo = MeliOrderService.ClasificarPorVariante(variacion);
        if (tipo == MeliOrderService.MoliendaTipo.Desconocido) tipo = MeliOrderService.ClasificarPorTitulo(titulo);
        if (tipo == MeliOrderService.MoliendaTipo.Granos) return "Grano";
        if (tipo == MeliOrderService.MoliendaTipo.Desconocido) return "¿Grano o molido? ver publi";
        var t = ((variacion ?? "") + " " + (titulo ?? "")).ToLowerInvariant();
        foreach (var (k, texto) in TiposMolido) if (t.Contains(k)) return texto;
        return "Molido";
    }

    /// <summary>Cuando el vínculo no dice el tamaño, se saca del título de la publi ("1/2 Kg", "250 g"...).</summary>
    private static string? FormatoDelTitulo(string? titulo)
    {
        var t = (titulo ?? "").ToLowerInvariant().Replace(" ", "");
        if (t.Contains("1/4k") || t.Contains("250g") || t.Contains("cuartokilo")) return "CUARTO";
        if (t.Contains("1/2k") || t.Contains("500g") || t.Contains("mediokilo") || t.Contains("medikilo")) return "MEDIO";
        if (t.Contains("1kg") || t.Contains("1kilo") || t.Contains("1000g")) return "1KG";
        return null;
    }

    [HttpGet("meli")]
    public async Task<ActionResult<List<VentaMeliDto>>> VentasMeliConCafe([FromQuery] int dias = 7)
    {
        if (dias < 1) dias = 1;
        if (dias > 30) dias = 30;
        // MeliOrders.DateCreated ya está en hora argentina (ver MeliDepositoController.Ordenes).
        var desdeAr = DateTime.UtcNow.AddHours(-3).Date.AddDays(-(dias - 1));

        // Para despachar = no cancelada, no entregada ni en camino. Full lo despacha MeLi: afuera.
        var filas = await _db.MeliOrders.AsNoTracking()
            .Where(o => o.DateCreated >= desdeAr && o.Status != "cancelled"
                        && (o.ShippingStatus == null || (o.ShippingStatus != "cancelled" && o.ShippingStatus != "delivered"
                            && o.ShippingStatus != "shipped" && o.ShippingStatus != "not_delivered"))
                        && (o.LogisticType == null || o.LogisticType != "fulfillment"))
            .Select(o => new
            {
                o.MeliOrderId, o.MeliAccountId, o.DateCreated, o.BuyerNickname, o.ItemId, o.VariationId, o.ItemTitle,
                o.Quantity, o.ShippingId, o.PackId, o.ShippingStatus, o.ShippingSubstatus, o.ShippingMode, o.LogisticType,
                o.EtiquetaImpresaAt
            })
            .ToListAsync();
        if (filas.Count == 0) return new List<VentaMeliDto>();

        var itemIds = filas.Select(f => f.ItemId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        var comps = await _db.MeliItemComponentes.AsNoTracking()
            .Where(c => itemIds.Contains(c.MeliItemId) && c.Producto != null)
            .Select(c => new { c.MeliItemId, c.MeliVariationId, c.CafeProductoId, c.Cantidad, c.Formato, Nombre = c.Producto!.Nombre, c.Producto.Categoria })
            .ToListAsync();
        // La mayoría de las publis de café NO usan MeliItemComponentes: están vinculadas directo en
        // MeliItems (CafeProductoId + CafeFormato). Se usan las dos fuentes.
        var publis = await _db.MeliItems.AsNoTracking()
            .Where(i => itemIds.Contains(i.MeliItemId))
            .Select(i => new
            {
                i.MeliItemId, i.VariationId, i.VariationAttributes, i.CafeProductoId, i.CafeFormato,
                Nombre = i.CafeProducto != null ? i.CafeProducto.Nombre : null,
                Categoria = i.CafeProducto != null ? i.CafeProducto.Categoria : null
            })
            .ToListAsync();
        var cuentas = await _db.MeliAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Nickname);

        var grupos = filas.GroupBy(f => f.ShippingId ?? f.PackId ?? f.MeliOrderId).ToList();
        var numeros = grupos.Select(g => g.Key).ToList();
        var listos = await _db.TostaderoCafeListos.AsNoTracking()
            .Where(t => t.Tipo == "MELI" && numeros.Contains(t.Ref))
            .ToDictionaryAsync(t => t.Ref, t => t.ListoAt);

        var res = new List<VentaMeliDto>();
        foreach (var g in grupos)
        {
            var cafes = new List<CafeMeliDto>();
            var otros = new List<string>();
            foreach (var f in g)
            {
                // Los de esa variante si los hay; si no, los que valen para toda la publi. Sin repetir:
                // hay publis con el mismo producto cargado varias veces (auto-link).
                var delItem = comps.Where(c => c.MeliItemId == f.ItemId).ToList();
                var exactos = delItem.Where(c => c.MeliVariationId != null && c.MeliVariationId == f.VariationId).ToList();
                var cs = (exactos.Count > 0 ? exactos : delItem.Where(c => c.MeliVariationId == null))
                    .GroupBy(c => new { c.CafeProductoId, c.Formato, c.Cantidad }).Select(x => x.First()).ToList();
                var cafe = cs.Where(c => c.Categoria == "CAFE").ToList();
                var publi = publis.FirstOrDefault(x => x.MeliItemId == f.ItemId && x.VariationId == f.VariationId)
                            ?? publis.FirstOrDefault(x => x.MeliItemId == f.ItemId);
                var mol = Molienda(publi?.VariationAttributes, f.ItemTitle);
                if (cafe.Count > 0)
                {
                    foreach (var c in cafe)
                        cafes.Add(new CafeMeliDto(c.Nombre, c.Formato ?? publi?.CafeFormato ?? FormatoDelTitulo(f.ItemTitle),
                            (int)Math.Round(f.Quantity * c.Cantidad), mol));
                }
                else if (publi?.Categoria == "CAFE" && publi.Nombre != null)
                {
                    cafes.Add(new CafeMeliDto(publi.Nombre, publi.CafeFormato ?? FormatoDelTitulo(f.ItemTitle), f.Quantity, mol));
                }
                else otros.Add($"{f.ItemTitle} ×{f.Quantity}");
            }
            if (cafes.Count == 0) continue;
            var p = g.OrderBy(x => x.DateCreated).First();
            var tipo = (p.LogisticType?.ToLowerInvariant()) switch
            {
                "self_service" => "Flex",
                _ => string.Equals(p.ShippingMode, "me1", StringComparison.OrdinalIgnoreCase) ? "ME1" : "Colecta / Correo"
            };
            var impresa = g.Any(x => x.EtiquetaImpresaAt != null)
                          || (p.ShippingStatus == "ready_to_ship" && p.ShippingSubstatus != null
                              && p.ShippingSubstatus != "ready_to_print" && p.ShippingSubstatus != "buffered");
            res.Add(new VentaMeliDto(g.Key, p.DateCreated, cuentas.TryGetValue(p.MeliAccountId, out var nk) ? nk : "",
                p.BuyerNickname, tipo, impresa, listos.TryGetValue(g.Key, out var la) ? la : null, cafes, otros));
        }
        return res.OrderBy(r => r.Fecha).ToList();
    }

    [HttpPost("listo")]
    public async Task<IActionResult> MarcarListo([FromBody] ListoReq req)
    {
        var tipo = (req.Tipo ?? "").Trim().ToUpperInvariant();
        if (tipo is not ("VENTA" or "MELI") || req.Ref <= 0) return BadRequest(new { error = "Pedido inválido" });
        var fila = await _db.TostaderoCafeListos.FirstOrDefaultAsync(t => t.Tipo == tipo && t.Ref == req.Ref);
        if (req.Listo && fila == null)
        {
            _db.TostaderoCafeListos.Add(new TostaderoCafeListo { Tipo = tipo, Ref = req.Ref, Quien = User.Identity?.Name });
            await _db.SaveChangesAsync();
        }
        else if (!req.Listo && fila != null)
        {
            _db.TostaderoCafeListos.Remove(fila);
            await _db.SaveChangesAsync();
        }
        return Ok();
    }

    /// <summary>Para el depósito: qué envíos de MeLi ya tienen el café listo (por número).</summary>
    [HttpGet("listos-meli")]
    public async Task<ActionResult<List<long>>> ListosMeli([FromQuery] int dias = 30)
    {
        var desde = DateTime.UtcNow.AddDays(-Math.Clamp(dias, 1, 90));
        return await _db.TostaderoCafeListos.AsNoTracking()
            .Where(t => t.Tipo == "MELI" && t.ListoAt >= desde).Select(t => t.Ref).ToListAsync();
    }
}
