using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>27/11/2026: "Mandar a cobrar" — ver <see cref="CobranzaVisitaService"/>.
/// Lo usan Ventas (menú ⋮), Nueva cobranza (los que deben) y el En vivo del mapa (pestaña Deben).</summary>
[ApiController]
[Route("api/cafe/cobranza-visitas")]
[Authorize]
public class CobranzaVisitasController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CobranzaVisitaService _svc;
    private readonly CafeReciboVisitaCobranzaPdfService _pdf;
    private readonly QrRepartidorService _qr;

    public CobranzaVisitasController(AppDbContext db, CobranzaVisitaService svc, CafeReciboVisitaCobranzaPdfService pdf, QrRepartidorService qr)
    {
        _db = db; _svc = svc; _pdf = pdf; _qr = qr;
    }

    public record RepartidorOpcion(int Id, string Nombre);
    public record PrepararDto(int ClienteId, int? VentaId, string Nombre, string? Direccion,
        List<CobranzaVisitaService.ComprobanteDto> Comprobantes, decimal SaldoTodas, decimal SaldoSolo,
        List<RepartidorOpcion> Repartidores, CobranzaVisitaService.AbiertaDto? Abierta, decimal SaldoAlquileres);

    /// <summary>Datos para la ventanita: lo que debe, los repartidores y si ya estaba mandada.</summary>
    [HttpGet("preparar")]
    public async Task<IActionResult> Preparar([FromQuery] int? clienteId, [FromQuery] int? ventaId)
    {
        int cid = clienteId ?? 0;
        Models.CafeVenta? venta = null;
        if (ventaId.HasValue)
        {
            venta = await _db.CafeVentas.AsNoTracking().FirstOrDefaultAsync(v => v.Id == ventaId.Value);
            if (venta is null) return NotFound(new { error = "No encontré la venta" });
            if (cid <= 0) cid = venta.ClienteId ?? 0;
        }
        var cli = cid > 0 ? await _db.CafeClientes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid) : null;
        var todas = cid > 0 ? await _svc.ComprobantesAsync(cid, null) : await _svc.ComprobantesAsync(0, ventaId);
        var saldoTodas = await _svc.SaldoAsync(cid, cid > 0 ? null : ventaId, todas);
        var saldoSolo = ventaId.HasValue ? todas.Where(c => c.VentaId == ventaId.Value).Sum(c => c.Saldo) : saldoTodas;
        var reps = await _db.CafeRepartidores.AsNoTracking().Where(r => r.IsActive).OrderBy(r => r.Nombre)
            .Select(r => new RepartidorOpcion(r.Id, r.Nombre)).ToListAsync();
        var abierta = (await _svc.AbiertasAsync()).FirstOrDefault(a => a.ClienteId == cid && (cid > 0 || a.VentaId == ventaId));
        var dir = string.Join(", ", new[] { cli?.DomicilioEntrega ?? cli?.Direccion ?? venta?.ClienteDomicilioEntregaSnapshot ?? venta?.ClienteDireccionSnapshot,
                                             cli?.LocalidadEntrega ?? cli?.Localidad ?? venta?.ClienteLocalidadSnapshot }
            .Where(x => !string.IsNullOrWhiteSpace(x)));
        return Ok(new PrepararDto(cid, ventaId, cli?.Nombre ?? venta?.ClienteNombreSnapshot ?? "Cliente",
            string.IsNullOrWhiteSpace(dir) ? null : dir, todas, saldoTodas, saldoSolo, reps, abierta,
            await _svc.SaldoAlquileresAsync(cid)));
    }

    [HttpPost]
    public async Task<IActionResult> Mandar([FromBody] CobranzaVisitaService.MandarRequest req)
    {
        var r = await _svc.MandarAsync(req);
        return r.Ok ? Ok(r) : BadRequest(new { error = r.Mensaje });
    }

    [HttpDelete("{stopId:int}")]
    public async Task<IActionResult> Cancelar(int stopId)
        => await _svc.CancelarAsync(stopId) ? Ok(new { ok = true }) : BadRequest(new { error = "Ya no se puede cancelar (ya la hizo o no existe)" });

    /// <summary>Las mandadas sin resolver: para mostrar "🚚 Mati · vie 28/11" en Ventas y en Nueva cobranza.</summary>
    [HttpGet("abiertas")]
    public async Task<IActionResult> Abiertas() => Ok(await _svc.AbiertasAsync());

    /// <summary>Pestaña "Deben" del En vivo del mapa.</summary>
    [HttpGet("deudores")]
    public async Task<IActionResult> Deudores([FromQuery] DateTime? fecha)
        => Ok(await _svc.DeudoresAsync((fecha ?? DateTime.UtcNow.AddHours(-3)).Date));

    /// <summary>Recibo de visita por cobranza. Por parada mandada, por cliente (todo lo que debe) o por venta.</summary>
    [HttpGet("recibo.pdf")]
    public async Task<IActionResult> Recibo([FromQuery] int? stopId, [FromQuery] int? clienteId, [FromQuery] int? ventaId, [FromQuery] bool todas = true)
    {
        var (datos, ventaQr) = await _svc.DatosReciboAsync(stopId, clienteId, ventaId, todas);
        if (datos is null) return NotFound(new { error = "No debe nada: no hay recibo para imprimir" });
        byte[]? qr = null;
        if (ventaQr is not null)
        {
            if (string.IsNullOrEmpty(ventaQr.PublicToken))
            {
                ventaQr.PublicToken = CafeVentasController.GeneratePublicToken();
                await _db.SaveChangesAsync();
            }
            qr = await _qr.GenerarQrAsync(ventaQr.PublicToken);
        }
        var cfg = await _db.CafeSettings.FindAsync(1);
        var bytes = _pdf.GenerarPdf(datos, cfg, qr);
        return File(bytes, "application/pdf", $"{datos.NumeroRecibo}.pdf");
    }
}
