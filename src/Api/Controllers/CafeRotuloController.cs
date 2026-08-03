using Api.Data;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-08-03: Genera el rotulo/etiqueta de transporte de una venta en PDF.
/// Dos formatos: ?formato=a4 (hoja A4) o ?formato=termica (etiqueta 10x15 cm para impresora Zebra).
/// Arma los datos desde la venta (transporte + cliente destinatario + remitente) y devuelve el PDF
/// INLINE para que se abra en el navegador (no fuerza descarga).
/// </summary>
[ApiController]
[Route("api/cafe/rotulo")]
[Authorize]
public class CafeRotuloController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly CafeRotuloPdfService _pdfService;

    public CafeRotuloController(AppDbContext db, CafeRotuloPdfService pdfService)
    {
        _db = db;
        _pdfService = pdfService;
    }

    [HttpGet("{ventaId:int}")]
    public async Task<IActionResult> Generar(int ventaId, [FromQuery] string formato = "a4")
    {
        var venta = await _db.CafeVentas.FindAsync(ventaId);
        if (venta is null) return NotFound();

        var data = await BuildRotuloDataAsync(venta);

        var esTermica = string.Equals(formato, "termica", StringComparison.OrdinalIgnoreCase);
        var bytes = esTermica ? _pdfService.GenerarTermica(data) : _pdfService.GenerarA4(data);

        // INLINE: abre en el navegador (mismo estilo que la vista previa de la cotizacion).
        var nombre = $"rotulo-{venta.Numero}.pdf";
        Response.Headers["Content-Disposition"] = $"inline; filename=\"{nombre}\"";
        return File(bytes, "application/pdf");
    }

    private async Task<RotuloData> BuildRotuloDataAsync(Models.CafeVenta venta)
    {
        // ─── Transporte ───
        string transporteNombre;
        string? transporteDireccion = null, transporteTelefono = null,
                transporteLocalidad = null, transporteProvincia = null, transporteCp = null;
        bool pagoDestino = false;

        if (venta.TransporteId.HasValue)
        {
            var t = await _db.CafeTransportes.FindAsync(venta.TransporteId.Value);
            if (t is not null)
            {
                transporteNombre = t.Nombre;
                transporteDireccion = t.Direccion;
                transporteTelefono = t.Telefono;
                transporteLocalidad = t.Localidad;
                transporteProvincia = t.Provincia;
                transporteCp = t.CodigoPostal;
                pagoDestino = t.PagoDestino;
            }
            else
            {
                transporteNombre = venta.TransporteEmpresa ?? "-";
                transporteLocalidad = venta.TransporteDestino;
            }
        }
        else
        {
            transporteNombre = venta.TransporteEmpresa ?? "-";
            transporteLocalidad = venta.TransporteDestino;
        }

        // ─── Destinatario (cliente) ───
        string destNombre;
        string? destDniCuit = null, destTelefono = null, destDireccion = null,
                destLocalidad = null, destCp = null;
        string? destProvincia = null; // CafeCliente no tiene Provincia

        if (venta.ClienteId.HasValue)
        {
            var cli = await _db.CafeClientes.FindAsync(venta.ClienteId.Value);
            if (cli is not null)
            {
                destNombre = !string.IsNullOrWhiteSpace(cli.RazonSocial)
                    ? cli.RazonSocial!
                    : (!string.IsNullOrWhiteSpace(cli.Nombre) ? cli.Nombre : (venta.ClienteNombreSnapshot ?? "-"));
                destDniCuit = cli.Cuit;
                destTelefono = cli.Telefono;
                destDireccion = !string.IsNullOrWhiteSpace(cli.DomicilioEntrega) ? cli.DomicilioEntrega : cli.Direccion;
                destLocalidad = !string.IsNullOrWhiteSpace(cli.Localidad) ? cli.Localidad : cli.Ciudad;
                destCp = cli.Cp;
            }
            else
            {
                destNombre = venta.ClienteNombreSnapshot ?? "-";
            }
        }
        else
        {
            destNombre = venta.ClienteNombreSnapshot ?? "-";
        }

        // UsuarioMl / NotaProducto: se cablean despues.
        string? usuarioMl = null;
        string? notaProducto = null;

        // ─── Remitente ───
        string remNombre = "-";
        string? remCuit = null, remFantasia = null, remDireccion = null,
                remTelefono = null, remLocalidad = null, remCp = null;
        string? remProvincia = null;

        if (venta.RemitenteId.HasValue)
        {
            var rem = await _db.CafeRemitentes.FindAsync(venta.RemitenteId.Value);
            if (rem is not null)
            {
                remNombre = rem.Nombre;
                remCuit = rem.Cuit;
                remFantasia = rem.NombreFantasia;
                remDireccion = rem.Direccion;
                remTelefono = rem.Telefono;
                remLocalidad = rem.Localidad;
                remProvincia = rem.Provincia;
                remCp = rem.CodigoPostal;
            }
        }

        if (!venta.RemitenteId.HasValue || remNombre == "-")
        {
            var cfg = await _db.CafeSettings.FindAsync(1);
            if (cfg is not null)
            {
                remNombre = !string.IsNullOrWhiteSpace(cfg.NegocioRazonSocial)
                    ? cfg.NegocioRazonSocial!
                    : (cfg.NegocioNombre ?? "-");
                remCuit = cfg.NegocioCuit;
                remFantasia = cfg.NegocioNombre;
                remDireccion = cfg.NegocioDireccion;
                remTelefono = cfg.NegocioTelefono;
                remLocalidad = cfg.NegocioLocalidad;
                remCp = cfg.NegocioCp;
            }
        }

        return new RotuloData(
            // Transporte
            transporteNombre, transporteDireccion, transporteTelefono,
            transporteLocalidad, transporteProvincia, transporteCp, pagoDestino,
            // Destinatario
            destNombre, destDniCuit, destTelefono, destDireccion,
            destLocalidad, destProvincia, destCp,
            usuarioMl, notaProducto,
            // Remitente
            remNombre, remCuit, remFantasia, remDireccion,
            remTelefono, remLocalidad, remProvincia, remCp,
            // Pie
            venta.EsFragil, venta.CantidadBultos,
            LogoBytes: null
        );
    }
}
