using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Endpoints publicos (sin auth) usados por la pantalla mobile /repartidor/{token}.
/// El "login" del repartidor es el PIN de 3 digitos del DNI — patron tomado de Horas Extras.
/// La sesion del PIN es manejada por el frontend (15 min de inactividad). Aca cada endpoint
/// pide el PIN cada vez (el backend no guarda sesion), pero el frontend lo guarda y reenvia.
/// </summary>
[ApiController]
[Route("api/cafe/repartidor-public")]
[AllowAnonymous]
public class CafeRepartidorPublicController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeRepartidorPublicController(AppDbContext db) { _db = db; }

    public record RepartidorListItemDto(int Id, string Nombre);
    public record InfoVentaDto(int VentaId, string Numero, DateTime Fecha,
        string? ClienteNombre, string? ClienteDireccion, string? ClienteLocalidad, string? ClienteCiudad,
        decimal TotalCobrable, decimal SaldoPendiente,
        bool YaEntregada, string? EntregadoPor,
        List<ItemSimpleDto> Items);
    public record ItemSimpleDto(int Cantidad, string Nombre, string Formato, string? Molienda, bool EsDoyPack, bool EsEnvasePlateado);

    /// <summary>Lista de repartidores activos para el primer paso "¿Quien sos?".
    /// Solo Nombre + Id, sin PIN.</summary>
    [HttpGet("repartidores")]
    public async Task<IActionResult> Repartidores()
    {
        var l = await _db.CafeRepartidores.Where(r => r.IsActive)
            .OrderBy(r => r.Nombre)
            .Select(r => new RepartidorListItemDto(r.Id, r.Nombre))
            .ToListAsync();
        return Ok(l);
    }

    public record LoginRequest(int RepartidorId, string Pin);

    /// <summary>Valida que el PIN coincida con el repartidor. Devuelve el nombre si OK
    /// (el frontend lo usa para mostrar "Hola, Maxi"). NO devuelve token — el frontend
    /// guarda RepartidorId + Pin en sessionStorage y reenvía en cada request.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == req.RepartidorId && x.IsActive);
        if (r is null) return BadRequest(new { error = "Repartidor no encontrado" });
        if (string.IsNullOrEmpty(r.DniUltimos3)) return BadRequest(new { error = "Este repartidor no tiene PIN configurado. Avisale al admin." });
        if ((req.Pin ?? "").Trim() != r.DniUltimos3) return Unauthorized(new { error = "PIN incorrecto" });
        return Ok(new { id = r.Id, nombre = r.Nombre });
    }

    /// <summary>Devuelve info de la venta para el repartidor (al escanear el QR).
    /// NO pide PIN — la info no es sensible (cliente, importe, items).</summary>
    [HttpGet("venta/{token}")]
    public async Task<IActionResult> InfoVenta(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return BadRequest();
        var v = await _db.CafeVentas
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.PublicToken == token);
        if (v is null) return NotFound(new { error = "Venta no encontrada (token invalido)" });

        var totalCobrable = (v.ArcaImpTotal.HasValue && v.ArcaImpTotal.Value > 0m) ? v.ArcaImpTotal.Value : v.Total;
        var pagado = await _db.CafeCobranzasComprobantes
            .Where(c => c.VentaId == v.Id && c.Cobranza!.Estado == "VIGENTE").SumAsync(c => (decimal?)c.Importe) ?? 0m;
        var saldo = totalCobrable - pagado;

        string? entregadoPor = null;
        if (v.EntregadoPorRepartidorId.HasValue)
            entregadoPor = await _db.CafeRepartidores.Where(r => r.Id == v.EntregadoPorRepartidorId.Value)
                .Select(r => r.Nombre).FirstOrDefaultAsync();

        var items = v.Items.Select(i => new ItemSimpleDto(
            i.Cantidad, i.ProductoNombreSnapshot, i.Formato, i.Molienda, i.EsDoyPack, i.EsEnvasePlateado)).ToList();

        return Ok(new InfoVentaDto(
            v.Id, v.Numero, v.Fecha,
            v.ClienteNombreSnapshot, v.ClienteDireccionSnapshot, v.ClienteLocalidadSnapshot, v.ClienteCiudadSnapshot,
            totalCobrable, saldo,
            v.EntregadoPorRepartidorId.HasValue, entregadoPor,
            items));
    }

    public record CobrarRequest(int RepartidorId, string Pin, bool MarcarEntregado, decimal? Importe, string? Notas);

    /// <summary>Carga una cobranza pendiente. Valida PIN del repartidor en cada request.
    /// El Importe es opcional — si no viene, se asume que no cobro (solo entrego).
    /// Si MarcarEntregado=true y la venta esta en flujo de Preparacion, se setea a "ENTREGADO".
    /// </summary>
    [HttpPost("cobrar/{token}")]
    public async Task<IActionResult> Cobrar(string token, [FromBody] CobrarRequest req)
    {
        if (string.IsNullOrWhiteSpace(token)) return BadRequest();
        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.PublicToken == token);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        // Validar PIN
        var rep = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == req.RepartidorId && x.IsActive);
        if (rep is null) return BadRequest(new { error = "Repartidor no valido" });
        if (string.IsNullOrEmpty(rep.DniUltimos3) || (req.Pin ?? "").Trim() != rep.DniUltimos3)
            return Unauthorized(new { error = "PIN incorrecto" });

        var importe = Math.Max(0m, req.Importe ?? 0m);
        var marcoEntregado = req.MarcarEntregado;

        if (importe <= 0m && !marcoEntregado)
            return BadRequest(new { error = "No marcaste 'entregue' ni cargaste importe — no hay nada que guardar" });

        // Si solo marca entregado (sin importe), actualizar directo la venta sin crear cobranza pendiente
        if (importe <= 0m && marcoEntregado)
        {
            v.EntregadoPorRepartidorId = rep.Id;
            v.EntregadoAt = DateTime.UtcNow;
            if (v.EstadoPreparacion != null)
            {
                v.EstadoPreparacion = "ENTREGADO";
                v.PreparacionUpdatedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync();
            return Ok(new { soloEntrega = true, mensaje = $"✓ Marcaste como entregada (sin cobro)" });
        }

        // Sino, crear cobranza pendiente que el admin aprueba despues
        var pend = new CafeCobranzaPendiente
        {
            VentaId = v.Id,
            RepartidorId = rep.Id,
            Importe = importe,
            MarcadoEntregado = marcoEntregado,
            Notas = string.IsNullOrWhiteSpace(req.Notas) ? null : req.Notas!.Trim(),
            Estado = "PENDIENTE",
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeCobranzasPendientes.Add(pend);

        // Si marca entregado, anotar repartidor en la venta tambien (info inmediata aunque
        // la cobranza este pendiente de aprobar)
        if (marcoEntregado)
        {
            v.EntregadoPorRepartidorId = rep.Id;
            v.EntregadoAt = DateTime.UtcNow;
            if (v.EstadoPreparacion != null)
            {
                v.EstadoPreparacion = "ENTREGADO";
                v.PreparacionUpdatedAt = DateTime.UtcNow;
            }
        }

        await _db.SaveChangesAsync();
        return Ok(new { id = pend.Id, mensaje = $"✓ Cobranza precargada — el admin la va a aprobar despues" });
    }
}
