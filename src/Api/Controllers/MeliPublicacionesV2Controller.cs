using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-08-25 — Endpoints de la pantalla NUEVA de publicaciones (/publicaciones-nueva).
/// Controller aparte para no tocar MeliController (3.775 líneas) mientras la pantalla nueva
/// está en prueba. Solo LEE: no modifica nada en MeLi ni en el sistema.
/// </summary>
[ApiController]
[Route("api/meli/v2")]
[Authorize]
public class MeliPublicacionesV2Controller : ControllerBase
{
    private readonly MeliPublicacionesV2Service _svc;

    public MeliPublicacionesV2Controller(MeliPublicacionesV2Service svc) => _svc = svc;

    /// <summary>Lista paginada con receta, stock armable, comisión real y familia.</summary>
    [HttpGet("publicaciones")]
    public async Task<IActionResult> GetPublicaciones(
        [FromQuery] string? texto = null,
        [FromQuery] string? sku = null,
        [FromQuery] string? estado = null,
        [FromQuery] int? cuentaId = null,
        [FromQuery] decimal? comisionMinPct = null,
        [FromQuery] string? cuotas = null,
        [FromQuery] string? tipo = null,
        [FromQuery] bool variosPrecios = false,
        [FromQuery] bool precioAMano = false,
        [FromQuery] bool sinCosto = false,
        [FromQuery] decimal? noLleganAlPct = null,
        [FromQuery] bool comisionVieja = false,
        [FromQuery] bool enPromo = false,
        [FromQuery] int pagina = 1,
        [FromQuery] int porPagina = 100,
        [FromQuery] string? ampliar = null,
        [FromQuery] string? orden = null,
        [FromQuery] decimal? precioDesde = null,
        [FromQuery] decimal? precioHasta = null,
        [FromQuery] bool? envioGratis = null,
        [FromQuery] string? logistica = null,
        [FromQuery] bool bajoMiPct = false,
        [FromQuery] bool conCambios = false)
    {
        var f = new MeliPublicacionesV2Service.Filtros(
            texto, sku, estado, cuentaId, comisionMinPct, cuotas, tipo,
            variosPrecios, precioAMano, precioAMano, sinCosto, noLleganAlPct, comisionVieja, pagina, porPagina, enPromo, ampliar,
            orden, precioDesde, precioHasta, envioGratis, logistica, bajoMiPct, conCambios);
        var res = await _svc.GetAsync(f, HttpContext.RequestAborted);
        return Ok(res);
    }

    /// <summary>2026-10-01 — "✓ visto": marca como vistos los cambios de MeLi (envío) de esa
    /// publicación. Quién y cuándo queda en el registro de cambios.</summary>
    [HttpPost("publicaciones/{mla}/cambios-vistos")]
    public async Task<IActionResult> MarcarCambiosVistos(string mla, [FromServices] Api.Data.AppDbContext db,
        [FromServices] Api.Services.AuditLogService audit)
    {
        var tipos = MeliPublicacionesV2Service.TiposCambioVisibles;
        var filas = await db.MeliCambiosDetectados
            .Where(c => c.MeliItemId == mla && c.SeenAt == null && tipos.Contains(c.Tipo)).ToListAsync();
        if (filas.Count == 0) return Ok(new { marcados = 0 });
        var ahora = DateTime.UtcNow;
        foreach (var c in filas) c.SeenAt = ahora;
        await db.SaveChangesAsync();
        await audit.LogAsync("MeliItem", mla, "CAMBIOS_VISTOS",
            $"{filas.Count} cambio(s) de MeLi marcados como vistos: " + string.Join(" · ", filas.Select(c => c.Notes ?? c.Tipo)),
            // El operador elegido en pantalla; si no hay, el usuario que entró.
            string.IsNullOrWhiteSpace(Request.Headers["X-Operator-Name"].ToString()) ? User?.Identity?.Name : null);
        return Ok(new { marcados = filas.Count });
    }

