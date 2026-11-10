using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-11-10: armar pedidos desde el celu del depósito (/depo/preparacion), sin usuario.
/// Pide las dos cosas: el pase de la clave del día (encabezado X-Pase-Celu) y que sea el celu
/// HABILITADO del depósito (el mismo del picking). Muestra los mismos pedidos que el tablero
/// "Pedidos para armar", sin plata: número, cliente, cuántos productos y cómo se entrega.
/// Abrir un pedido arma (o reusa) su lista de picking y el celu sigue en /picking/{token}.
/// "Ya armado" hace lo mismo que el botón LISTO del tablero, anotando quién lo armó.
/// Las rutas van bajo /api/public/ (permitida en WaMovilScopeMiddleware para los celus).
/// </summary>
[ApiController]
[Route("api/public/depo-preparacion")]
[AllowAnonymous]
public class DepoCeluPreparacionController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly ClaveDiaCeluService _clave;

    // Mismos días que pide el tablero (CafePreparacion.razor → GetCafePreparacionAsync(7)).
    private const int DIAS_TABLERO = 7;
    private const string CREADA_POR_CELU = "Celu del depósito";

    public DepoCeluPreparacionController(AppDbContext db, ClaveDiaCeluService clave)
    {
        _db = db;
        _clave = clave;
    }

    public record CeluPedidoRequest(string? DeviceId, string? Quien);

    private ObjectResult PaseVencido() => StatusCode(403, new { error = "La clave cambió. Pedí la de hoy." });
    private static OkObjectResult NoHabilitado() => new(new { estado = "no_habilitado" });

    /// <summary>Revisa pase y celu. Devuelve null si está todo bien; si no, la respuesta a mandar.</summary>
    private async Task<IActionResult?> ControlarAsync(string? deviceId)
    {
        if (!_clave.PaseValido(Request)) return PaseVencido();
        if (!await ClaveDiaCeluService.EsCeluHabilitadoAsync(_db, deviceId)) return NoHabilitado();
        return null;
    }

    /// <summary>Personas para "¿quién lo armó?": las mismas del tablero (preparacion/operadores).</summary>
    private Task<List<string>> OperadoresAsync()
        => _db.StockOperadores.AsNoTracking().Where(o => o.IsActive)
            .OrderBy(o => o.Orden == 0 ? 999 : o.Orden).ThenBy(o => o.Nombre)
            .Select(o => o.Nombre).ToListAsync();

    /// <summary>GET pedidos?deviceId=… — los pedidos para armar del tablero, sin importes.</summary>
    [HttpGet("pedidos")]
    public async Task<IActionResult> Pedidos([FromQuery] string? deviceId)
    {
        if (await ControlarAsync(deviceId) is { } corte) return corte;

        var pedidos = await CafeVentasController.EnTableroPreparacion(_db.CafeVentas.AsNoTracking(), DIAS_TABLERO)
            .OrderByDescending(v => v.DriveSubidoAt ?? v.CreatedAt)   // mismo orden que el tablero
            .Select(v => new
            {
                id = v.Id,
                numero = v.Numero,
                cliente = v.ClienteNombreSnapshot ?? "Consumidor final",
                productos = v.Items.Count,
                unidades = v.Items.Sum(i => (int?)i.Cantidad) ?? 0,
                entregaPor = v.EntregaPor,
                modificado = v.ModificadoDespuesDeArmar
            })
            .ToListAsync();

        return Ok(new { estado = "ok", pedidos, operadores = await OperadoresAsync() });
    }

    /// <summary>POST {id}/abrir — la lista de picking de ESE pedido. Si ya se armó una para él
    /// después del último cambio de la venta, se reusa (así no se pierden los tildes); si no,
    /// se arma una nueva igual que "Llevar al celu" del tablero.</summary>
    [HttpPost("{id:int}/abrir")]
    public async Task<IActionResult> Abrir(int id, [FromBody] CeluPedidoRequest? req)
    {
        if (await ControlarAsync(req?.DeviceId) is { } corte) return corte;

        var v = await _db.CafeVentas.Include(x => x.Items).FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return NotFound(new { error = "No se encontró el pedido." });

        var desde = new[] { v.CreatedAt, v.UpdatedAt ?? v.CreatedAt, v.PreparacionUpdatedAt ?? v.CreatedAt }.Max();
        var token = string.IsNullOrEmpty(v.Numero) ? null : await _db.CafePickingListas.AsNoTracking()
            .Where(l => l.CantidadPedidos == 1 && l.VentaNumeros == v.Numero && l.CreatedAt >= desde)
            .OrderByDescending(l => l.Id)
            .Select(l => l.Token)
            .FirstOrDefaultAsync();

        if (token is null)
        {
            var quien = string.IsNullOrWhiteSpace(req?.Quien) ? CREADA_POR_CELU : req!.Quien!.Trim();
            var lista = await CafePickingController.CrearListaAsync(_db, new() { v }, quien);
            token = lista.Token;
        }

        return Ok(new { estado = "ok", token });
    }

    /// <summary>POST {id}/armado — "Ya armado": pasa a LISTO como el botón del tablero y anota quién.</summary>
    [HttpPost("{id:int}/armado")]
    public async Task<IActionResult> Armado(int id, [FromBody] CeluPedidoRequest? req)
    {
        if (await ControlarAsync(req?.DeviceId) is { } corte) return corte;

        var quien = req?.Quien?.Trim() ?? "";
        var operadores = await OperadoresAsync();
        var elegido = operadores.FirstOrDefault(o => string.Equals(o, quien, StringComparison.OrdinalIgnoreCase));
        if (elegido is null) return BadRequest(new { error = "Elegí quién lo armó." });

        var v = await CafeVentasController.EnTableroPreparacion(_db.CafeVentas, DIAS_TABLERO)
            .FirstOrDefaultAsync(x => x.Id == id);
        if (v is null) return Conflict(new { error = "Ese pedido ya no está para armar." });

        CafeVentasController.AplicarEstadoPreparacion(_db, v, "LISTO", elegido, "Ya armado desde el celu del depósito");
        await _db.SaveChangesAsync();
        return Ok(new { estado = "ok", id = v.Id, numero = v.Numero });
    }
}
