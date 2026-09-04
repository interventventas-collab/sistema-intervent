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

    /// <summary>
    /// Lee una publicacion de MeLi y devuelve todo lo clonable.
    ///
    /// 04/09/2026 — COMPROBADO contra la API: MeLi devuelve 403 en /items/{id} de CUALQUIER
    /// publicacion que no sea del dueño del token (probado con las dos cuentas: cada una ve solo
    /// lo suyo). Por eso hay dos caminos:
    ///   · publicacion NUESTRA  -> se lee con el token de ESA cuenta (con el de la otra da 403).
    ///   · publicacion AJENA    -> si el producto esta en CATALOGO, el clon se arma con la ficha
    ///     del catalogo (/products/{id}), que es de MercadoLibre y se lee sin permiso de nadie.
    ///     Es la misma puerta que usa Integraly para "obtener publicaciones de terceros".
    /// </summary>
    public async Task<ClonPreviewDto> TraerAsync(string referencia)
    {
        if (string.IsNullOrWhiteSpace(referencia))
            return new ClonPreviewDto { Error = "Pegá el link o el código de la publicación." };

        var http = await CrearHttpConTokenAsync();
        if (http is null)
            return new ClonPreviewDto { Error = "No hay ninguna cuenta de MercadoLibre conectada." };

        var prodId = ProductoDeCatalogo(referencia);
        var itemId = await ResolverItemIdAsync(referencia, http);

        // Si la publicacion es NUESTRA hay que preguntar con el token de SU cuenta: con el de la
        // otra, MeLi contesta 403 igual que si fuera de un desconocido (era el bug de GROVAS).
        if (itemId is not null)
        {
            var duenaId = await _db.MeliItems.Where(i => i.MeliItemId == itemId)
                                             .Select(i => (int?)i.MeliAccountId).FirstOrDefaultAsync();
            if (duenaId is int cid)
                http = await CrearHttpConTokenAsync(cid) ?? http;
        }

        if (itemId is not null)
        {
            var (dtoItem, prohibido) = await LeerItemAsync(itemId, http);
            if (dtoItem is not null) return dtoItem;

            if (prodId is null)
                return new ClonPreviewDto
                {
                    Error = prohibido
                        ? "Esa publicación es de otro vendedor y MercadoLibre no deja leerla por sistema. Si el producto está en catálogo, pegá el link de catálogo (el que tiene /p/ en la dirección) y la traigo desde la ficha de MercadoLibre."
                        : $"MercadoLibre no me dejó leer la publicación {itemId}. Puede estar dada de baja."
                };
        }

        if (prodId is not null)
            return await TraerDeCatalogoAsync(prodId, itemId, http);

        return new ClonPreviewDto { Error = "No pude reconocer la publicación en ese link. Probá pegando el código (MLA...) o el link del aviso." };
    }

    /// <summary>Lee /items/{id}. Devuelve dto=null si no se pudo, y prohibido=true cuando MeLi dio 403.</summary>
    private async Task<(ClonPreviewDto? Dto, bool Prohibido)> LeerItemAsync(string itemId, HttpClient http)
    {
        var resp = await http.GetAsync($"https://api.mercadolibre.com/items/{itemId}");
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("Clon: no pude leer {Item} ({Code}): {Body}", itemId, (int)resp.StatusCode, body);
            return (null, resp.StatusCode == System.Net.HttpStatusCode.Forbidden);
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
        return (dto, false);
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
    /// <summary>Codigo del producto de CATALOGO del link (/p/MLAxxx), o null si no es de catalogo.</summary>
    private static string? ProductoDeCatalogo(string texto)
    {
        var m = Regex.Match(texto, @"/p/(ML[A-Z]\d{6,})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Saca el codigo de publicacion (MLA...) de un link o texto.
    ///
    /// 04/09/2026 — antes, con un link de catalogo SIN "ganador de la caja de compra", terminaba
    /// devolviendo el codigo del CATALOGO como si fuera una publicacion y el error era confuso.
    /// Ahora el orden es: 1) el wid= del link (la publicacion que el usuario esta mirando de verdad),
    /// 2) el codigo suelto, 3) el ganador de la caja, 4) el vendedor del pdp_filters, 5) la mas
    /// barata del catalogo. NUNCA devuelve el id de /p/.
    /// </summary>
    private async Task<string?> ResolverItemIdAsync(string referencia, HttpClient http)
    {
        var texto = referencia.Trim();
        var prodId = ProductoDeCatalogo(texto);

        // 1) wid=MLAxxxx — MeLi pone ahi la publicacion concreta que se esta viendo.
        var wid = Regex.Match(texto, @"[?&#]wid=(ML[A-Z]\d{6,})", RegexOptions.IgnoreCase);
        if (wid.Success) return wid.Groups[1].Value.ToUpperInvariant();

        // 2) Codigo suelto o dentro de un link normal (no de catalogo): MLA-1234 o MLA1234
        var m = Regex.Match(texto, @"\bML[A-Z]-?(\d{6,})\b", RegexOptions.IgnoreCase);
        if (m.Success && prodId is null)
            return (texto.Substring(m.Index, 3) + m.Groups[1].Value).ToUpperInvariant();

        if (prodId is not null)
        {
            // 3) El ganador de la caja de compra. Ojo: muchos productos NO tienen ninguno.
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

            // 4 y 5) Sin ganador: la del vendedor que venia en el link, o la primera del catalogo.
            var vend = Regex.Match(texto, @"seller_id(?:%3A|:)(\d+)", RegexOptions.IgnoreCase);
            try
            {
                var ir = await http.GetAsync($"https://api.mercadolibre.com/products/{prodId}/items?limit=20");
                if (ir.IsSuccessStatusCode)
                {
                    using var idoc = JsonDocument.Parse(await ir.Content.ReadAsStringAsync());
                    if (idoc.RootElement.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array
                        && res.GetArrayLength() > 0)
                    {
                        if (vend.Success)
                            foreach (var r in res.EnumerateArray())
                                if (r.TryGetProperty("seller_id", out var sid) && sid.ValueKind == JsonValueKind.Number
                                    && sid.GetInt64().ToString() == vend.Groups[1].Value)
                                    return Str(r, "item_id");
                        return Str(res.EnumerateArray().First(), "item_id");
                    }
                }
            }
            catch { }

            // Es de catalogo: el que llama arma el clon con la ficha, no hace falta la publicacion.
            return null;
        }

        // 6) Link corto (mercadolibre.com/sec/xxxx) u otro: seguimos el redirect y buscamos el codigo.
        if (texto.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var plain = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true });
                plain.Timeout = TimeSpan.FromSeconds(15);
                plain.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");
                var r = await plain.GetAsync(texto);
                var final = r.RequestMessage?.RequestUri?.ToString() ?? "";
                if (ProductoDeCatalogo(final) is null)
                {
                    var m2 = Regex.Match(final, @"\bML[A-Z]-?(\d{6,})\b", RegexOptions.IgnoreCase);
                    if (m2.Success) return (final.Substring(m2.Index, 3) + m2.Groups[1].Value).ToUpperInvariant();
                }
                var html = await r.Content.ReadAsStringAsync();
                var m3 = Regex.Match(html, @"\b(ML[A-Z]\d{9,})\b");
                if (m3.Success) return m3.Groups[1].Value.ToUpperInvariant();
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Arma el clon desde la FICHA DE CATALOGO. Es el unico camino valido para partir de la
    /// publicacion de otro: la ficha es de MercadoLibre, no del vendedor, y se lee sin permiso.
    /// La ficha NO trae categoria — sale de /products/{id}/items, que ademas nos dice a que precio
    /// lo tienen los que ya compiten por ese catalogo.
    /// </summary>
    private async Task<ClonPreviewDto> TraerDeCatalogoAsync(string prodId, string? itemPreferido, HttpClient http)
    {
        var resp = await http.GetAsync($"https://api.mercadolibre.com/products/{prodId}");
        if (!resp.IsSuccessStatusCode)
            return new ClonPreviewDto { Error = $"No pude leer la ficha de catálogo {prodId} en MercadoLibre." };

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        var dto = new ClonPreviewDto
        {
            MeliItemId = itemPreferido ?? "",
            Titulo = Str(root, "name") ?? "",
            Condition = "new",
            Stock = 1,
            FreeShipping = true,
            EsCatalogo = true,
            CatalogProductId = prodId,
            DesdeCatalogo = true,
            Permalink = Str(root, "permalink")
        };

        if (root.TryGetProperty("pictures", out var pics) && pics.ValueKind == JsonValueKind.Array)
            foreach (var pic in pics.EnumerateArray())
            {
                var url = Str(pic, "secure_url") ?? Str(pic, "url");
                if (!string.IsNullOrEmpty(url)) dto.Fotos.Add(url);
            }
        dto.Thumbnail = dto.Fotos.FirstOrDefault();

        if (root.TryGetProperty("attributes", out var attrs) && attrs.ValueKind == JsonValueKind.Array)
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

        // La descripcion de la ficha viene como objeto { type, content }.
        if (root.TryGetProperty("short_description", out var sd))
            dto.Descripcion = sd.ValueKind == JsonValueKind.Object ? Str(sd, "content")
                            : sd.ValueKind == JsonValueKind.String ? sd.GetString() : null;

        // Categoria + quienes ya lo venden.
        try
        {
            var ir = await http.GetAsync($"https://api.mercadolibre.com/products/{prodId}/items?limit=20");
            if (ir.IsSuccessStatusCode)
            {
                using var idoc = JsonDocument.Parse(await ir.Content.ReadAsStringAsync());
                if (idoc.RootElement.TryGetProperty("results", out var res) && res.ValueKind == JsonValueKind.Array)
                {
                    var mias = await _db.MeliAccounts.Select(c => c.MeliUserId).ToListAsync();
                    foreach (var r in res.EnumerateArray())
                    {
                        var comp = new ClonCompetidorDto
                        {
                            MeliItemId = Str(r, "item_id") ?? "",
                            Precio = r.TryGetProperty("price", out var pp) && pp.ValueKind == JsonValueKind.Number ? pp.GetDecimal() : 0m,
                            ListingTypeId = Str(r, "listing_type_id"),
                            SellerId = r.TryGetProperty("seller_id", out var sid) && sid.ValueKind == JsonValueKind.Number ? sid.GetInt64() : 0L
                        };
                        comp.EsMio = mias.Contains(comp.SellerId);
                        dto.Competidores.Add(comp);
                        if (string.IsNullOrEmpty(dto.CategoryId))
                        {
                            var cat = Str(r, "category_id");
                            if (!string.IsNullOrEmpty(cat)) dto.CategoryId = cat;
                        }
                    }
                    dto.Competidores = dto.Competidores.OrderBy(c => c.Precio).ToList();
                }
            }
        }
        catch { }

        // Precio de arranque: el de la publicacion que venia en el link; si no, la mas barata.
        var elegido = dto.Competidores.FirstOrDefault(c => c.MeliItemId == itemPreferido);
        dto.Precio = elegido?.Precio ?? dto.Competidores.FirstOrDefault()?.Precio ?? 0m;
        if (elegido is not null && !string.IsNullOrWhiteSpace(elegido.ListingTypeId))
            dto.ListingTypeId = elegido.ListingTypeId!;

        dto.CategoriaNombre = await NombreCategoriaAsync(http, dto.CategoryId);

        if (string.IsNullOrWhiteSpace(dto.Titulo))
            return new ClonPreviewDto { Error = "La ficha de catálogo no trajo título. Probá con el link del aviso." };

        dto.Ok = true;
        return dto;
    }

    /// <summary>"Herramientas › Discos › ..." para que el usuario vea donde va a caer.</summary>
    private async Task<string?> NombreCategoriaAsync(HttpClient http, string? categoryId)
    {
        if (string.IsNullOrWhiteSpace(categoryId)) return null;
        try
        {
            var catResp = await http.GetAsync($"https://api.mercadolibre.com/categories/{categoryId}");
            if (!catResp.IsSuccessStatusCode) return null;
            using var catDoc = JsonDocument.Parse(await catResp.Content.ReadAsStringAsync());
            if (catDoc.RootElement.TryGetProperty("path_from_root", out var path) && path.ValueKind == JsonValueKind.Array)
                return string.Join(" › ", path.EnumerateArray().Select(x => Str(x, "name")).Where(n => !string.IsNullOrEmpty(n)));
            return Str(catDoc.RootElement, "name");
        }
        catch { return null; }
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
