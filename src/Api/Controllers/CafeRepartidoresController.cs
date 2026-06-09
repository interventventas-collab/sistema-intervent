using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>CRUD admin de repartidores (los que entran a /repartidor/{token} con PIN).</summary>
[ApiController]
[Route("api/cafe/repartidores")]
[Authorize]
public class CafeRepartidoresController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafeRepartidoresController(AppDbContext db) { _db = db; }

    public record RepartidorDto(int Id, string Nombre, string? DniUltimos3, bool IsActive, string? PublicToken);
    public record CrearRequest(string Nombre, string? DniUltimos3);
    public record EditarRequest(string Nombre, string? DniUltimos3, bool IsActive);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var l = await _db.CafeRepartidores.OrderBy(r => r.Nombre)
            .Select(r => new RepartidorDto(r.Id, r.Nombre, r.DniUltimos3, r.IsActive, r.PublicToken))
            .ToListAsync();
        return Ok(l);
    }

    /// <summary>2026-06-05: Generar o regenerar el PublicToken (URL fija /mis-pedidos/{token}).
    /// Si el repartidor perdio el celu, usamos este endpoint para invalidar el anterior y darle uno nuevo.</summary>
    [HttpPost("{id:int}/regenerar-public-token")]
    public async Task<IActionResult> RegenerarPublicToken(int id)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound();
        r.PublicToken = Guid.NewGuid().ToString("N");
        r.UpdatedAt = DateTime.UtcNow;
        // Tambien revocar todas las sesiones activas del repartidor (por si perdio el celu)
        var sesiones = await _db.CafeRepartidorSesiones.Where(s => s.RepartidorId == id && !s.Revoked).ToListAsync();
        foreach (var s in sesiones) s.Revoked = true;
        await _db.SaveChangesAsync();
        return Ok(new { publicToken = r.PublicToken, sesionesRevocadas = sesiones.Count });
    }

    [HttpPost]
    public async Task<IActionResult> Crear([FromBody] CrearRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Nombre)) return BadRequest(new { error = "Nombre requerido" });
        var r = new CafeRepartidor
        {
            Nombre = req.Nombre.Trim(),
            DniUltimos3 = LimpiarPin(req.DniUltimos3),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeRepartidores.Add(r);
        await _db.SaveChangesAsync();
        return Ok(new RepartidorDto(r.Id, r.Nombre, r.DniUltimos3, r.IsActive, r.PublicToken));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Editar(int id, [FromBody] EditarRequest req)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound();
        if (!string.IsNullOrWhiteSpace(req.Nombre)) r.Nombre = req.Nombre.Trim();
        r.DniUltimos3 = LimpiarPin(req.DniUltimos3);
        r.IsActive = req.IsActive;
        r.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new RepartidorDto(r.Id, r.Nombre, r.DniUltimos3, r.IsActive, r.PublicToken));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Borrar(int id)
    {
        var r = await _db.CafeRepartidores.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return NotFound();
        var usadoEnCobranzas = await _db.CafeCobranzasPendientes.AnyAsync(c => c.RepartidorId == id);
        if (usadoEnCobranzas)
        {
            r.IsActive = false;
            r.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return Ok(new { soft = true, mensaje = "Repartidor desactivado (tiene cobranzas asociadas, no se puede borrar)" });
        }
        _db.CafeRepartidores.Remove(r);
        await _db.SaveChangesAsync();
        return Ok(new { soft = false });
    }

    // 2026-06-05: log de QR escaneos (auditoria)
    public record QrEscaneoDto(int Id, int VentaId, string? VentaNumero, int RepartidorId, string RepartidorNombre,
        string Accion, DateTime CreatedAt, string? Ip);

    /// <summary>2026-06-05: Reasigna una venta a otro repartidor. Borra el escaneo
    /// "cargado" actual (si existe) y crea uno nuevo para el repartidor destino.
    /// Si nuevoRepartidorId es null, solo desvincula (la venta queda sin nadie asignado).</summary>
    public class ReasignarVentaRequest
    {
        public int VentaId { get; set; }
        public int? NuevoRepartidorId { get; set; }
    }

    /// <summary>2026-06-05: Desmarcar una entrega (admin). Limpia EntregadoPorRepartidorId,
    /// EntregadoAt, vuelve EstadoPreparacion a PARA_PREPARAR, y borra el escaneo "entregado".
    /// Deja el "cargado" intacto si lo tenia (sigue en la lista del repartidor).</summary>
    [HttpPost("ventas/{ventaId:int}/desmarcar-entrega")]
    public async Task<IActionResult> DesmarcarEntrega(int ventaId)
    {
        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == ventaId);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });
        if (!v.EntregadoPorRepartidorId.HasValue)
            return BadRequest(new { error = "Esta venta no esta marcada como entregada" });

        // Limpiar campos de entrega
        v.EntregadoPorRepartidorId = null;
        v.EntregadoAt = null;
        if (v.EstadoPreparacion == "ENTREGADO")
        {
            v.EstadoPreparacion = "PARA_PREPARAR";
            v.PreparacionUpdatedAt = DateTime.UtcNow;
            // 2026-06-09: log obligatorio cuando admin desmarca una entrega y la venta vuelve al tablero.
            // Antes pasaba silencioso y el armador no entendia por que reaparecia.
            _db.CafeVentaPreparacionLogs.Add(new CafeVentaPreparacionLog
            {
                VentaId = v.Id,
                EstadoAnterior = "ENTREGADO",
                EstadoNuevo = "PARA_PREPARAR",
                OperadorNombre = "admin (desmarcar entrega)",
                Notas = "Admin uso Desmarcar Entrega — la venta vuelve a aparecer en Para Armar",
                CreatedAt = DateTime.UtcNow
            });
        }

        // Borrar escaneos "entregado" del log
        var entregados = await _db.CafeQrEscaneos
            .Where(e => e.VentaId == ventaId && e.Accion == "entregado")
            .ToListAsync();
        _db.CafeQrEscaneos.RemoveRange(entregados);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, escaneosBorrados = entregados.Count });
    }

    [HttpPost("qr-escaneos/reasignar")]
    public async Task<IActionResult> ReasignarEscaneo([FromBody] ReasignarVentaRequest req)
    {
        var v = await _db.CafeVentas.FirstOrDefaultAsync(x => x.Id == req.VentaId);
        if (v is null) return NotFound(new { error = "Venta no encontrada" });

        // Borrar los escaneos "cargado" existentes para esta venta (cualquier repartidor)
        var existentes = await _db.CafeQrEscaneos
            .Where(e => e.VentaId == req.VentaId && e.Accion == "cargado")
            .ToListAsync();
        _db.CafeQrEscaneos.RemoveRange(existentes);

        // Si hay nuevoRepartidorId, crear escaneo "cargado" a ese repartidor
        string mensaje;
        if (req.NuevoRepartidorId.HasValue)
        {
            var nuevoRep = await _db.CafeRepartidores.FirstOrDefaultAsync(r => r.Id == req.NuevoRepartidorId.Value && r.IsActive);
            if (nuevoRep is null) return NotFound(new { error = "Repartidor destino no encontrado" });
            _db.CafeQrEscaneos.Add(new CafeQrEscaneo
            {
                VentaId = v.Id,
                RepartidorId = nuevoRep.Id,
                Accion = "cargado",
                CreatedAt = DateTime.UtcNow,
                Ip = "admin-reasignar"
            });
            mensaje = $"Venta reasignada a {nuevoRep.Nombre}";
        }
        else
        {
            mensaje = "Venta desvinculada (sin repartidor asignado)";
        }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, mensaje, escaneosBorrados = existentes.Count });
    }

    [HttpGet("qr-escaneos")]
    public async Task<IActionResult> ListarQrEscaneos([FromQuery] int? ventaId = null,
        [FromQuery] int? repartidorId = null, [FromQuery] int dias = 30)
    {
        var desde = DateTime.UtcNow.AddDays(-Math.Max(1, dias));
        var q = _db.CafeQrEscaneos.AsQueryable().Where(e => e.CreatedAt >= desde);
        if (ventaId.HasValue) q = q.Where(e => e.VentaId == ventaId.Value);
        if (repartidorId.HasValue) q = q.Where(e => e.RepartidorId == repartidorId.Value);
        var rows = await q
            .OrderByDescending(e => e.CreatedAt)
            .Take(500)
            .Join(_db.CafeRepartidores, e => e.RepartidorId, r => r.Id, (e, r) => new { e, r })
            .Join(_db.CafeVentas, x => x.e.VentaId, v => v.Id, (x, v) => new QrEscaneoDto(
                x.e.Id, x.e.VentaId, v.Numero, x.r.Id, x.r.Nombre, x.e.Accion, x.e.CreatedAt, x.e.Ip))
            .ToListAsync();
        return Ok(rows);
    }

    private static string? LimpiarPin(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        var digits = new string(p.Where(char.IsDigit).ToArray());
        return digits.Length == 0 ? null : (digits.Length > 3 ? digits[..3] : digits);
    }
}