    // ─── 2026-08-26 · ETAPA 2: acciones sobre las publicaciones TILDADAS ───
    // Todas reciben la lista de MLAs que el usuario eligió en pantalla. Nada actúa sobre
    // "todo el catálogo": ese era justamente el problema de la pantalla vieja.

    public record LoteRequest(List<string> Mlas);
    public record SincroRequest(List<string> Mlas, bool? Precio, bool? Stock);
    public record ObjetivoRequest(List<string> Mlas, decimal ObjetivoPct, bool AplicarAhora);

    /// <summary>SEGURA: le pregunta a MeLi la comisión de cada una. No cambia nada.</summary>
    [HttpPost("acciones/comisiones")]
    public async Task<IActionResult> AccionComisiones([FromBody] LoteRequest req, [FromServices] MeliAccionesLoteService svc)
        => Ok(await svc.ActualizarComisionesAsync(req.Mlas ?? new(), HttpContext.RequestAborted));

    /// <summary>SEGURA: prende o apaga el sincro de precio y/o stock. Sólo configuración.</summary>
    [HttpPost("acciones/sincro")]
    public async Task<IActionResult> AccionSincro([FromBody] SincroRequest req, [FromServices] MeliAccionesLoteService svc)
        => Ok(await svc.CambiarSincroAsync(req.Mlas ?? new(), req.Precio, req.Stock, HttpContext.RequestAborted));

    /// <summary>Guarda el objetivo de ganancia; con AplicarAhora además pushea el precio.</summary>
    [HttpPost("acciones/objetivo")]
    public async Task<IActionResult> AccionObjetivo([FromBody] ObjetivoRequest req, [FromServices] MeliAccionesLoteService svc)
        => Ok(await svc.PonerObjetivoAsync(req.Mlas ?? new(), req.ObjetivoPct, req.AplicarAhora, HttpContext.RequestAborted));

    /// <summary>TOCA MELI: pushea el precio que el sistema calcula hoy.</summary>
    [HttpPost("acciones/precio")]
    public async Task<IActionResult> AccionPrecio([FromBody] LoteRequest req, [FromServices] MeliAccionesLoteService svc)
        => Ok(await svc.PushearPrecioAsync(req.Mlas ?? new(), HttpContext.RequestAborted));

    /// <summary>TOCA MELI: manda el stock del sistema, con todas las reglas del motor de stock.</summary>
    [HttpPost("acciones/stock")]
    public async Task<IActionResult> AccionStock([FromBody] LoteRequest req, [FromServices] MeliAccionesLoteService svc)
        => Ok(await svc.PushearStockAsync(req.Mlas ?? new(), HttpContext.RequestAborted));

    // ─── 2026-08-26: peso y medidas del paquete ───
    // El envío que MeLi cobra sale de acá. Si están mal cargadas, el envío se dispara y no hay
    // precio que lo arregle. Leer es en vivo contra MeLi; guardar además recalcula el envío
    // para ver al instante si sirvió.

    /// <summary>Peso (g) y medidas (cm) del paquete, leídas en vivo de MeLi.</summary>
    [HttpGet("publicaciones/{mla}/medidas")]
    public async Task<IActionResult> GetMedidas(string mla, [FromServices] MeliMedidasService svc)
    {
        var r = await svc.LeerAsync(mla, HttpContext.RequestAborted);
        if (r is null) return NotFound(new { error = "Publicación no encontrada o sin cuenta MeLi" });
        return Ok(r);
    }

