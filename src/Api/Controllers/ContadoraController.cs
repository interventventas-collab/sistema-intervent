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
}
