using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-26 — Precio a mano en la pantalla nueva: probar un precio y ver qué deja, y publicarlo.
///
/// Idea de Osmar: *"poder meter el precio manual y que ahí aparezca el porcentaje basado en el precio"*.
/// Antes de eso había que cargar un objetivo de ganancia y dejar que el sistema calculara — no había
/// forma de decir "quiero que valga tanto".
///
/// Dos cosas hacen que esto no sea una cuenta trivial:
///
/// 1. **El escalón del envío.** Arriba de cierto precio MeLi obliga a envío gratis y ese costo pasa
///    a ser del vendedor. Medido en la bañera M302AZ: a $32.999 deja 71,9%, a $33.500 deja 19,8%,
///    porque aparecen $11.080 de envío. Una fórmula local diría que subir conviene. Por eso el
///    número se le pide a MeLi a cada precio que se prueba.
///
/// 2. **Los dos modos se pisan.** Si la publicación está en modo PORCENTAJE (objetivo cargado +
///    sincro de precio prendido), el sistema le va a recalcular el precio en la próxima pasada y el
///    precio puesto a mano desaparece. Por eso al publicar se pregunta si ese precio queda FIJO:
///    si dice que sí, se apaga el sincro y manda el precio; si no, sigue mandando el porcentaje.
/// </summary>
public class MeliPrecioManualService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly MeliItemService _itemService;
    private readonly MeliPricePushService _pricePush;
    private readonly ILogger<MeliPrecioManualService> _logger;

    private const decimal IVA = 1.21m;
    private const decimal TOPE_SEGURO = 2_000_000m;   // mismo candado que el motor de precios

    /// <summary>El precio donde MeLi cambia de régimen: deja de cobrar el cargo fijo y empieza a
    /// empujar el envío gratis. Medido el 26/08 sobre las 3.790 activas: la publicación más barata
    /// SIN cargo fijo está exactamente en $33.000, y no hay ninguna por debajo.</summary>
    private const decimal ESCALON_ENVIO = 33_000m;

    public MeliPrecioManualService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService accountService, MeliItemService itemService,
        MeliPricePushService pricePush, ILogger<MeliPrecioManualService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _itemService = itemService;
        _pricePush = pricePush;
        _logger = logger;
    }

    public record SimulacionDto(
        string MeliItemId,
        decimal PrecioActual, decimal PrecioProbado,
        decimal Comision, decimal Envio, decimal? Costo,
        decimal? Ganancia, decimal? MargenPct,
        decimal? MargenActualPct,
        bool CruzaEscalonEnvio,
        decimal? MargenSiPagasElEnvioPct, decimal? GananciaSiPagasElEnvio,
        bool PagasElEnvioHoy,
        string? Aviso);

    public record PublicarRequest(decimal Precio, bool QuedaFijo);
    public record PublicarResultado(bool Ok, string Mensaje, decimal? PrecioNuevo, bool ModoPrecio);

    /// <summary>Qué dejaría esta publicación si valiera `precio`. No cambia nada.</summary>
    public async Task<SimulacionDto?> SimularAsync(string meliItemId, decimal precio, CancellationToken ct = default)
    {
        var item = await _db.MeliItems.AsNoTracking()
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId && i.VariationId == null, ct);
        if (item is null) return null;

        if (precio <= 0)
            return new SimulacionDto(meliItemId, item.Price, precio, 0, 0, null, null, null, null,
                false, null, null, item.FreeShipping, "Poné un precio mayor que cero.");
        if (precio > TOPE_SEGURO)
            return new SimulacionDto(meliItemId, item.Price, precio, 0, 0, null, null, null, null,
                false, null, null, item.FreeShipping,
                $"${precio:N0} está fuera de rango — el tope de seguridad es ${TOPE_SEGURO:N0}.");

        var costos = await _itemService.SimularCostosAsync(meliItemId, precio, ct);
        if (costos is null)
            return new SimulacionDto(meliItemId, item.Price, precio, 0, 0, null, null, null, null,
                false, null, null, item.FreeShipping, "MercadoLibre no contestó. Probá de nuevo.");

        // El envío solo es costo tuyo si lo pagás vos. Hoy puede pagarlo el comprador.
        var envioQuePagas = item.FreeShipping ? costos.ShippingCost : 0m;

        // ¿Este precio cruza el escalón? Medido sobre las 3.790 activas: NINGUNA por debajo de
        // $33.000 deja de pagar el cargo fijo, así que el escalón está exactamente ahí. Pero NO se
        // afirma que arriba del escalón el envío pase a ser tuyo: hay 184 publicaciones activas
        // arriba de $33.000 donde lo sigue pagando el comprador. Entonces no se adivina — se avisa
        // y se muestran LOS DOS escenarios, para que la decisión la tome el que sabe.
        var cruza = !item.FreeShipping && item.Price < ESCALON_ENVIO && precio >= ESCALON_ENVIO
                    && costos.ShippingCost > 0;

        var costo = await _pricePush.CalcularCostoTotalAsync(
            await _db.MeliItems.AsNoTracking().FirstAsync(i => i.MeliItemId == meliItemId && i.VariationId == null, ct), ct);

        decimal? ganancia = null, margen = null, gananciaSiPaga = null, margenSiPaga = null;
        var neto = (precio - costos.SaleFeeAmount - envioQuePagas) / IVA;
        if (costo is > 0)
        {
            ganancia = Math.Round(neto - costo.Value, 2);
            margen = Math.Round(ganancia.Value / costo.Value * 100m, 1);

            if (cruza)
            {
                var netoSiPaga = (precio - costos.SaleFeeAmount - costos.ShippingCost) / IVA;
                gananciaSiPaga = Math.Round(netoSiPaga - costo.Value, 2);
                margenSiPaga = Math.Round(gananciaSiPaga.Value / costo.Value * 100m, 1);
            }
        }

        decimal? margenActual = null;
        if (costo is > 0 && item.SaleFeeAmount is > 0 && item.Price > 0)
        {
            var netoHoy = (item.Price - item.SaleFeeAmount.Value - (item.SaleFeeShippingCost ?? 0m)) / IVA;
            margenActual = Math.Round((netoHoy - costo.Value) / costo.Value * 100m, 1);
        }

        string? aviso = null;
        if (cruza)
            aviso = $"Pasás los ${ESCALON_ENVIO:N0}. Arriba de ese precio MercadoLibre suele obligar al envío " +
                    $"gratis, y este envío cuesta ${costos.ShippingCost:N0}. Si pasa, lo pagás vos.";
        else if (costo is null or <= 0)
            aviso = "Este producto no tiene costo cargado, así que no se puede saber qué te deja.";

        return new SimulacionDto(meliItemId, item.Price, precio, costos.SaleFeeAmount, costos.ShippingCost,
            costo, ganancia, margen, margenActual, cruza, margenSiPaga, gananciaSiPaga, item.FreeShipping, aviso);
    }

    /// <summary>TOCA MELI: publica el precio escrito a mano.
    /// `quedaFijo` decide quién manda de acá en adelante — el precio (sincro apagado) o el
    /// porcentaje (sincro prendido, el sistema lo va a recalcular).</summary>
    public async Task<PublicarResultado> PublicarAsync(string meliItemId, decimal precio, bool quedaFijo,
        CancellationToken ct = default)
    {
        if (precio <= 0) return new PublicarResultado(false, "Poné un precio mayor que cero.", null, false);
        if (precio > TOPE_SEGURO)
            return new PublicarResultado(false, $"${precio:N0} está fuera de rango — frenado por seguridad.", null, false);

        var item = await _db.MeliItems.Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId && i.VariationId == null, ct);
        if (item?.MeliAccount is null) return new PublicarResultado(false, "Publicación no encontrada.", null, false);
        if (item.Status is "closed" or "deleted")
            return new PublicarResultado(false, $"La publicación está {item.Status}, no se puede cambiar.", null, false);

        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        if (string.IsNullOrWhiteSpace(token))
            return new PublicarResultado(false, "Sin token de MercadoLibre. Reconectá la cuenta.", null, false);

        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var body = new StringContent(JsonSerializer.Serialize(new { price = precio }), Encoding.UTF8, "application/json");
        var resp = await http.PutAsync($"https://api.mercadolibre.com/items/{meliItemId}", body, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("[PrecioManual] {Mla} rechazado: {Code} {Err}", meliItemId, (int)resp.StatusCode, err);
            var amable = err.Contains("under_review") || err.Contains("not_modifiable")
                ? "MercadoLibre tiene esta publicación en revisión y no deja cambiarle el precio."
                : $"MercadoLibre rechazó el cambio ({(int)resp.StatusCode}).";
            return new PublicarResultado(false, amable, null, false);
        }

        item.Price = precio;
        item.UpdatedAt = DateTime.UtcNow;

        var cfg = await _db.MeliItemSyncConfigs.FirstOrDefaultAsync(c => c.MeliItemId == meliItemId, ct);
        if (cfg is null)
        {
            cfg = new MeliItemSyncConfig { MeliItemId = meliItemId, CreatedAt = DateTime.UtcNow };
            _db.MeliItemSyncConfigs.Add(cfg);
        }
        // Acá se define quién manda. Sin esto, un precio puesto a mano sobre una publicación en modo
        // porcentaje amanece cambiado al día siguiente y parece que el sistema "hace cosas solo".
        if (quedaFijo) cfg.SyncPrecio = false;
        cfg.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // La comisión escala con el precio: si no se recaptura, el margen que se ve queda mintiendo.
        try { await _itemService.RefreshSaleFeeAsync(meliItemId); }
        catch (Exception ex) { _logger.LogWarning(ex, "[PrecioManual] {Mla}: precio OK pero no se recapturó la comisión", meliItemId); }

        _logger.LogWarning("[PrecioManual] {Mla} → ${Precio} (queda fijo: {Fijo})", meliItemId, precio, quedaFijo);
        return new PublicarResultado(true,
            quedaFijo
                ? $"Precio ${precio:N0} publicado. Queda fijo: el sistema no lo va a tocar."
                : $"Precio ${precio:N0} publicado. Ojo: manda el porcentaje, así que el sistema lo va a recalcular.",
            precio, quedaFijo);
    }
}
