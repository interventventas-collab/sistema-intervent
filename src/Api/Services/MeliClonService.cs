using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-09-04 — Clonar publicaciones de MercadoLibre.
///
/// Dos usos, el mismo motor:
///  1) Pegar el link (o codigo MLAxxx) de CUALQUIER publicacion → traer todos sus datos
///     (titulo, categoria, ficha tecnica, fotos, descripcion) a un formulario editable
///     → retocar → publicar en la cuenta que se elija.
///  2) Duplicar una publicacion PROPIA de una cuenta a la otra con un boton. Ademas de la
///     publicacion, copia el vinculo con el producto del sistema y la receta
///     (MeliItemComponentes), asi el stock se comparte solo entre las dos cuentas.
///
/// El alta contra MeLi la sigue haciendo MeliItemService.PublishItemAsync (ya trae los
/// reintentos: titulo largo, atributos faltantes con IA, bypass de GTIN).
/// </summary>
public class MeliClonService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly MeliItemService _itemService;
    private readonly ILogger<MeliClonService> _logger;

    // Atributos que MeLi maneja solo o son propios del vendedor original: no se copian.
    private static readonly HashSet<string> AtributosNoCopiables = new()
    { "ITEM_CONDITION", "SELLER_SKU", "GTIN", "EAN", "UPC", "MPN" };

    public MeliClonService(AppDbContext db, IHttpClientFactory httpFactory, MeliAccountService accountService,
        MeliItemService itemService, ILogger<MeliClonService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _itemService = itemService;
        _logger = logger;
    }

    // ---------------------------------------------------------------- TRAER

    /// <summary>Lee una publicacion de MeLi (propia o ajena) y devuelve todo lo clonable.</summary>
    public async Task<ClonPreviewDto> TraerAsync(string referencia)
    {
        if (string.IsNullOrWhiteSpace(referencia))
            return new ClonPreviewDto { Error = "Pegá el link o el código de la publicación." };

        var http = await CrearHttpConTokenAsync();
        if (http is null)
            return new ClonPreviewDto { Error = "No hay ninguna cuenta de MercadoLibre conectada." };

        var itemId = await ResolverItemIdAsync(referencia, http);
        if (itemId is null)
            return new ClonPreviewDto { Error = "No pude reconocer la publicación en ese link. Probá pegando el código (MLA...) o el link largo del aviso." };

        var resp = await http.GetAsync($"https://api.mercadolibre.com/items/{itemId}");
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("Clon: no pude leer {Item}: {Body}", itemId, body);
            return new ClonPreviewDto { Error = $"MercadoLibre no me dejó leer la publicación {itemId}. Puede estar dada de baja." };
        }

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var dto = new ClonPreviewDto
        {
            MeliItemId = Str(root, "id") ?? itemId,
            Titulo = Str(root, "title") ?? Str(root, "family_name") ?? "",
            CategoryId = Str(root, "category_id") ?? "",
            Precio = root.TryGetProperty("price", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetDecimal() : 0m,
            Stock = root.TryGetProperty("available_quantity", out var aq) && aq.ValueKind == JsonValueKind.Number ? aq.GetInt32() : 1,
            Condition = Str(root, "condition") ?? "new",
            ListingTypeId = Str(root, "listing_type_id") ?? "gold_special",
            Permalink = Str(root, "permalink"),
            Thumbnail = Str(root, "thumbnail"),
            EsCatalogo = root.TryGetProperty("catalog_listing", out var cl) && cl.ValueKind == JsonValueKind.True,
            CatalogProductId = Str(root, "catalog_product_id"),
            Vendidas = root.TryGetProperty("sold_quantity", out var sq) && sq.ValueKind == JsonValueKind.Number ? sq.GetInt32() : 0,
        };

        if (root.TryGetProperty("shipping", out var sh) && sh.ValueKind == JsonValueKind.Object)
            dto.FreeShipping = sh.TryGetProperty("free_shipping", out var fs) && fs.ValueKind == JsonValueKind.True;

        // Fotos
        if (root.TryGetProperty("pictures", out var pics) && pics.ValueKind == JsonValueKind.Array)
        {
            foreach (var pic in pics.EnumerateArray())
            {
                var url = Str(pic, "secure_url") ?? Str(pic, "url");
                if (!string.IsNullOrEmpty(url)) dto.Fotos.Add(url);
            }
        }

        // Ficha tecnica
        if (root.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in attrs.EnumerateArray())
            {
                var id = Str(a, "id");
                if (string.IsNullOrEmpty(id) || AtributosNoCopiables.Contains(id)) continue;
                var valueId = Str(a, "value_id");
                var valueName = Str(a, "value_name");
                if (valueId == "-1") valueId = null;
                if (valueName == "-1") valueName = null;
                if (valueId is null && string.IsNullOrWhiteSpace(valueName)) continue;
                dto.Atributos.Add(new ClonAtributoDto
                {
                    Id = id,
                    Nombre = Str(a, "name") ?? id,
                    ValueId = valueId,
                    ValueName = valueName
                });
            }
        }

        // Variantes: en esta version no se clonan (avisamos)
        if (root.TryGetProperty("variations", out var vars) && vars.ValueKind == JsonValueKind.Array)
            dto.CantidadVariantes = vars.GetArrayLength();

        // Descripcion
        try
        {
            var descResp = await http.GetAsync($"https://api.mercadolibre.com/items/{dto.MeliItemId}/description");
            if (descResp.IsSuccessStatusCode)
            {
                using var descDoc = JsonDocument.Parse(await descResp.Content.ReadAsStringAsync());
                dto.Descripcion = Str(descDoc.RootElement, "plain_text");
            }
        }
        catch { }

        // Nombre de la categoria (para que el usuario vea donde va a caer)
        if (!string.IsNullOrEmpty(dto.CategoryId))
        {
            try
            {
                var catResp = await http.GetAsync($"https://api.mercadolibre.com/categories/{dto.CategoryId}");
                if (catResp.IsSuccessStatusCode)
                {
                    using var catDoc = JsonDocument.Parse(await catResp.Content.ReadAsStringAsync());
                    if (catDoc.RootElement.TryGetProperty("path_from_root", out var path) && path.ValueKind == JsonValueKind.Array)
                        dto.CategoriaNombre = string.Join(" › ", path.EnumerateArray().Select(p => Str(p, "name")).Where(n => !string.IsNullOrEmpty(n)));
                    else
                        dto.CategoriaNombre = Str(catDoc.RootElement, "name");
                }
            }
            catch { }
        }

        // ¿Es una publicacion nuestra? (para poder copiar el vinculo con el producto del sistema)
        var sellerId = root.TryGetProperty("seller_id", out var si) && si.ValueKind == JsonValueKind.Number ? si.GetInt64() : 0L;
        var cuentas = await _db.MeliAccounts.ToListAsync();
        var propia = cuentas.FirstOrDefault(c => c.MeliUserId == sellerId);
        if (propia is not null)
        {
            dto.EsPropia = true;
            dto.CuentaOrigenId = propia.Id;
            dto.CuentaOrigenNombre = propia.Nickname;
            var enSistema = await _db.MeliItems.FirstOrDefaultAsync(i => i.MeliItemId == dto.MeliItemId);
            if (enSistema is not null)
            {
                dto.Sku = enSistema.Sku;
                dto.ProductoVinculadoId = enSistema.CafeProductoId;
                if (enSistema.CafeProductoId is int cpid)
                    dto.ProductoVinculadoNombre = await _db.CafeProductos.Where(p => p.Id == cpid).Select(p => p.Nombre).FirstOrDefaultAsync();
            }
        }
        else
        {
            dto.VendedorNickname = await ObtenerNicknameVendedorAsync(http, sellerId);
        }

        dto.Ok = true;
        return dto;
    }

    // ------------------------------------------------------------- PUBLICAR

    /// <summary>Publica el clon (ya editado por el usuario) en la cuenta elegida.</summary>
    public async Task<ClonPublicarResponse> PublicarAsync(ClonPublicarRequest req)
    {
        if (req.CuentaDestinoId <= 0)
            return new ClonPublicarResponse { Error = "Elegí en qué cuenta publicar." };
        if (string.IsNullOrWhiteSpace(req.Titulo))
            return new ClonPublicarResponse { Error = "Falta el título." };
        if (string.IsNullOrWhiteSpace(req.CategoryId))
            return new ClonPublicarResponse { Error = "Falta la categoría." };
        if (req.Precio <= 0)
            return new ClonPublicarResponse { Error = "Poné un precio mayor a cero." };

        var publishReq = new PublishItemRequest
        {
            ProductId = null,
            MeliAccountId = req.CuentaDestinoId,
            CategoryId = req.CategoryId,
            Title = req.Titulo,
            FamilyName = req.Titulo,
            Description = req.Descripcion,
            Price = req.Precio,
            AvailableQuantity = req.Stock > 0 ? req.Stock : 1,
            Condition = string.IsNullOrWhiteSpace(req.Condition) ? "new" : req.Condition,
            ListingTypeId = string.IsNullOrWhiteSpace(req.ListingTypeId) ? "gold_special" : req.ListingTypeId,
            FreeShipping = req.FreeShipping,
            SellerCustomField = string.IsNullOrWhiteSpace(req.Sku) ? null : req.Sku.Trim(),
            PictureUrls = req.Fotos.Where(f => !string.IsNullOrWhiteSpace(f)).ToList(),
            Attributes = req.Atributos
                .Where(a => !string.IsNullOrWhiteSpace(a.Id) && (a.ValueId is not null || !string.IsNullOrWhiteSpace(a.ValueName)))
                .Select(a => new PublishAttributeDto { Id = a.Id, ValueId = a.ValueId, ValueName = a.ValueName })
                .ToList()
        };

        var res = await _itemService.PublishItemAsync(publishReq);
        if (!res.Success || string.IsNullOrEmpty(res.MeliItemId))
            return new ClonPublicarResponse { Error = res.Error ?? "MercadoLibre rechazó la publicación." };

        var salida = new ClonPublicarResponse
        {
            Ok = true,
            MeliItemId = res.MeliItemId,
            Permalink = res.Permalink,
            CuentaDestinoNombre = await _db.MeliAccounts.Where(c => c.Id == req.CuentaDestinoId).Select(c => c.Nickname).FirstOrDefaultAsync()
        };

        // Copiar el vinculo con el producto del sistema (y la receta) desde la publicacion origen.
        if (req.CopiarVinculoStock && !string.IsNullOrWhiteSpace(req.MeliItemOrigen))
            salida.VinculoCopiado = await CopiarVinculoDeStockAsync(req.MeliItemOrigen!, res.MeliItemId!);

        return salida;
    }

    // ------------------------------------------------- DUPLICAR ENTRE CUENTAS

    /// <summary>Un boton: agarra una publicacion propia y la vuelve a publicar en la otra cuenta.</summary>
    public async Task<ClonPublicarResponse> DuplicarACuentaAsync(ClonDuplicarRequest req)
    {
        var origen = await _db.MeliItems.Include(i => i.MeliAccount)
            .FirstOrDefaultAsync(i => i.MeliItemId == req.MeliItemId);
        if (origen is null)
            return new ClonPublicarResponse { Error = $"No encontré la publicación {req.MeliItemId} en el sistema." };

        // Cuenta destino: la indicada, o "la otra" si hay exactamente dos.
        var cuentas = await _db.MeliAccounts.ToListAsync();
        var destinoId = req.CuentaDestinoId;
        if (destinoId <= 0)
        {
            var otras = cuentas.Where(c => c.Id != origen.MeliAccountId).ToList();
            if (otras.Count == 0) return new ClonPublicarResponse { Error = "Hay una sola cuenta conectada: no hay a dónde duplicar." };
            if (otras.Count > 1) return new ClonPublicarResponse { Error = "Hay más de una cuenta: elegí a cuál duplicar." };
            destinoId = otras[0].Id;
        }
        if (destinoId == origen.MeliAccountId)
            return new ClonPublicarResponse { Error = "La cuenta destino es la misma que la de origen." };

        // Aviso si ya hay algo igual en la cuenta destino (mismo SKU o mismo titulo).
        if (!req.Forzar)
        {
            var yaEsta = await _db.MeliItems.FirstOrDefaultAsync(i =>
                i.MeliAccountId == destinoId && i.Status != "closed" &&
                ((origen.Sku != null && i.Sku == origen.Sku) || i.Title == origen.Title));
            if (yaEsta is not null)
                return new ClonPublicarResponse
                {
                    YaExiste = true,
                    MeliItemId = yaEsta.MeliItemId,
                    Permalink = yaEsta.Permalink,
                    Error = $"En esa cuenta ya hay una publicación parecida: {yaEsta.MeliItemId} — {yaEsta.Title}."
                };
        }

        var datos = await TraerAsync(req.MeliItemId);
        if (!datos.Ok) return new ClonPublicarResponse { Error = datos.Error };

        return await PublicarAsync(new ClonPublicarRequest
        {
            CuentaDestinoId = destinoId,
            MeliItemOrigen = req.MeliItemId,
            Titulo = datos.Titulo,
            CategoryId = datos.CategoryId,
            Descripcion = datos.Descripcion,
            Precio = req.Precio ?? datos.Precio,
            Stock = req.Stock ?? datos.Stock,
            Condition = datos.Condition,
            ListingTypeId = string.IsNullOrWhiteSpace(req.ListingTypeId) ? datos.ListingTypeId : req.ListingTypeId,
            FreeShipping = datos.FreeShipping,
            Sku = datos.Sku,
            Fotos = datos.Fotos,
            Atributos = datos.Atributos,
            CopiarVinculoStock = true
        });
    }

    // ------------------------------------------------------------- INTERNOS

    /// <summary>Copia a la publicacion nueva el vinculo con el producto del sistema y la receta
    /// (MeliItemComponentes) de la publicacion origen. Sin esto el stock no le llegaria.</summary>
    private async Task<bool> CopiarVinculoDeStockAsync(string meliItemOrigen, string meliItemNuevo)
    {
        try
        {
            var origen = await _db.MeliItems.FirstOrDefaultAsync(i => i.MeliItemId == meliItemOrigen);
            var nuevo = await _db.MeliItems.FirstOrDefaultAsync(i => i.MeliItemId == meliItemNuevo);
            if (origen is null || nuevo is null) return false;

            nuevo.CafeProductoId = origen.CafeProductoId;
            nuevo.CafeFormato = origen.CafeFormato;
            nuevo.ProductId = origen.ProductId;
            nuevo.ComboId = origen.ComboId;
            if (string.IsNullOrWhiteSpace(nuevo.Sku)) nuevo.Sku = origen.Sku;

            var componentes = await _db.MeliItemComponentes
                .Where(c => c.MeliItemId == meliItemOrigen && c.MeliVariationId == null)
                .ToListAsync();
            var yaTiene = await _db.MeliItemComponentes.AnyAsync(c => c.MeliItemId == meliItemNuevo);
            if (!yaTiene)
            {
                foreach (var c in componentes)
                {
                    _db.MeliItemComponentes.Add(new MeliItemComponente
                    {
                        MeliItemId = meliItemNuevo,
                        CafeProductoId = c.CafeProductoId,
                        Cantidad = c.Cantidad,
                        Formato = c.Formato,
                        Source = "clon"
                    });
                }
            }
            await _db.SaveChangesAsync();
            return origen.CafeProductoId is not null || componentes.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clon: no pude copiar el vinculo de stock de {Origen} a {Nuevo}", meliItemOrigen, meliItemNuevo);
            return false;
        }
    }

    /// <summary>Saca el MLAxxxx de un link pegado (largo, corto /sec/, de catalogo /p/) o de un codigo suelto.</summary>
    private async Task<string?> ResolverItemIdAsync(string referencia, HttpClient http)
    {
        var texto = referencia.Trim();

        // Codigo suelto o dentro del link largo: MLA-1234 o MLA1234
        var m = Regex.Match(texto, @"\bML[A-Z]-?(\d{6,})\b", RegexOptions.IgnoreCase);
        if (m.Success && !texto.Contains("/p/", StringComparison.OrdinalIgnoreCase))
            return (texto.Substring(m.Index, 3) + m.Groups[1].Value).ToUpperInvariant();

        // Link de CATALOGO (/p/MLAxxxx): pedimos el producto y usamos el ganador de la caja de compra.
        var cat = Regex.Match(texto, @"/p/(ML[A-Z]\d{6,})", RegexOptions.IgnoreCase);
        if (cat.Success)
        {
            var prodId = cat.Groups[1].Value.ToUpperInvariant();
            try
            {
                var pr = await http.GetAsync($"https://api.mercadolibre.com/products/{prodId}");
                if (pr.IsSuccessStatusCode)
                {
                    using var pd = JsonDocument.Parse(await pr.Content.ReadAsStringAsync());
                    if (pd.RootElement.TryGetProperty("buy_box_winner", out var w) && w.ValueKind == JsonValueKind.Object)
                    {
                        var winner = Str(w, "item_id");
                        if (!string.IsNullOrEmpty(winner)) return winner;
                    }
                }
            }
            catch { }
        }

        // Link corto (mercadolibre.com/sec/xxxx) u otro: seguimos el redirect y buscamos el codigo.
        if (texto.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var plain = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
                plain.Timeout = TimeSpan.FromSeconds(15);
                plain.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                var r = await plain.GetAsync(texto);
                var final = r.RequestMessage?.RequestUri?.ToString() ?? "";
                var m2 = Regex.Match(final, @"\bML[A-Z]-?(\d{6,})\b", RegexOptions.IgnoreCase);
                if (m2.Success) return (final.Substring(m2.Index, 3) + m2.Groups[1].Value).ToUpperInvariant();
                var html = await r.Content.ReadAsStringAsync();
                var m3 = Regex.Match(html, @"\b(ML[A-Z]\d{9,})\b");
                if (m3.Success) return m3.Groups[1].Value.ToUpperInvariant();
            }
            catch { }
        }

        return null;
    }

    private async Task<HttpClient?> CrearHttpConTokenAsync(int? cuentaId = null)
    {
        var cuenta = cuentaId is int id
            ? await _db.MeliAccounts.FindAsync(id)
            : await _db.MeliAccounts.OrderBy(c => c.Id).FirstOrDefaultAsync();
        if (cuenta is null) return null;
        var token = await _accountService.GetValidTokenAsync(cuenta);
        if (token is null) return null;
        var http = _httpFactory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return http;
    }

    private static async Task<string?> ObtenerNicknameVendedorAsync(HttpClient http, long sellerId)
    {
        if (sellerId <= 0) return null;
        try
        {
            var r = await http.GetAsync($"https://api.mercadolibre.com/users/{sellerId}");
            if (!r.IsSuccessStatusCode) return null;
            using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            return Str(d.RootElement, "nickname");
        }
        catch { return null; }
    }

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
