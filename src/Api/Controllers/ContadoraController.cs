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
    public ContadoraController(ContadoraService svc) { _svc = svc; }

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
}
