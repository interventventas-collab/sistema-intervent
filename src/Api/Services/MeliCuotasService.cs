using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-25 — Botón "Cuotas" de la fila de Publicaciones: ver cómo está HOY la publicación en MeLi
/// (tipo + cuotas), cuánto te quedaría con cada opción, y cambiarla desde el sistema.
///
/// Por qué existe: Osmar pasó a 6 cuotas los discos Aliafor desde MeLi para vender la última unidad
/// de Full antes del descarte, y la pantalla siguió diciendo "Premium, sin cuotas" con la comisión
/// vieja hasta que el sistema volviera a leerla solo.
///
/// Cómo guarda MeLi las cuotas en Argentina:
///   • Clásica (gold_special): sin cuotas sin interés.
///   • Premium (gold_pro) SIN etiqueta = 6 cuotas (es el default de Premium; por eso la pantalla
///     decía "Premium, sin cuotas" cuando en realidad tenía 6).
///   • Premium con etiqueta 3x_campaign / 9x_campaign / 12x_campaign = 3, 9 o 12 cuotas.
///   • Poner una etiqueta de cuotas pasa la publicación a Premium sola (medido el 26/08).
/// El cambio de tipo NO recalcula el precio: el precio lo sigue manejando quien lo maneja hoy.
/// </summary>
public class MeliCuotasService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly MeliItemService _itemService;
    private readonly MeliListingTypeService _tipoService;
    private readonly MeliPricePushService _pricePush;
    private readonly ILogger<MeliCuotasService> _logger;

    private const decimal IVA = 1.21m;

    public MeliCuotasService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService,
        MeliItemService itemService, MeliListingTypeService tipoService, MeliPricePushService pricePush,
        ILogger<MeliCuotasService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _itemService = itemService;
        _tipoService = tipoService;
        _pricePush = pricePush;
        _logger = logger;
    }

    /// <summary>Las 5 opciones que se ofrecen. `Etiqueta` null = sin etiqueta en MeLi.</summary>
    private record Opcion(string Clave, string Tipo, string? Etiqueta, string Nombre, string Cuotas);

    private static readonly Opcion[] Opciones =
    {
        new("clasica", MeliListingTypeService.CLASICA, null, "Clásica", "sin cuotas"),
        new("p3", MeliListingTypeService.PREMIUM, "3x_campaign", "Premium", "3 cuotas"),
        new("p6", MeliListingTypeService.PREMIUM, null, "Premium", "6 cuotas"),
        new("p9", MeliListingTypeService.PREMIUM, "9x_campaign", "Premium", "9 cuotas"),
        new("p12", MeliListingTypeService.PREMIUM, "12x_campaign", "Premium", "12 cuotas"),
    };

    public record OpcionDto(string Clave, string Nombre, string Cuotas, bool Hoy,
        decimal? Comision, decimal? Envio, decimal? Queda, decimal? MargenPct);

    public record CuotasDto(string MeliItemId, string? ClaveHoy, string TextoHoy, decimal Precio,
        bool TieneCosto, List<OpcionDto> Opciones, List<string> Cambios, string? Aviso);

    public record CambiarRequest(string Opcion, decimal? Precio);
    public record CambiarResultado(bool Ok, string Mensaje, CuotasDto? Estado);

    /// <summary>Qué opción es la de hoy. Una etiqueta que no está en la lista (co-fundadas) = ninguna.</summary>
    public static string? ClaveDe(string? tipo, string? etiqueta)
    {
        if (tipo == MeliListingTypeService.CLASICA) return string.IsNullOrEmpty(etiqueta) ? "clasica" : null;
        if (tipo != MeliListingTypeService.PREMIUM) return null;
        return etiqueta switch
        {
            null or "" or "6x_campaign" => "p6",
            "3x_campaign" => "p3",
            "9x_campaign" => "p9",
            "12x_campaign" => "p12",
            _ => null
        };
    }

    public static string TextoDe(string? tipo, string? etiqueta)
    {
        var clave = ClaveDe(tipo, etiqueta);
        var op = Opciones.FirstOrDefault(o => o.Clave == clave);
        if (op is not null) return $"{op.Nombre} · {op.Cuotas}";
        var t = tipo == MeliListingTypeService.PREMIUM ? "Premium" : tipo == MeliListingTypeService.CLASICA ? "Clásica" : (tipo ?? "?");
        return etiqueta == "pcj-co-funded" ? $"{t} · cuotas co-fundadas" : $"{t} · {etiqueta}";
    }

    /// <summary>Relee de MeLi (y actualiza la comisión guardada) y arma las 5 opciones con lo que te
    /// quedaría con cada una, al precio `precio` (el de la cuenta de la fila: el de promo si hay).</summary>
    public async Task<CuotasDto?> VerAsync(string meliItemId, decimal? precio, CancellationToken ct = default)
    {
        var (_, cambios) = await _itemService.RefreshSaleFeeConDetalleAsync(meliItemId);

        var item = await _db.MeliItems.AsNoTracking()
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId && i.VariationId == null, ct);
        if (item is null) return null;

        var p = precio is > 0 ? precio.Value : item.Price;
        var costo = await _pricePush.CalcularCostoTotalAsync(item, ct);
        var claveHoy = ClaveDe(item.ListingTypeId, item.InstallmentTag);

        // De a una: SimularCostosAsync usa la misma conexión a la base (no se puede en paralelo).
        var ops = new List<OpcionDto>();
        foreach (var o in Opciones)
        {
            var c = await _itemService.SimularCostosAsync(meliItemId, p, ct, true, o.Tipo, o.Etiqueta);
            if (c is null) { ops.Add(new OpcionDto(o.Clave, o.Nombre, o.Cuotas, o.Clave == claveHoy, null, null, null, null)); continue; }
            var envio = item.FreeShipping ? c.ShippingCost : 0m;
            decimal? queda = null, margen = null;
            if (costo is > 0)
            {
                var neto = (p - c.SaleFeeAmount - envio) / IVA;
                queda = Math.Round(neto - costo.Value, 0);
                margen = Math.Round(queda.Value / costo.Value * 100m, 1);
            }
            ops.Add(new OpcionDto(o.Clave, o.Nombre, o.Cuotas, o.Clave == claveHoy,
                Math.Round(c.SaleFeeAmount, 0), Math.Round(envio, 0), queda, margen));
        }

        string? aviso = null;
        if (ops.All(o => o.Comision is null)) aviso = "MercadoLibre no contestó cuánto cobra. Probá de nuevo en un rato.";
        else if (costo is null or <= 0) aviso = "Este producto no tiene costo cargado: se ve la comisión, pero no cuánto te queda.";

        return new CuotasDto(meliItemId, claveHoy, TextoDe(item.ListingTypeId, item.InstallmentTag), p,
            costo is > 0, ops, cambios, aviso);
    }

    /// <summary>TOCA MELI: deja la publicación en la opción pedida. No cambia el precio.</summary>
    public async Task<CambiarResultado> CambiarAsync(string meliItemId, string clave, decimal? precio, CancellationToken ct = default)
    {
        var op = Opciones.FirstOrDefault(o => o.Clave == clave);
        if (op is null) return new CambiarResultado(false, "Opción no válida.", null);

        var item = await _db.MeliItems.Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == meliItemId && i.VariationId == null, ct);
        if (item?.MeliAccount is null) return new CambiarResultado(false, "Publicación no encontrada.", null);

        var token = await _accountService.GetValidTokenAsync(item.MeliAccount);
        if (string.IsNullOrWhiteSpace(token)) return new CambiarResultado(false, "No hay conexión con la cuenta de MercadoLibre.", null);

        using var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.Timeout = TimeSpan.FromSeconds(30);

        // 1) Cómo está HOY en MeLi (lo guardado puede estar viejo: es justo el caso que originó esto).
        var (okLeer, tipoHoy, tagsHoy, estado) = await LeerAsync(http, meliItemId, ct);
        if (!okLeer) return new CambiarResultado(false, "MercadoLibre no contestó. Probá de nuevo.", null);
        if (estado is "closed" or "under_review")
            return new CambiarResultado(false, $"MercadoLibre tiene la publicación en estado '{estado}': no se puede cambiar.", null);

        var etiquetaHoy = tagsHoy.FirstOrDefault(t => MeliItemService.MarcasDeCuotas.Contains(t));
        var claveAntes = ClaveDe(tipoHoy, etiquetaHoy);
        if (claveAntes == op.Clave)
        {
            var ya = await VerAsync(meliItemId, precio, ct);
            return new CambiarResultado(true, $"Ya estaba en {op.Nombre} · {op.Cuotas}.", ya);
        }

        // 2) Tipo primero. A Clásica: sacar antes la etiqueta de cuotas (con etiqueta MeLi la vuelve Premium).
        if (op.Tipo == MeliListingTypeService.CLASICA)
        {
            if (etiquetaHoy is not null)
            {
                var (okT, errT) = await PonerEtiquetaAsync(http, meliItemId, tagsHoy, null, ct);
                if (!okT) return await FallaAsync(meliItemId, precio, "MercadoLibre no dejó sacar las cuotas: " + errT, ct);
            }
            if (tipoHoy != MeliListingTypeService.CLASICA)
            {
                var r = await _tipoService.CambiarTipoAsync(meliItemId, MeliListingTypeService.CLASICA, recalcularPrecio: false, ct);
                if (!r.Ok) return await FallaAsync(meliItemId, precio, "MercadoLibre no dejó pasarla a Clásica: " + r.Mensaje, ct);
            }
        }
        else
        {
            if (tipoHoy != MeliListingTypeService.PREMIUM)
            {
                var r = await _tipoService.CambiarTipoAsync(meliItemId, MeliListingTypeService.PREMIUM, recalcularPrecio: false, ct);
                if (!r.Ok) return await FallaAsync(meliItemId, precio, "MercadoLibre no dejó pasarla a Premium: " + r.Mensaje, ct);
            }
            if (etiquetaHoy != op.Etiqueta && !(op.Etiqueta is null && etiquetaHoy == "6x_campaign"))
            {
                // Releer las etiquetas: el cambio de tipo puede haberlas tocado.
                var (_, _, tagsAhora, _) = await LeerAsync(http, meliItemId, ct);
                var (okT, errT) = await PonerEtiquetaAsync(http, meliItemId, tagsAhora, op.Etiqueta, ct);
                if (!okT) return await FallaAsync(meliItemId, precio, "MercadoLibre no dejó cambiar las cuotas: " + errT, ct);
            }
        }

        // 3) Verificar de verdad: MeLi tarda unos segundos en aplicar el cambio (FRENO 2 del cambio de tipo).
        string? claveDespues = null;
        for (var intento = 0; intento < 4 && claveDespues != op.Clave; intento++)
        {
            await Task.Delay(1500, ct);
            var (okV, tipoV, tagsV, _) = await LeerAsync(http, meliItemId, ct);
            if (okV) claveDespues = ClaveDe(tipoV, tagsV.FirstOrDefault(t => MeliItemService.MarcasDeCuotas.Contains(t)));
        }

        _db.MeliCambiosDetectados.Add(new MeliCambioDetectado
        {
            MeliItemId = meliItemId,
            MeliAccountId = item.MeliAccountId,
            Sku = item.Sku,
            Title = item.Title,
            Tipo = "CUOTAS",
            ValorAnterior = TextoDe(tipoHoy, etiquetaHoy),
            ValorNuevo = $"{op.Nombre} · {op.Cuotas}",
            Source = "boton-cuotas",
            DetectedAt = DateTime.UtcNow,
            SeenAt = DateTime.UtcNow   // lo hicimos a propósito: no es alerta
        });
        await _db.SaveChangesAsync(ct);

        var estadoNuevo = await VerAsync(meliItemId, precio, ct);
        if (claveDespues != op.Clave)
            return new CambiarResultado(false,
                $"Se le pidió a MercadoLibre pasarla a {op.Nombre} · {op.Cuotas}, pero hoy MeLi la muestra como " +
                $"{estadoNuevo?.TextoHoy ?? "?"}. Puede tardar unos minutos: tocá ↻ releer más tarde. Si no cambia, hacelo desde MercadoLibre.",
                estadoNuevo);

        _logger.LogWarning("[Cuotas] {Mla}: {Antes} → {Despues}", meliItemId, TextoDe(tipoHoy, etiquetaHoy), $"{op.Nombre} · {op.Cuotas}");
        return new CambiarResultado(true, $"Listo: ahora está en {op.Nombre} · {op.Cuotas}. El precio no se tocó.", estadoNuevo);
    }

    private async Task<CambiarResultado> FallaAsync(string mla, decimal? precio, string msg, CancellationToken ct)
    {
        _logger.LogWarning("[Cuotas] {Mla}: {Msg}", mla, msg);
        return new CambiarResultado(false, msg, await VerAsync(mla, precio, ct));
    }

    private static async Task<(bool Ok, string? Tipo, List<string> Tags, string? Estado)> LeerAsync(
        HttpClient http, string mla, CancellationToken ct)
    {
        try
        {
            var resp = await http.GetAsync($"https://api.mercadolibre.com/items/{mla}?attributes=listing_type_id,tags,status", ct);
            if (!resp.IsSuccessStatusCode) return (false, null, new(), null);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            var tags = r.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.GetString()).Where(x => x is not null).Select(x => x!).ToList()
                : new List<string>();
            return (true, r.TryGetProperty("listing_type_id", out var lt) ? lt.GetString() : null, tags,
                r.TryGetProperty("status", out var st) ? st.GetString() : null);
        }
        catch { return (false, null, new(), null); }
    }

    /// <summary>MeLi pide mandar TODAS las etiquetas que ya tiene la publicación más la de cuotas
    /// (si se manda sólo la nueva, se pierden las demás). `etiqueta` null = sacar las cuotas.</summary>
    private async Task<(bool Ok, string Error)> PonerEtiquetaAsync(HttpClient http, string mla, List<string> tagsHoy,
        string? etiqueta, CancellationToken ct)
    {
        var tags = tagsHoy.Where(t => !MeliItemService.MarcasDeCuotas.Contains(t)).ToList();
        if (etiqueta is not null) tags.Add(etiqueta);
        try
        {
            var body = new StringContent(JsonSerializer.Serialize(new { tags }), Encoding.UTF8, "application/json");
            var resp = await http.PutAsync($"https://api.mercadolibre.com/items/{mla}", body, ct);
            if (resp.IsSuccessStatusCode) return (true, "");
            var err = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning("[Cuotas] {Mla} PUT tags {Code}: {Err}", mla, (int)resp.StatusCode, err);
            return (false, err.Length > 300 ? err[..300] : err);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