    /// <summary>Guarda peso y/o medidas en MeLi y devuelve cuánto cambió el envío.</summary>
    [HttpPut("publicaciones/{mla}/medidas")]
    public async Task<IActionResult> PutMedidas(string mla,
        [FromBody] MeliMedidasService.GuardarRequest req,
        [FromServices] MeliMedidasService svc)
    {
        var r = await svc.GuardarAsync(mla, req, HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    // ─── 2026-08-26 · ETAPA 3: fotos ───
    // Osmar: "si se pincha la foto que se desplieguen todas, así las veo y las puedo modificar".
    // Lo importante es poder COPIARLAS a las hermanas: el mismo producto está publicado varias
    // veces y hoy hay que dejar linda cada una a mano.

    /// <summary>Fotos en vivo de MeLi + las publicaciones hermanas con cuántas fotos tiene cada una.</summary>
    [HttpGet("publicaciones/{mla}/fotos")]
    public async Task<IActionResult> GetFotos(string mla, [FromServices] MeliFotosService svc)
    {
        var r = await svc.LeerAsync(mla, HttpContext.RequestAborted);
        if (r is null) return NotFound(new { error = "Publicación no encontrada o sin cuenta MeLi" });
        return Ok(r);
    }

    /// <summary>TOCA MELI: guarda la lista final ordenada. La primera foto queda de portada y
    /// las que no vengan en la lista se borran.</summary>
    [HttpPut("publicaciones/{mla}/fotos")]
    public async Task<IActionResult> PutFotos(string mla,
        [FromBody] MeliFotosService.GuardarRequest req,
        [FromServices] MeliFotosService svc)
    {
        var r = await svc.GuardarAsync(mla, req.Fotos ?? new(), HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    /// <summary>TOCA MELI: copia estas fotos a las publicaciones hermanas elegidas.</summary>
    [HttpPost("publicaciones/{mla}/fotos/copiar")]
    public async Task<IActionResult> CopiarFotos(string mla,
        [FromBody] MeliFotosService.CopiarRequest req,
        [FromServices] MeliFotosService svc)
    {
        var r = await svc.CopiarAsync(mla, req.Destinos ?? new(), HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    // ─── 2026-09-21 · descripción ───
    // Osmar: "¿tengo manera de ver la descripción escrita acá?". Verla y cambiarla en la fila.

    /// <summary>Descripción en vivo de MeLi.</summary>
    [HttpGet("publicaciones/{mla}/descripcion")]
    public async Task<IActionResult> GetDescripcion(string mla, [FromServices] MeliDescripcionService svc)
    {
        var r = await svc.LeerAsync(mla, HttpContext.RequestAborted);
        if (r is null) return NotFound(new { error = "Publicación no encontrada o sin cuenta MeLi" });
        return Ok(r);
    }

    /// <summary>TOCA MELI: reemplaza la descripción entera.</summary>
    [HttpPut("publicaciones/{mla}/descripcion")]
    public async Task<IActionResult> PutDescripcion(string mla,
        [FromBody] MeliDescripcionService.GuardarRequest req,
        [FromServices] MeliDescripcionService svc)
    {
        var r = await svc.GuardarAsync(mla, req.Texto, HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    // ─── 2026-08-26 · precio a mano ───
    // Osmar: "poder meter el precio manual y que ahí aparezca el porcentaje basado en el precio".
    // Simular NO cambia nada; publicar sí, y define quién manda de ahí en más (el precio o el %).

    /// <summary>Qué dejaría esta publicación si valiera otro precio. No toca nada.</summary>
    [HttpGet("publicaciones/{mla}/simular")]
    public async Task<IActionResult> Simular(string mla, [FromQuery] decimal precio,
        [FromServices] MeliPrecioManualService svc)
    {
        var r = await svc.SimularAsync(mla, precio, HttpContext.RequestAborted);
        if (r is null) return NotFound(new { error = "Publicación no encontrada" });
        return Ok(r);
    }

    /// <summary>TOCA MELI: publica un precio escrito a mano.</summary>
    [HttpPut("publicaciones/{mla}/precio")]
    public async Task<IActionResult> PonerPrecio(string mla,
        [FromBody] MeliPrecioManualService.PublicarRequest req,
        [FromServices] MeliPrecioManualService svc)
    {
        var r = await svc.PublicarAsync(mla, req.Precio, req.QuedaFijo, HttpContext.RequestAborted, req.ObjetivoPct);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    // ─── 2026-09-25 · BOTÓN CUOTAS DE LA FILA ───
    // Osmar cambió las cuotas desde MeLi y la pantalla siguió mostrando las viejas. Ver relee MeLi
    // en el momento; cambiar toca MeLi (tipo + cuotas) pero NO el precio.

    /// <summary>Relee de MeLi tipo y cuotas, y dice cuánto te quedaría con cada opción.</summary>
    [HttpGet("publicaciones/{mla}/cuotas")]
    public async Task<IActionResult> VerCuotas(string mla, [FromQuery] decimal? precio,
        [FromServices] MeliCuotasService svc)
    {
        var r = await svc.VerAsync(mla, precio, HttpContext.RequestAborted);
        return r is null ? NotFound(new { error = "Publicación no encontrada" }) : Ok(r);
    }

    /// <summary>TOCA MELI: pasa la publicación a Clásica o a Premium con 3/6/9/12 cuotas.</summary>
    [HttpPut("publicaciones/{mla}/cuotas")]
    public async Task<IActionResult> CambiarCuotas(string mla, [FromBody] MeliCuotasService.CambiarRequest req,
        [FromServices] MeliCuotasService svc)
    {
        var r = await svc.CambiarAsync(mla, req.Opcion, req.Precio, HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    // ─── 2026-08-27 · PAUSAR Y ACTIVAR DESDE LA FILA ───
    // El cartelito "Activa"/"Pausada" de la derecha ahora se toca. Es de a UNA a propósito: nunca
    // en lote. Eliminar (2026-09-28, pedido de Osmar) sí va de a una o varias, pero con la palabra
    // ELIMINAR+cantidad: es irreversible y se pierde la antigüedad, el historial y las preguntas.

    /// <summary>TOCA MELI: deja de venderse. Se puede volver atrás.</summary>
    [HttpPost("publicaciones/{mla}/pausar")]
    public async Task<IActionResult> Pausar(string mla, [FromServices] MeliEstadoService svc)
    {
        var r = await svc.PausarAsync(mla, HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    /// <summary>TOCA MELI: vuelve a venderse, con el stock real y el precio del objetivo aplicados.</summary>
    [HttpPost("publicaciones/{mla}/activar")]
    public async Task<IActionResult> Activar(string mla, [FromServices] MeliEstadoService svc)
    {
        var r = await svc.ActivarAsync(mla, HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    /// <summary>TOCA MELI: la saca de la venta (finalizada). Se vuelve a publicar sólo desde MeLi.</summary>
    [HttpPost("publicaciones/{mla}/finalizar")]
    public async Task<IActionResult> Finalizar(string mla, [FromServices] MeliEstadoService svc)
    {
        var r = await svc.FinalizarAsync(mla, HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    public record EliminarRequest(List<string> Mlas, string? Confirmacion);
    public record EliminarFila(string Mla, bool Ok, string Mensaje, string? EstadoNuevo);

    /// <summary>TOCA MELI y NO SE DESHACE: finaliza y borra las publicaciones pedidas (una o varias).
    /// 2026-09-28 · Pide la palabra "ELIMINAR" + la cantidad (ELIMINAR1, ELIMINAR3...) y se chequea
    /// ACÁ: si la pantalla se equivoca con la cantidad, no se borra nada.</summary>
    [HttpPost("acciones/eliminar")]
    public async Task<IActionResult> Eliminar([FromBody] EliminarRequest req, [FromServices] MeliEstadoService svc)
    {
        var mlas = (req.Mlas ?? new()).Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (mlas.Count == 0) return BadRequest(new { error = "No hay publicaciones para eliminar." });

        var esperada = $"ELIMINAR{mlas.Count}";
        var escrita = (req.Confirmacion ?? "").Replace(" ", "").Trim().ToUpperInvariant();
        if (escrita != esperada)
            return BadRequest(new { error = $"Para eliminar {mlas.Count} hay que escribir {esperada}." });

        var filas = new List<EliminarFila>();
        foreach (var mla in mlas)
        {
            try
            {
                var r = await svc.EliminarAsync(mla, HttpContext.RequestAborted);
                filas.Add(new EliminarFila(mla, r.Ok, r.Mensaje, r.EstadoNuevo));
            }
            catch (Exception ex) { filas.Add(new EliminarFila(mla, false, ex.Message, null)); }
        }
        return Ok(new { ok = filas.Count(f => f.Ok), errores = filas.Count(f => !f.Ok), filas });
    }

    public record ArmarPackRequest(string? DataUri, int Cantidad);

    /// <summary>SEGURO (no toca MeLi): arma la foto de un pack repitiendo la de una unidad. Devuelve la
    /// imagen; la pantalla la agrega como foto nueva y se sube con el "Guardar" de las fotos.</summary>
    [HttpPost("fotos/armar-pack")]
    [RequestSizeLimit(20_000_000)]
    public IActionResult ArmarFotoPack([FromBody] ArmarPackRequest req)
    {
        var du = req.DataUri ?? "";
        var coma = du.IndexOf(',');
        if (!du.StartsWith("data:image", StringComparison.OrdinalIgnoreCase) || coma < 0)
            return BadRequest(new { error = "No llegó la foto." });
        byte[] bytes;
        try { bytes = Convert.FromBase64String(du[(coma + 1)..]); }
        catch { return BadRequest(new { error = "La foto llegó rota." }); }

        var r = FotoPackService.Armar(bytes, req.Cantidad);
        if (r.Jpeg is null) return BadRequest(new { error = r.Error ?? "No se pudo armar la foto." });
        return Ok(new { dataUri = "data:image/jpeg;base64," + Convert.ToBase64String(r.Jpeg), aviso = r.Aviso });
    }

    /// <summary>TOCA MELI pero SÓLO el SKU: le devuelve el que tenía antes de marcarla para
    /// revisar. NO la activa — eso lo decide el usuario aparte.</summary>
    [HttpPost("publicaciones/{mla}/devolver-sku")]
    public async Task<IActionResult> DevolverSku(string mla, [FromServices] MeliEstadoService svc)
    {
        var r = await svc.DevolverSkuAsync(mla, HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    // ─── 2026-08-27 · EXCEL EDITABLE ───
    // Es lo único que escala a 5.925 publicaciones: la pantalla sirve para trabajar de a pocas.
    // El recorrido tiene tres pasos y el del medio NO se saltea: bajar → subir (vista previa) →
    // aplicar sólo lo que quedó tildado. Ver MeliPublicacionesExcelService para el detalle.

    /// <summary>SEGURO: arma el .xlsx con lo que quedó filtrado en pantalla. No cambia nada.</summary>
    [HttpGet("excel")]
    public async Task<IActionResult> BajarExcel(
        [FromQuery] string? texto = null,
        [FromQuery] string? sku = null,
        [FromQuery] string? estado = null,
        [FromQuery] int? cuentaId = null,
        [FromQuery] decimal? comisionMinPct = null,
        [FromQuery] string? cuotas = null,
        [FromQuery] string? tipo = null,
        [FromQuery] bool variosPrecios = false,
        [FromQuery] bool precioAMano = false,
        [FromQuery] bool sinCosto = false,
        [FromQuery] decimal? noLleganAlPct = null,
        [FromQuery] bool comisionVieja = false,
        [FromQuery] string? ampliar = null,
        [FromQuery] string? orden = null,
        [FromQuery] decimal? precioDesde = null,
        [FromQuery] decimal? precioHasta = null,
        [FromQuery] bool? envioGratis = null,
        [FromQuery] string? logistica = null,
        [FromQuery] bool bajoMiPct = false,
        [FromServices] MeliPublicacionesExcelService svc = null!)
    {
        var f = new MeliPublicacionesV2Service.Filtros(
            texto, sku, estado, cuentaId, comisionMinPct, cuotas, tipo,
            variosPrecios, precioAMano, precioAMano, sinCosto, noLleganAlPct, comisionVieja, 1, 500, Ampliar: ampliar,
            Orden: orden, PrecioDesde: precioDesde, PrecioHasta: precioHasta, EnvioGratis: envioGratis, Logistica: logistica, BajoMiPct: bajoMiPct);
        var (bytes, filas) = await svc.ExportarAsync(f, HttpContext.RequestAborted);
        if (filas == 0) return BadRequest(new { error = "No hay publicaciones para bajar con esos filtros." });
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
    }

    /// <summary>SEGURO: lee el Excel editado y devuelve la vista previa. NO cambia nada.</summary>
    [HttpPost("excel/vista-previa")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> VistaPreviaExcel(IFormFile archivo,
        [FromServices] MeliPublicacionesExcelService svc)
    {
        if (archivo is null || archivo.Length == 0)
            return BadRequest(new { error = "No llegó ningún archivo." });
        await using var stream = archivo.OpenReadStream();
        var r = await svc.LeerAsync(stream, HttpContext.RequestAborted);
        return Ok(r);
    }

    /// <summary>TOCA MELI en las filas que cambian el precio; el resto sólo guarda configuración.</summary>
    [HttpPost("excel/aplicar")]
    public async Task<IActionResult> AplicarExcel(
        [FromBody] MeliPublicacionesExcelService.AplicarRequest req,
        [FromServices] MeliPublicacionesExcelService svc)
    {
        var items = req?.Items ?? new();
        if (items.Count == 0) return BadRequest(new { error = "No hay cambios para aplicar." });
        var r = await svc.AplicarAsync(items, HttpContext.RequestAborted);
        return Ok(r);
    }

    // ─── 2026-08-31 · PROMOCIONES ───

    /// <summary>SEGURO: le pregunta a MercadoLibre qué publicaciones están vendiendo con descuento
    /// y a qué precio, y lo guarda. NO aplica ni saca promociones — sólo mira.
    /// Sale barato: ~15 llamadas para todo el catálogo (una por campaña).</summary>
    [HttpPost("promociones/refrescar")]
    public async Task<IActionResult> RefrescarPromociones([FromServices] MeliPromocionesService svc)
        => Ok(await svc.RefrescarAsync(HttpContext.RequestAborted));

    /// <summary>SEGURO: qué campañas tiene disponibles esta publicación y, en cada una, QUÉ TE QUEDA.
    /// Junta lo que sabe MeLi (hasta dónde podés bajar) con lo que sabe el sistema (tu costo).</summary>
    [HttpGet("publicaciones/{mla}/promociones")]
    public async Task<IActionResult> PromocionesDeItem(string mla, [FromServices] MeliPromocionesService svc)
    {
        var r = await svc.LeerDeItemAsync(mla, HttpContext.RequestAborted);
        if (r is null) return NotFound(new { error = "Publicación no encontrada o sin cuenta MeLi" });
        return Ok(r);
    }

    public record PromoAccionRequest(string? PromocionId, string Tipo, decimal Precio);

    /// <summary>TOCA MELI: mete la publicación en una campaña al precio indicado. De a UNA.</summary>
    [HttpPost("publicaciones/{mla}/promociones/aplicar")]
    public async Task<IActionResult> AplicarPromocion(string mla,
        [FromBody] PromoAccionRequest req, [FromServices] MeliPromocionesService svc)
    {
        var r = await svc.AplicarAsync(mla, req.PromocionId, req.Tipo, req.Precio, HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    /// <summary>TOCA MELI: saca la publicación de una campaña. Vuelve al precio de lista.</summary>
    [HttpPost("publicaciones/{mla}/promociones/sacar")]
    public async Task<IActionResult> SacarPromocion(string mla,
        [FromBody] PromoAccionRequest req, [FromServices] MeliPromocionesService svc)
    {
        var r = await svc.SacarAsync(mla, req.PromocionId, req.Tipo, HttpContext.RequestAborted);
        return r.Ok ? Ok(r) : BadRequest(r);
    }

    /// <summary>Contadores para los chips de filtro.</summary>
    [HttpGet("resumen")]
    public async Task<IActionResult> GetResumen()
        => Ok(await _svc.GetResumenAsync(HttpContext.RequestAborted));
}
