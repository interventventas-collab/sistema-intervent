using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

/// <summary>
/// Modulo "Contadora": cuadro de ventas por jurisdiccion (Ingresos Brutos) a partir de MercadoLibre.
/// Ver ContadoraService para el detalle. Todo es de solo lectura salvo el backfill, que solo
/// completa la columna ProvinciaDestino de MeliOrders (no toca datos de MeLi).
/// </summary>
[ApiController]
[Route("api/contadora")]
[Authorize]
public class ContadoraController : ControllerBase
{
    private readonly ContadoraService _svc;
    private readonly ContadoraAutoBackfillService _robot;
    public ContadoraController(ContadoraService svc, ContadoraAutoBackfillService robot) { _svc = svc; _robot = robot; }

    /// <summary>Dispara el robot en el SERVIDOR (provincias + facturas) y contesta al instante.
    /// Corre en segundo plano; el usuario puede cerrar la pestaña. Vuelve a consultar el cuadro para ver el avance.</summary>
    [HttpPost("run-robot")]
    public IActionResult RunRobot()
    {
        _ = Task.Run(() => _robot.RunOnceManualAsync());
        return Ok(new { ok = true });
    }

    /// <summary>Cuadro de ventas por jurisdiccion para el rango [desde, hasta] (por fecha de venta).</summary>
    [HttpGet("jurisdiccion")]
    public async Task<ActionResult<ContadoraJurisdiccionDto>> Jurisdiccion([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta)
        => Ok(await _svc.GetVentasPorJurisdiccionAsync(desde, hasta));

    /// <summary>Trae de MeLi la provincia de un lote de ventas que todavia no la tienen.
    /// El front llama repetido hasta que Pendientes = 0.</summary>
    [HttpPost("backfill-provincias")]
    public async Task<ActionResult<ContadoraBackfillResultDto>> Backfill([FromQuery] int lote = 150)
        => Ok(await _svc.BackfillProvinciasAsync(lote));

    /// <summary>Descarga el cuadro en Excel con el formato de la contadora.</summary>
    [HttpGet("jurisdiccion/excel")]
    public async Task<IActionResult> JurisdiccionExcel([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta)
    {
        var bytes = await _svc.GenerarExcelAsync(desde, hasta);
        var nombre = $"ventas-por-jurisdiccion-{DateTime.Now:yyyy-MM-dd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nombre);
    }

    // ───────── Libro IVA Ventas (etapa 2) ─────────

    /// <summary>Empresas (CUIT emisor) disponibles para el filtro.</summary>
    [HttpGet("empresas")]
    public async Task<ActionResult<List<ContadoraEmpresaDto>>> Empresas() => Ok(await _svc.GetEmpresasAsync());

    /// <summary>Trae de MeLi las facturas de venta que faltan (por lote). El front llama hasta Pendientes=0.</summary>
    [HttpPost("backfill-facturas")]
    public async Task<ActionResult<ContadoraBackfillResultDto>> BackfillFacturas([FromQuery] int lote = 120)
        => Ok(await _svc.BackfillFacturasAsync(lote));

    /// <summary>Resumen del Libro IVA Ventas (por empresa + punto de venta + tipo) segun filtros.</summary>
    [HttpGet("libro-iva")]
    public async Task<ActionResult<ContadoraLibroIvaDto>> LibroIva([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? empresa, [FromQuery] int? puntoVenta, [FromQuery] string? letra, [FromQuery] string? provincia, [FromQuery] string? search)
        => Ok(await _svc.GetLibroIvaVentasAsync(desde, hasta, empresa, puntoVenta, letra, provincia, search));

    /// <summary>Detalle: lista de facturas (paginada) segun filtros.</summary>
    [HttpGet("facturas")]
    public async Task<ActionResult<ContadoraFacturasPageDto>> Facturas([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? empresa, [FromQuery] int? puntoVenta, [FromQuery] string? letra, [FromQuery] string? provincia,
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => Ok(await _svc.GetFacturasAsync(desde, hasta, empresa, puntoVenta, letra, provincia, search, page, pageSize));

    /// <summary>Descarga el Libro IVA Ventas en Excel (resumen + detalle) segun filtros.</summary>
    [HttpGet("libro-iva/excel")]
    public async Task<IActionResult> LibroIvaExcel([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? empresa, [FromQuery] int? puntoVenta, [FromQuery] string? letra, [FromQuery] string? provincia, [FromQuery] string? search)
    {
        var bytes = await _svc.GenerarLibroIvaExcelAsync(desde, hasta, empresa, puntoVenta, letra, provincia, search);
        var nombre = $"libro-iva-ventas-{DateTime.Now:yyyy-MM-dd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nombre);
    }

    // ───────── Importacion del reporte oficial de MeLi (etapa 3, con notas de credito) ─────────

    /// <summary>Importa uno o varios Excel de reporte de MeLi subidos por el usuario.</summary>
    [HttpPost("importar-reporte")]
    [RequestSizeLimit(80_000_000)]
    public async Task<ActionResult<ContadoraImportResultDto>> ImportarReporte([FromForm] List<IFormFile> archivos)
    {
        if (archivos == null || archivos.Count == 0)
            return Ok(new ContadoraImportResultDto { Ok = false, Mensaje = "No se recibio ningun archivo." });
        var items = new List<(string, Stream)>();
        foreach (var f in archivos) items.Add((f.FileName, f.OpenReadStream()));
        return Ok(await _svc.ImportarReporteArchivosAsync(items));
    }

    /// <summary>Importa todos los .xlsx que esten en una subcarpeta de la Carpeta Compartida
    /// (por defecto "Compartido/facturas meli"). Comodo: el usuario sube ahi y aprieta un boton.</summary>
    [HttpPost("importar-reporte-carpeta")]
    public async Task<ActionResult<ContadoraImportResultDto>> ImportarReporteCarpeta([FromQuery] string? subcarpeta)
        => Ok(await _svc.ImportarReporteCarpetaAsync(string.IsNullOrWhiteSpace(subcarpeta) ? "Compartido/facturas meli" : subcarpeta));

    /// <summary>Trae al Libro IVA las facturas que emite nuestro sistema por AFIP (Cafe_Ventas con CAE). NC restan.</summary>
    [HttpPost("sincronizar-sistema")]
    public async Task<ActionResult<ContadoraImportResultDto>> SincronizarSistema() => Ok(await _svc.SincronizarSistemaAsync());

    /// <summary>Empresas (CUIT) presentes en los comprobantes importados.</summary>
    [HttpGet("reporte/empresas")]
    public async Task<ActionResult<List<ContadoraEmpresaDto>>> ReporteEmpresas() => Ok(await _svc.GetReporteEmpresasAsync());

    /// <summary>Provincias presentes en los comprobantes importados (para el desplegable del filtro).</summary>
    [HttpGet("reporte/provincias")]
    public async Task<ActionResult<List<string>>> ReporteProvincias() => Ok(await _svc.GetReporteProvinciasAsync());

    /// <summary>Resumen del Libro IVA Ventas desde el reporte importado (NC restan). origen: MELI_REPORTE | SISTEMA | (vacio = todo).</summary>
    [HttpGet("reporte/resumen")]
    public async Task<ActionResult<ContadoraReporteResumenDto>> ReporteResumen([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? empresa, [FromQuery] int? puntoVenta, [FromQuery] string? letra, [FromQuery] string? provincia, [FromQuery] string? search, [FromQuery] string? origen)
        => Ok(await _svc.GetReporteResumenAsync(desde, hasta, empresa, puntoVenta, letra, provincia, search, origen));

    /// <summary>Meses ya cargados.</summary>
    [HttpGet("reporte/cargas")]
    public async Task<ActionResult<List<ContadoraCargaDto>>> ReporteCargas([FromQuery] string? empresa, [FromQuery] string? origen)
        => Ok(await _svc.GetReporteCargasAsync(empresa, origen));

    /// <summary>Detalle paginado de comprobantes importados.</summary>
    [HttpGet("reporte/comprobantes")]
    public async Task<ActionResult<ContadoraComprobantesPageDto>> ReporteComprobantes([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? empresa, [FromQuery] int? puntoVenta, [FromQuery] string? letra, [FromQuery] string? provincia,
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, [FromQuery] string? origen = null)
        => Ok(await _svc.GetReporteComprobantesAsync(desde, hasta, empresa, puntoVenta, letra, provincia, search, page, pageSize, origen));

    /// <summary>Descarga el Libro IVA Ventas (importado) en Excel.</summary>
    [HttpGet("reporte/excel")]
    public async Task<IActionResult> ReporteExcel([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? empresa, [FromQuery] int? puntoVenta, [FromQuery] string? letra, [FromQuery] string? provincia, [FromQuery] string? search, [FromQuery] string? origen)
    {
        var bytes = await _svc.GenerarReporteExcelAsync(desde, hasta, empresa, puntoVenta, letra, provincia, search, origen);
        var nombre = $"libro-iva-ventas-{DateTime.Now:yyyy-MM-dd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nombre);
    }

    // ───────── COMPRAS (Mis Comprobantes Recibidos de AFIP) + BALANZA ─────────

    /// <summary>Importa los "Mis Comprobantes Recibidos" de AFIP que esten en una subcarpeta compartida.</summary>
    [HttpPost("importar-compras-carpeta")]
    public async Task<ActionResult<ContadoraImportResultDto>> ImportarComprasCarpeta([FromQuery] string? subcarpeta)
        => Ok(await _svc.ImportarComprasCarpetaAsync(string.IsNullOrWhiteSpace(subcarpeta) ? "Compartido/facturas meli" : subcarpeta));

    /// <summary>Importa archivos de "Mis Comprobantes Recibidos" subidos por el usuario.</summary>
    [HttpPost("importar-compras")]
    [RequestSizeLimit(80_000_000)]
    public async Task<ActionResult<ContadoraImportResultDto>> ImportarCompras([FromForm] List<IFormFile> archivos)
    {
        if (archivos == null || archivos.Count == 0)
            return Ok(new ContadoraImportResultDto { Ok = false, Mensaje = "No se recibio ningun archivo." });
        var items = new List<(string, Stream)>();
        foreach (var f in archivos) items.Add((f.FileName, f.OpenReadStream()));
        return Ok(await _svc.ImportarComprasArchivosAsync(items));
    }

    /// <summary>Resumen del Libro IVA Compras (NC restan).</summary>
    [HttpGet("compras/resumen")]
    public async Task<ActionResult<ContadoraReporteResumenDto>> ComprasResumen([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? empresa, [FromQuery] string? search)
        => Ok(await _svc.GetReporteResumenAsync(desde, hasta, empresa, null, null, null, search, null, "COMPRA"));

    /// <summary>Detalle paginado de compras.</summary>
    [HttpGet("compras/comprobantes")]
    public async Task<ActionResult<ContadoraComprobantesPageDto>> ComprasComprobantes([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? empresa, [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => Ok(await _svc.GetReporteComprobantesAsync(desde, hasta, empresa, null, null, null, search, page, pageSize, null, "COMPRA"));

    /// <summary>Descarga el Libro IVA Compras en Excel.</summary>
    [HttpGet("compras/excel")]
    public async Task<IActionResult> ComprasExcel([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta, [FromQuery] string? empresa, [FromQuery] string? search)
    {
        var bytes = await _svc.GenerarReporteExcelAsync(desde, hasta, empresa, null, null, null, search, null, "COMPRA");
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"libro-iva-compras-{DateTime.Now:yyyy-MM-dd}.xlsx");
    }

    /// <summary>Balanza de IVA: por mes, IVA de ventas - IVA de compras = saldo.</summary>
    [HttpGet("balanza")]
    public async Task<ActionResult<ContadoraBalanzaDto>> Balanza([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta, [FromQuery] string? empresa)
        => Ok(await _svc.GetBalanzaAsync(desde, hasta, empresa));

    /// <summary>Importa los "Mis Comprobantes Emitidos" de AFIP (ventas) de una subcarpeta compartida.</summary>
    [HttpPost("importar-ventas-afip-carpeta")]
    public async Task<ActionResult<ContadoraImportResultDto>> ImportarVentasAfipCarpeta([FromQuery] string? subcarpeta)
        => Ok(await _svc.ImportarVentasAfipCarpetaAsync(string.IsNullOrWhiteSpace(subcarpeta) ? "Compartido/facturas meli" : subcarpeta));

    /// <summary>Importa archivos de "Mis Comprobantes Emitidos" (ventas AFIP) subidos por el usuario.</summary>
    [HttpPost("importar-ventas-afip")]
    [RequestSizeLimit(80_000_000)]
    public async Task<ActionResult<ContadoraImportResultDto>> ImportarVentasAfip([FromForm] List<IFormFile> archivos)
    {
        if (archivos == null || archivos.Count == 0)
            return Ok(new ContadoraImportResultDto { Ok = false, Mensaje = "No se recibio ningun archivo." });
        var items = new List<(string, Stream)>();
        foreach (var f in archivos) items.Add((f.FileName, f.OpenReadStream()));
        return Ok(await _svc.ImportarVentasAfipArchivosAsync(items));
    }

    /// <summary>Control / doble-check: concilia ventas de AFIP contra MeLi/sistema.</summary>
    [HttpGet("control")]
    public async Task<ActionResult<ContadoraControlDto>> Control([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta, [FromQuery] string? empresa)
        => Ok(await _svc.GetControlAsync(desde, hasta, empresa));

    /// <summary>Vuelca al Libro IVA las facturas de MeLi ya bajadas por la API (ventas automáticas).</summary>
    [HttpPost("sincronizar-meli-api")]
    public async Task<ActionResult<ContadoraImportResultDto>> SincronizarMeliApi()
        => Ok(await _svc.SincronizarMeliApiAsync());

    /// <summary>Importa el CSV que el scraper de AFIP bajó en la última corrida (compras + ventas).</summary>
    [HttpPost("importar-scrape-afip")]
    public async Task<ActionResult<ContadoraImportResultDto>> ImportarScrapeAfip()
        => Ok(await _svc.ImportarUltimoScrapeAfipAsync());

    /// <summary>Procesa los PDF de facturas de una carpeta: lee el QR y los adjunta a la venta/compra que corresponde.</summary>
    [HttpPost("procesar-facturas-pdf")]
    public async Task<ActionResult<ContadoraPdfResultDto>> ProcesarFacturasPdf([FromQuery] string? subcarpeta)
        => Ok(await _svc.ProcesarFacturasPdfAsync(string.IsNullOrWhiteSpace(subcarpeta) ? "Compartido/facturas recibidas" : subcarpeta));

    /// <summary>Descarga el PDF adjunto de un comprobante (venta o compra).</summary>
    [HttpGet("factura-pdf")]
    public async Task<IActionResult> FacturaPdf([FromQuery] string id)
    {
        var (bytes, nombre) = await _svc.GetFacturaPdfAsync(id);
        if (bytes is null) return NotFound();
        return File(bytes, "application/pdf", nombre ?? "factura.pdf");
    }

    /// <summary>Config de la casilla de correo de facturas (sin la clave).</summary>
    [HttpGet("config-correo")]
    public async Task<IActionResult> GetConfigCorreo() => Ok(await _svc.GetConfigCorreoAsync());

    /// <summary>Guarda la config del correo (host/usuario/clave/carpeta). Si la clave viene vacía, se conserva.</summary>
    [HttpPost("config-correo")]
    public async Task<IActionResult> GuardarConfigCorreo([FromBody] ConfigCorreoRequest req)
    {
        await _svc.GuardarConfigCorreoAsync(req.Host, req.Port, req.Usuario, req.Password, req.Carpeta, req.Activo);
        return Ok(new { ok = true });
    }

    /// <summary>Revisa la casilla ahora: baja los PDF adjuntos nuevos y los matchea.</summary>
    [HttpPost("revisar-correo")]
    public async Task<ActionResult<ContadoraPdfResultDto>> RevisarCorreo() => Ok(await _svc.RevisarCorreoAsync());

    /// <summary>Baja de MercadoLibre las facturas de compra (PDF real con QR) de las últimas compras y las matchea.</summary>
    [HttpPost("bajar-facturas-meli")]
    public async Task<ActionResult<ContadoraPdfResultDto>> BajarFacturasMeli() => Ok(await _svc.BajarFacturasMeliAsync());

    /// <summary>Retenciones/percepciones de IVA cargadas por mes para una empresa.</summary>
    [HttpGet("retenciones")]
    public async Task<ActionResult<List<ContadoraRetencionDto>>> Retenciones([FromQuery] string empresa)
        => Ok(await _svc.GetRetencionesAsync(empresa));

    /// <summary>Carga/actualiza el total de retenciones de IVA de un mes.</summary>
    [HttpPost("retenciones")]
    public async Task<IActionResult> GuardarRetencion([FromBody] GuardarRetencionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Empresa)) return BadRequest(new { error = "Falta la empresa." });
        await _svc.GuardarRetencionAsync(req.Empresa, req.Anio, req.Mes, req.Monto, req.Nota);
        return Ok(new { ok = true });
    }
}

public class GuardarRetencionRequest
{
    public string Empresa { get; set; } = "";
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal Monto { get; set; }
    public string? Nota { get; set; }
}

public class ConfigCorreoRequest
{
    public string Host { get; set; } = "imap.gmail.com";
    public int Port { get; set; } = 993;
    public string Usuario { get; set; } = "";
    public string? Password { get; set; }
    public string? Carpeta { get; set; }
    public bool Activo { get; set; } = true;
}
