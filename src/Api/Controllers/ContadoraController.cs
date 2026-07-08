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

    /// <summary>Empresas (CUIT) presentes en los comprobantes importados.</summary>
    [HttpGet("reporte/empresas")]
    public async Task<ActionResult<List<ContadoraEmpresaDto>>> ReporteEmpresas() => Ok(await _svc.GetReporteEmpresasAsync());

    /// <summary>Resumen del Libro IVA Ventas desde el reporte importado (NC restan).</summary>
    [HttpGet("reporte/resumen")]
    public async Task<ActionResult<ContadoraReporteResumenDto>> ReporteResumen([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? empresa, [FromQuery] int? puntoVenta, [FromQuery] string? letra, [FromQuery] string? provincia, [FromQuery] string? search)
        => Ok(await _svc.GetReporteResumenAsync(desde, hasta, empresa, puntoVenta, letra, provincia, search));

    /// <summary>Meses ya cargados.</summary>
    [HttpGet("reporte/cargas")]
    public async Task<ActionResult<List<ContadoraCargaDto>>> ReporteCargas([FromQuery] string? empresa)
        => Ok(await _svc.GetReporteCargasAsync(empresa));

    /// <summary>Detalle paginado de comprobantes importados.</summary>
    [HttpGet("reporte/comprobantes")]
    public async Task<ActionResult<ContadoraComprobantesPageDto>> ReporteComprobantes([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? empresa, [FromQuery] int? puntoVenta, [FromQuery] string? letra, [FromQuery] string? provincia,
        [FromQuery] string? search, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
        => Ok(await _svc.GetReporteComprobantesAsync(desde, hasta, empresa, puntoVenta, letra, provincia, search, page, pageSize));

    /// <summary>Descarga el Libro IVA Ventas (importado) en Excel.</summary>
    [HttpGet("reporte/excel")]
    public async Task<IActionResult> ReporteExcel([FromQuery] DateTime? desde, [FromQuery] DateTime? hasta,
        [FromQuery] string? empresa, [FromQuery] int? puntoVenta, [FromQuery] string? letra, [FromQuery] string? provincia, [FromQuery] string? search)
    {
        var bytes = await _svc.GenerarReporteExcelAsync(desde, hasta, empresa, puntoVenta, letra, provincia, search);
        var nombre = $"libro-iva-ventas-meli-{DateTime.Now:yyyy-MM-dd}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nombre);
    }
}
