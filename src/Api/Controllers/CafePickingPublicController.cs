using Api.Data;
using Api.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Camino al picking (escalon 1, 2026-08-02) — lado PUBLICO (sin login).
/// El celu del deposito abre /picking/{token} (QR) y va tildando lo que ya junto.
/// Regla del usuario: solo el celular HABILITADO del deposito puede usarlo (no telefonos
/// personales). El primer celu que se habilita queda registrado; el resto ve "no habilitado".
/// </summary>
[ApiController]
[Route("api/public/picking")]
[AllowAnonymous]
public class CafePickingPublicController : ControllerBase
{
    private readonly AppDbContext _db;
    public CafePickingPublicController(AppDbContext db) { _db = db; }

    public record AbrirRequest(string DeviceId);
    public record HabilitarRequest(string DeviceId, string? Nombre);
    public record TildarRequest(string DeviceId, bool Tildado);

    /// <summary>Abre la lista desde el celu. Devuelve estado del dispositivo:
    /// ok (habilitado, trae la lista) / sin_dispositivo (nadie habilitado aun, ofrece habilitar) /
    /// no_habilitado (ya hay otro celu registrado).</summary>
    [HttpPost("{token}/abrir")]
    public async Task<IActionResult> Abrir(string token, [FromBody] AbrirRequest req)
    {
        var lista = await _db.CafePickingListas.Include(l => l.Items)
            .FirstOrDefaultAsync(l => l.Token == token);
        if (lista is null) return NotFound(new { error = "Lista no encontrada" });

        var deviceId = req?.DeviceId?.Trim() ?? "";
        var deposito = await _db.CafePickingDispositivos.Where(x => x.Activo).OrderBy(x => x.Id).FirstOrDefaultAsync();

        string estado;
        if (deposito is null)
        {
            estado = "sin_dispositivo";
        }
        else if (!string.IsNullOrEmpty(deviceId) && deposito.DeviceId == deviceId)
        {
            deposito.LastSeenAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            estado = "ok";
        }
        else
        {
            estado = "no_habilitado";
        }

        return Ok(new
        {
            estado,
            lista = estado == "ok" ? Proyectar(lista) : null,
            info = new { creadaPor = lista.CreadaPor, cantidadPedidos = lista.CantidadPedidos, ventaNumeros = lista.VentaNumeros, createdAt = lista.CreatedAt }
        });
    }

    /// <summary>Habilita ESTE celu como el del deposito. Solo funciona si no hay otro activo.</summary>
    [HttpPost("{token}/habilitar-dispositivo")]
    public async Task<IActionResult> Habilitar(string token, [FromBody] HabilitarRequest req)
    {
        var lista = await _db.CafePickingListas.Include(l => l.Items)
            .FirstOrDefaultAsync(l => l.Token == token);
        if (lista is null) return NotFound(new { error = "Lista no encontrada" });

        var deviceId = req?.DeviceId?.Trim() ?? "";
        if (string.IsNullOrEmpty(deviceId)) return BadRequest(new { error = "Falta identificar el celular" });

        var deposito = await _db.CafePickingDispositivos.Where(x => x.Activo).OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (deposito is not null && deposito.DeviceId != deviceId)
            return Ok(new { estado = "no_habilitado" });  // ya hay otro celu del deposito

        if (deposito is null)
        {
            var previo = await _db.CafePickingDispositivos.FirstOrDefaultAsync(x => x.DeviceId == deviceId);
            if (previo is not null)
            {
                previo.Activo = true;
                previo.LastSeenAt = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(req?.Nombre)) previo.Nombre = req.Nombre;
            }
            else
            {
                _db.CafePickingDispositivos.Add(new CafePickingDispositivo
                {
                    DeviceId = deviceId,
                    Nombre = req?.Nombre,
                    Activo = true,
                    LastSeenAt = DateTime.UtcNow
                });
            }
            await _db.SaveChangesAsync();
        }

        return Ok(new { estado = "ok", lista = Proyectar(lista) });
    }

    /// <summary>Marca/desmarca un producto como ya juntado. Solo desde el celu habilitado.</summary>
    [HttpPost("{token}/item/{itemId:int}")]
    public async Task<IActionResult> Tildar(string token, int itemId, [FromBody] TildarRequest req)
    {
        var lista = await _db.CafePickingListas.FirstOrDefaultAsync(l => l.Token == token);
        if (lista is null) return NotFound();

        var deviceId = req?.DeviceId?.Trim() ?? "";
        var deposito = await _db.CafePickingDispositivos.Where(x => x.Activo).OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (deposito is null || deposito.DeviceId != deviceId)
            return StatusCode(403, new { error = "no_habilitado" });

        var item = await _db.CafePickingItems.FirstOrDefaultAsync(i => i.Id == itemId && i.PickingListaId == lista.Id);
        if (item is null) return NotFound();

        item.Tildado = req.Tildado;
        item.TildadoAt = req.Tildado ? DateTime.UtcNow : null;
        deposito.LastSeenAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { id = item.Id, tildado = item.Tildado });
    }

    private static object Proyectar(CafePickingLista l) => new
    {
        token = l.Token,
        creadaPor = l.CreadaPor,
        ventaNumeros = l.VentaNumeros,
        cantidadPedidos = l.CantidadPedidos,
        createdAt = l.CreatedAt,
        items = l.Items.OrderBy(i => i.Orden).Select(i => new
        {
            id = i.Id,
            productoNombre = i.ProductoNombre,
            formato = i.Formato,
            molienda = i.Molienda,
            sku = i.Sku,
            categoria = i.Categoria,
            cantidad = i.Cantidad,
            tildado = i.Tildado
        }).ToList()
    };
}
