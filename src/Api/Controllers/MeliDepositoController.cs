using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-09-21: página "Órdenes MeLi · Depósito" (/deposito/ordenes-meli). Antes el escaneo de
/// etiqueta vivía adentro del armado de pedidos (/cafe/preparacion); ahora es una página aparte con:
/// el listado de órdenes MeLi (agrupadas por ENVÍO: lo que va en un mismo paquete) + la ficha grande
/// de cada una (foto grande, combo, mensajes, todas las preguntas del comprador y sus compras
/// anteriores). SIN PLATA a propósito: el depósito no ve precio, comisión ni lo que se recibe.
/// Solo lee: no toca stock, ni MeLi (salvo traer una venta que falte, igual que antes).
/// </summary>
[ApiController]
[Route("api/meli-deposito")]
[Authorize]
public class MeliDepositoController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;

    public MeliDepositoController(AppDbContext db, IServiceScopeFactory scopeFactory)
    {
        _db = db;
        _scopeFactory = scopeFactory;
    }

    // ─────────────────────────── LISTADO ───────────────────────────

    /// <summary>Órdenes MeLi agrupadas por envío, de la más nueva a la más vieja. Filtros (todos
    /// opcionales): dias (1=hoy, 2=hoy+ayer, 7, 30), cuenta (MeliAccountId), envio (flex|correo|full),
    /// estado (despachar|camino|entregada|cancelada), q (comprador / producto / nº de venta o envío).</summary>
    [HttpGet("ordenes")]
    public async Task<IActionResult> Ordenes([FromQuery] int dias = 2, [FromQuery] int? cuenta = null,
        [FromQuery] string? envio = null, [FromQuery] string? estado = null, [FromQuery] string? q = null)
    {
        if (dias < 1) dias = 1;
        if (dias > 90) dias = 90;
        // "Hoy" es el día ARGENTINO. 2026-09-24: MeliOrders.DateCreated YA está en hora argentina
        // (el contenedor corre con TZ=America/Argentina y el sync lo convierte al leerlo de MeLi).
        var desdeAr = DateTime.UtcNow.AddHours(-3).Date.AddDays(-(dias - 1));

        var query = _db.MeliOrders.AsNoTracking().Where(o => o.DateCreated >= desdeAr);
        if (cuenta.HasValue) query = query.Where(o => o.MeliAccountId == cuenta.Value);

        var texto = q?.Trim();
        if (!string.IsNullOrEmpty(texto))
        {
            if (long.TryParse(texto, out var n))
                query = query.Where(o => o.MeliOrderId == n || o.ShippingId == n || o.PackId == n);
            else
                query = query.Where(o => o.BuyerNickname.Contains(texto) || o.ItemTitle.Contains(texto));
        }

        var filas = await query
            .Select(o => new
            {
                o.MeliOrderId, o.MeliAccountId, o.Status, o.DateCreated, o.BuyerNickname,
                o.ItemId, o.ItemTitle, o.Quantity, o.ShippingId, o.PackId,
                o.ShippingStatus, o.ShippingSubstatus, o.ShippingMode, o.LogisticType, o.EtiquetaImpresaAt
            })
            .ToListAsync();

        var cuentas = await _db.MeliAccounts.AsNoTracking()
            .Select(a => new { a.Id, a.Nickname }).ToListAsync();
        var nickCuenta = cuentas.ToDictionary(a => a.Id, a => a.Nickname);

        var itemIds = filas.Select(f => f.ItemId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        var fotos = await FotosDeItemsAsync(itemIds);

        var grupos = filas
            .GroupBy(f => f.ShippingId ?? f.PackId ?? f.MeliOrderId)
            .Select(g =>
            {
                var p = g.OrderBy(x => x.DateCreated).First();
                var tipo = TipoEnvio(p.LogisticType, p.ShippingMode);
                var est = EstadoAmigable(g.All(x => x.Status == "cancelled") ? "cancelled" : p.Status, p.ShippingStatus);
                return new
                {
                    numero = p.ShippingId ?? p.PackId ?? p.MeliOrderId,
                    numeroVenta = p.PackId ?? p.MeliOrderId,
                    // 2026-09-24: para imprimir la etiqueta desde el deposito (Full no tiene etiqueta).
                    numeroEnvio = tipo.Clave == "full" ? null : p.ShippingId,
                    etiquetaImpresa = EtiquetaImpresa(p.ShippingStatus, p.ShippingSubstatus) || g.Any(x => x.EtiquetaImpresaAt != null),
                    etiquetaImpresaAt = g.Min(x => x.EtiquetaImpresaAt),
                    fecha = p.DateCreated,
                    cuenta = nickCuenta.TryGetValue(p.MeliAccountId, out var nk) ? nk : "",
                    comprador = p.BuyerNickname,
                    tipo = tipo.Etiqueta,
                    tipoClave = tipo.Clave,
                    estado = est.Etiqueta,
                    estadoClave = est.Clave,
                    unidades = g.Sum(x => x.Quantity),
                    productos = g.OrderBy(x => x.ItemTitle).Select(x => new
                    {
                        titulo = x.ItemTitle,
                        cantidad = x.Quantity,
                        foto = fotos.TryGetValue(x.ItemId ?? "", out var f) ? f.Foto : null
                    }).ToList()
                };
            })
            .Where(g => string.IsNullOrEmpty(envio) || g.tipoClave == envio)
            .Where(g => string.IsNullOrEmpty(estado) || g.estadoClave == estado)
            .OrderByDescending(g => g.fecha)
            .Take(500)
            .ToList();

        return Ok(new
        {
            cuentas = cuentas.Select(a => new { id = a.Id, nombre = a.Nickname }),
            ordenes = grupos
        });
    }

    // ─────────────────────────── FICHA ───────────────────────────

    public record EscanearRequest(string Code);

    /// <summary>Escaneo de la etiqueta (QR Flex o código de barras de Correo) → ficha de esa venta.
    /// Si la venta no está en la base (muy nueva o vieja), la trae de MeLi en el momento.</summary>
    [HttpPost("escanear")]
    public async Task<IActionResult> Escanear([FromBody] EscanearRequest req)
    {
        var num = ExtractShipmentIdFromCode(req?.Code);
        if (num is null)
            return Ok(new { ok = false, mensaje = "No pude leer el número de esa etiqueta. Probá de nuevo o tipealo a mano." });
        return await ArmarFichaAsync(num.Value, traerSiFalta: true);
    }

    /// <summary>Ficha de una venta por número (envío, pack o nº de venta). La usa el listado.</summary>
    [HttpGet("ficha/{num:long}")]
    public async Task<IActionResult> Ficha(long num) => await ArmarFichaAsync(num, traerSiFalta: false);

    private async Task<IActionResult> ArmarFichaAsync(long num, bool traerSiFalta)
    {
        var (productos, shippingId) = await ResolverProductosDeVentaMeliAsync(num);
        if (productos.Count == 0 && traerSiFalta)
        {
            await TraerVentaMeliEnVivoAsync(num);
            (productos, shippingId) = await ResolverProductosDeVentaMeliAsync(num);
        }
        if (productos.Count == 0)
            return Ok(new { ok = false, numero = num,
                mensaje = $"No encontré la venta de MercadoLibre para el código {num}. Verificá el número; puede ser de otra cuenta que no está conectada." });

        var primero = productos.OrderBy(p => p.DateCreated).First();
        var buyerId = primero.BuyerId;
        var tipo = TipoEnvio(primero.LogisticType, primero.ShippingMode);
        var est = EstadoAmigable(productos.All(x => x.Status == "cancelled") ? "cancelled" : primero.Status, primero.ShippingStatus);
        // con tracking: si el token está vencido, el servicio lo renueva y lo guarda en esta cuenta
        var cuenta = await _db.MeliAccounts.FirstOrDefaultAsync(a => a.Id == primero.MeliAccountId);

        var itemIds = productos.Select(p => p.ItemId).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();

        // ── MENSAJES post-venta, EN VIVO desde MeLi. Distingue "no escribió" de "MeLi no contestó".
        bool mensajesOk = false;
        var mensajes = new List<object>();
        if (cuenta is not null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orderSvc = scope.ServiceProvider.GetRequiredService<MeliOrderService>();
                var (ok, msgs) = await orderSvc.TryGetPackMessagesAsync(primero.PackId ?? primero.MeliOrderId, cuenta);
                mensajesOk = ok;
                mensajes = msgs.OrderBy(m => m.Date).Select(m => (object)new
                {
                    de = m.FromUserId == buyerId ? "comprador" : "vendedor",
                    texto = m.Text,
                    // en UTC explícito: el navegador (que puede estar en España) lo pasa a hora argentina
                    fecha = m.Date.HasValue ? DateTime.SpecifyKind(m.Date.Value.ToUniversalTime(), DateTimeKind.Utc) : (DateTime?)null
                }).ToList();
            }
            catch { mensajesOk = false; }
        }

        // ── NOTAS de la venta en MeLi ("Agregar nota" de la web de MeLi + las que pone el sistema),
        // EN VIVO. Una por orden: un pack puede tener varias órdenes.
        bool notasOk = cuenta is not null;
        var notas = new List<object>();
        if (cuenta is not null)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var orderSvc = scope.ServiceProvider.GetRequiredService<MeliOrderService>();
                foreach (var oid in productos.Select(p => p.MeliOrderId).Distinct().Take(5))
                {
                    var (ok, ns) = await orderSvc.TryGetOrderNotesAsync(oid, cuenta);
                    if (!ok) notasOk = false;
                    notas.AddRange(ns.Select(n => (object)new
                    {
                        texto = n.Texto,
                        fecha = n.Fecha.HasValue ? DateTime.SpecifyKind(n.Fecha.Value, DateTimeKind.Utc) : (DateTime?)null
                    }));
                }
            }
            catch { notasOk = false; }
        }

        // ── TODAS las preguntas que hizo este comprador (cualquier publicación, cualquier cuenta).
        var preguntas = await _db.MeliQuestions.AsNoTracking()
            .Where(qq => qq.FromUserId == buyerId)
            .OrderByDescending(qq => qq.DateCreated)
            .Select(qq => new
            {
                texto = qq.Text,
                respuesta = qq.AnswerText,
                fecha = qq.DateCreated,
                producto = qq.ItemTitle,
                itemId = qq.ItemId
            })
            .Take(100)
            .ToListAsync();
        var preguntasOut = preguntas.Select(qq => new
        {
            qq.texto, qq.respuesta, qq.fecha, qq.producto,
            deEsteProducto = itemIds.Contains(qq.itemId)
        }).ToList();

        // ── COMPRAS ANTERIORES del mismo comprador (otros envíos), sin plata.
        var claveActual = shippingId ?? primero.PackId ?? primero.MeliOrderId;
        var historial = await _db.MeliOrders.AsNoTracking()
            .Where(o => o.BuyerId == buyerId)
            .Select(o => new { o.MeliOrderId, o.ShippingId, o.PackId, o.DateCreated, o.ItemTitle, o.Quantity, o.Status, o.ShippingStatus, o.MeliAccountId })
            .ToListAsync();
        var nickCuenta = await _db.MeliAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Nickname);
        var compras = historial
            .GroupBy(o => o.ShippingId ?? o.PackId ?? o.MeliOrderId)
            .Where(g => g.Key != claveActual)
            .Select(g =>
            {
                var p = g.OrderBy(x => x.DateCreated).First();
                return new
                {
                    numero = g.Key,
                    fecha = p.DateCreated,
                    cuenta = nickCuenta.TryGetValue(p.MeliAccountId, out var nk) ? nk : "",
                    estado = EstadoAmigable(g.All(x => x.Status == "cancelled") ? "cancelled" : p.Status, p.ShippingStatus).Etiqueta,
                    productos = g.Select(x => new { titulo = x.ItemTitle, cantidad = x.Quantity }).ToList()
                };
            })
            .OrderByDescending(c => c.fecha)
            .ToList();

        // ── POST-ITS nuestros de este envío (tabla Postits, un "tablero" por envío).
        var scopePostit = $"meli-envio:{claveActual}";
        var postits = await _db.Postits.AsNoTracking()
            .Where(p => p.Scope == scopePostit)
            .OrderBy(p => p.CreatedAt)
            .Select(p => new { id = p.Id, texto = p.Texto, color = p.Color, creadoPor = p.CreadoPor, scope = p.Scope, createdAt = p.CreatedAt })
            .ToListAsync();

        // ── Productos: SKU + FOTO GRANDE + componentes si es combo.
        var fotos = await FotosDeItemsAsync(itemIds);
        var comps = await _db.MeliItemComponentes.AsNoTracking()
            .Where(c => itemIds.Contains(c.MeliItemId))
            .Include(c => c.Producto)
            .ToListAsync();

        var productosOut = productos
            .OrderBy(p => p.ItemTitle)
            .Select(p =>
            {
                fotos.TryGetValue(p.ItemId ?? "", out var mi);
                var componentes = comps
                    .Where(c => c.MeliItemId == p.ItemId
                        && (c.MeliVariationId == null || c.MeliVariationId == p.VariationId))
                    .Select(c => new
                    {
                        nombre = c.Producto != null ? c.Producto.Nombre : "(producto)",
                        sku = c.Producto != null ? c.Producto.Sku : null,
                        cantidad = c.Cantidad,
                        formato = c.Formato
                    }).ToList();
                bool esCombo = componentes.Count > 1 || componentes.Any(x => x.cantidad > 1);
                return new
                {
                    titulo = p.ItemTitle,
                    cantidad = p.Quantity,
                    sku = mi.Sku,
                    foto = mi.Foto,
                    esCombo,
                    componentes
                };
            }).ToList();

        return Ok(new
        {
            ok = true,
            origen = "meli",
            numero = claveActual,
            numeroEnvio = shippingId,
            numeroVenta = primero.PackId ?? primero.MeliOrderId,
            fecha = primero.DateCreated,
            cuenta = cuenta?.Nickname,
            comprador = primero.BuyerNickname,
            tipo = tipo.Etiqueta,
            estado = est.Etiqueta,
            unidades = productos.Sum(p => p.Quantity),
            productos = productosOut,
            mensajesOk,
            mensajes,
            notasOk,
            notas,
            postits,
            preguntas = preguntasOut,
            compras
        });
    }

    // ─────────────────────── ESCÁNER DEL CELU DEL DEPÓSITO ───────────────────────

    public record CeluEscanearRequest(string? DeviceId, string? Code);

    /// <summary>2026-09-23: escáner con la cámara del CELU DEL DEPÓSITO (/escaner). Sin login, como la
    /// lista de armado (/picking): solo le contesta al celu habilitado (Cafe_PickingDispositivos).
    /// Reconoce la etiqueta de MeLi (QR Flex / barras de Correo) y el QR del repartidor que va en el
    /// comprobante de las ventas propias (/repartidor/{token}). Sin plata.</summary>
    [HttpPost("celu/escanear")]
    [AllowAnonymous]
    public async Task<IActionResult> CeluEscanear([FromBody] CeluEscanearRequest req)
    {
        var deviceId = req?.DeviceId?.Trim() ?? "";
        var deposito = await _db.CafePickingDispositivos.Where(x => x.Activo).OrderBy(x => x.Id).FirstOrDefaultAsync();
        if (deposito is not null && !string.IsNullOrEmpty(deviceId) && deposito.DeviceId == deviceId)
        {
            deposito.LastSeenAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        else if (string.IsNullOrEmpty(deviceId) || !(await LeerCelusExtraAsync()).Any(c => c.DeviceId == deviceId))
            return StatusCode(403, new { error = "no_habilitado" });

        var code = req?.Code?.Trim() ?? "";
        if (code.Length == 0)
            return Ok(new { ok = false, mensaje = "No leí nada. Probá de nuevo." });

        // Venta propia: QR del repartidor impreso en el comprobante.
        var idx = code.IndexOf("/repartidor/", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var token = code.Substring(idx + "/repartidor/".Length).Split('?', '#', '/')[0];
            var venta = string.IsNullOrEmpty(token) ? null
                : await _db.CafeVentas.AsNoTracking().FirstOrDefaultAsync(v => v.PublicToken == token);
            return await FichaVentaPropiaAsync(venta);
        }

        // Nº de comprobante tipeado a mano (ej. CAFE-2026-0123).
        if (code.Any(char.IsLetter) && !code.Contains('/') && !code.Contains('{'))
        {
            var venta = await _db.CafeVentas.AsNoTracking().FirstOrDefaultAsync(v => v.Numero == code);
            if (venta is not null) return await FichaVentaPropiaAsync(venta);
        }

        // Cualquier otro enlace (QR de alquiler, de visita, una web) no es etiqueta de envío.
        if (code.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return Ok(new { ok = false, mensaje = "Ese código no es una etiqueta de envío (ni de MercadoLibre ni de una venta nuestra)." });

        var num = ExtractShipmentIdFromCode(code);
        if (num is null)
            return Ok(new { ok = false, mensaje = "No pude leer el número de esa etiqueta. Probá de nuevo." });
        return await ArmarFichaAsync(num.Value, traerSiFalta: true);
    }

    // Celus EXTRA para el escáner (ej. el del dueño en España), aparte del celu del depósito:
    // viven en un AppSetting, así no se mezclan con la lista de armado ni con "Cambiar celu".
    private const string KeyCelusExtra = "deposito.escaner_celus_extra";
    public record CeluExtra(string DeviceId, string? Nombre, string? HabilitadoPor, DateTime Fecha);
    public record CeluHabilitarRequest(string? DeviceId, string? Nombre);

    private async Task<List<CeluExtra>> LeerCelusExtraAsync()
    {
        var fila = await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(a => a.Key == KeyCelusExtra);
        if (string.IsNullOrWhiteSpace(fila?.Value)) return new();
        try { return System.Text.Json.JsonSerializer.Deserialize<List<CeluExtra>>(fila.Value) ?? new(); }
        catch { return new(); }
    }

    /// <summary>2026-09-23: habilita ESTE celu para el escáner, además del del depósito. Solo un
    /// administrador logueado en ese celu (así nadie habilita un teléfono cualquiera).</summary>
    [HttpPost("celu/habilitar-extra")]
    [Authorize(Roles = "admin")]
    public async Task<IActionResult> CeluHabilitarExtra([FromBody] CeluHabilitarRequest req)
    {
        var deviceId = req?.DeviceId?.Trim() ?? "";
        if (deviceId.Length < 8) return BadRequest(new { error = "Falta identificar el celular" });
        var lista = await LeerCelusExtraAsync();
        if (!lista.Any(c => c.DeviceId == deviceId))
            lista.Add(new CeluExtra(deviceId, req?.Nombre, User.Identity?.Name, DateTime.UtcNow));
        var json = System.Text.Json.JsonSerializer.Serialize(lista);
        var fila = await _db.AppSettings.FirstOrDefaultAsync(a => a.Key == KeyCelusExtra);
        if (fila is null) _db.AppSettings.Add(new AppSetting { Key = KeyCelusExtra, Value = json, UpdatedAt = DateTime.UtcNow });
        else { fila.Value = json; fila.UpdatedAt = DateTime.UtcNow; }
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    /// <summary>Ficha de una venta propia para el celu: qué va en el paquete, con foto. Sin plata.</summary>
    private async Task<IActionResult> FichaVentaPropiaAsync(CafeVenta? v)
    {
        if (v is null)
            return Ok(new { ok = false, mensaje = "No encontré la venta de ese QR. Puede ser un comprobante borrado." });

        var items = await _db.CafeVentaItems.AsNoTracking()
            .Where(i => i.VentaId == v.Id)
            .OrderBy(i => i.Id)
            .Select(i => new
            {
                i.ProductoId, i.ProductoNombreSnapshot, i.Cantidad, i.Formato, i.Categoria, i.Molienda,
                i.EsDoyPack, i.EsEnvasePlateado,
                combo = i.ComboOrigenId != null ? _db.Set<CafeCombo>().Where(c => c.Id == i.ComboOrigenId).Select(c => c.Nombre).FirstOrDefault() : null,
                sku = i.ProductoId != null ? _db.CafeProductos.Where(p => p.Id == i.ProductoId).Select(p => p.Sku).FirstOrDefault() : null,
                fotoPropia = i.ProductoId != null ? _db.CafeProductoFotos.Where(f => f.CafeProductoId == i.ProductoId).Select(f => f.FotoPropiaArchivo).FirstOrDefault() : null,
                // misma foto que el tablero de armado: la publicación MeLi activa más nueva de ese producto
                thumb = i.ProductoId != null
                    ? _db.MeliItems.Where(mi => mi.CafeProductoId == i.ProductoId && mi.Thumbnail != null && mi.Status == "active")
                        .OrderByDescending(mi => mi.UpdatedAt).Select(mi => mi.Thumbnail).FirstOrDefault()
                    : null
            })
            .ToListAsync();

        var productos = items.Select(i => new
        {
            titulo = i.ProductoNombreSnapshot,
            cantidad = i.Cantidad,
            sku = i.sku,
            foto = !string.IsNullOrEmpty(i.fotoPropia) ? $"/api/public/producto-foto/img/{i.fotoPropia}" : FotoGrande(i.thumb),
            formato = i.Categoria == "CAFE"
                ? i.Formato switch { "1KG" => "1 kg", "MEDIO" => "½ kg", "CUARTO" => "¼ kg", _ => null }
                : null,
            molienda = i.Molienda,
            envase = i.EsDoyPack ? "Doypack" : i.EsEnvasePlateado ? "Envase plateado" : null,
            combo = i.combo
        }).ToList();

        string tipo = v.Retira ? "Retira en depósito"
            : v.PorTransporte ? ("Transporte" + (string.IsNullOrWhiteSpace(v.TransporteEmpresa) ? "" : " " + v.TransporteEmpresa))
            : "Reparto";
        string estado = v.Estado == "anulado" ? "ANULADA"
            : v.EntregadoAt != null || v.EstadoPreparacion == "ENTREGADO" ? "Entregada"
            : v.EstadoPreparacion switch { "EN_CAMINO" => "En camino", "LISTO" => "Armada", "EN_PREPARACION" => "Armándose", _ => "Para armar" };

        return Ok(new
        {
            ok = true,
            origen = "propia",
            numeroComprobante = v.Numero,
            fecha = DateTime.SpecifyKind(v.CreatedAt, DateTimeKind.Utc),
            comprador = !string.IsNullOrWhiteSpace(v.ClienteNombreSnapshot) ? v.ClienteNombreSnapshot : v.ClienteRazonSocialSnapshot,
            localidad = !string.IsNullOrWhiteSpace(v.ClienteLocalidadSnapshot) ? v.ClienteLocalidadSnapshot : v.ClienteCiudadSnapshot,
            domicilio = !string.IsNullOrWhiteSpace(v.DomicilioEntregaImpreso) ? v.DomicilioEntregaImpreso : v.ClienteDomicilioEntregaSnapshot,
            tipo,
            estado,
            anulada = v.Estado == "anulado",
            bultos = v.CantidadBultos,
            fragil = v.EsFragil,
            comentarioArmado = v.ComentarioArmado,
            unidades = productos.Sum(p => p.cantidad),
            productos
        });
    }

    // ─────────────────────────── AYUDAS ───────────────────────────

    /// <summary>SKU + foto GRANDE de cada publicación. La miniatura guardada suele ser la "-I"
    /// (chiquita); para verla en grande se pide la "-O" (la de ~500 px) que MeLi sirve igual.
    /// Solo 3 columnas: MeliItems entero es pesado.</summary>
    private async Task<Dictionary<string, (string? Sku, string? Foto)>> FotosDeItemsAsync(List<string> itemIds)
    {
        if (itemIds.Count == 0) return new();
        var items = await _db.MeliItems.AsNoTracking()
            .Where(m => itemIds.Contains(m.MeliItemId))
            .Select(m => new { m.MeliItemId, m.Sku, m.Thumbnail })
            .ToListAsync();
        return items
            .GroupBy(m => m.MeliItemId)
            .ToDictionary(g => g.Key, g => (g.First().Sku, FotoGrande(g.First().Thumbnail)));
    }

    private static string? FotoGrande(string? thumb)
    {
        if (string.IsNullOrEmpty(thumb)) return null;
        if (thumb.StartsWith("http://")) thumb = "https://" + thumb.Substring("http://".Length);
        return System.Text.RegularExpressions.Regex.Replace(thumb, "-[A-Z]\\.(jpg|jpeg|webp|png)$", "-O.$1",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// 2026-09-24: despues de imprimir, MeLi no siempre deja "printed": en Correo pasa directo a
    /// "ready_for_pickup" (visto en envios reales), y despues vienen dropped_off, picked_up, etc.
    /// Por eso: listo para despachar y el subestado YA NO es de "falta imprimir" = impresa.
    /// </summary>
    private static bool EtiquetaImpresa(string? status, string? substatus) =>
        status is "shipped" or "delivered" or "not_delivered"
        || (status == "ready_to_ship" && substatus is not (null or "ready_to_print" or "invoice_pending" or "buffered"));

    private static (string Clave, string Etiqueta) TipoEnvio(string? logisticType, string? shippingMode)
        => (logisticType?.ToLowerInvariant()) switch
        {
            "self_service" => ("flex", "Flex"),
            "fulfillment" => ("full", "Full"),
            _ => ("correo", string.Equals(shippingMode, "me1", StringComparison.OrdinalIgnoreCase) ? "Correo (ME1)" : "Correo")
        };

    private static (string Clave, string Etiqueta) EstadoAmigable(string? status, string? shippingStatus)
    {
        if (status == "cancelled" || shippingStatus == "cancelled") return ("cancelada", "Cancelada");
        return shippingStatus switch
        {
            "delivered" => ("entregada", "Entregada"),
            "shipped" => ("camino", "En camino"),
            "not_delivered" => ("camino", "No entregada"),
            "ready_to_ship" or "handling" or "pending" => ("despachar", "Para despachar"),
            _ => ("despachar", "Para despachar")
        };
    }

    /// <summary>Saca el nº de envío de un código escaneado: prioriza el "id" del JSON del QR Flex;
    /// si no, la corrida de dígitos más larga (código de barras de Correo).</summary>
    private static long? ExtractShipmentIdFromCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(code, "\"id\"\\s*:\\s*\"?(\\d+)\"?");
        string digits;
        if (m.Success) digits = m.Groups[1].Value;
        else
        {
            var runs = System.Text.RegularExpressions.Regex.Matches(code, "\\d+");
            digits = runs.Count == 0 ? "" : runs.Cast<System.Text.RegularExpressions.Match>()
                .Select(x => x.Value).OrderByDescending(x => x.Length).First();
        }
        return digits.Length >= 6 && long.TryParse(digits, out var val) ? val : (long?)null;
    }

    /// <summary>Dado un número (envío, pack u orden), junta TODOS los productos del mismo envío
    /// desde la base LOCAL. Lista vacía si no está sincronizado.</summary>
    private async Task<(List<MeliOrder> productos, long? shippingId)> ResolverProductosDeVentaMeliAsync(long num)
    {
        // (1) nº de envío
        var prods = await _db.MeliOrders.AsNoTracking().Where(o => o.ShippingId == num).ToListAsync();
        if (prods.Count > 0) return (prods, num);

        // (2) nº de PACK (carrito) — el que ve el vendedor suele ser este
        var packProds = await _db.MeliOrders.AsNoTracking().Where(o => o.PackId == num).ToListAsync();
        if (packProds.Count > 0)
            return (packProds, packProds.FirstOrDefault(o => o.ShippingId != null)?.ShippingId);

        // (3) nº de venta (order id): su envío + hermanos del mismo envío
        var ord = await _db.MeliOrders.AsNoTracking().FirstOrDefaultAsync(o => o.MeliOrderId == num);
        if (ord is not null)
        {
            if (ord.ShippingId is not null)
                return (await _db.MeliOrders.AsNoTracking().Where(o => o.ShippingId == ord.ShippingId).ToListAsync(), ord.ShippingId);
            return (new List<MeliOrder> { ord }, null);
        }

        // (4) envío conocido en MeliShipments → su orden → hermanos
        var sh = await _db.MeliShipments.AsNoTracking().FirstOrDefaultAsync(s => s.MeliShipmentId == num);
        if (sh?.MeliOrderId is not null)
        {
            var ord2 = await _db.MeliOrders.AsNoTracking().FirstOrDefaultAsync(o => o.MeliOrderId == sh.MeliOrderId);
            if (ord2?.ShippingId is not null)
                return (await _db.MeliOrders.AsNoTracking().Where(o => o.ShippingId == ord2.ShippingId).ToListAsync(), ord2.ShippingId);
            if (ord2 is not null) return (new List<MeliOrder> { ord2 }, null);
        }

        return (new List<MeliOrder>(), null);
    }

    /// <summary>Trae de MeLi EN VIVO una venta que no está en la base, probando el número como
    /// orden, pack y envío. Best-effort: si falla, después simplemente no se encuentra.</summary>
    private async Task TraerVentaMeliEnVivoAsync(long num)
    {
        var account = await _db.MeliAccounts.OrderBy(a => a.Id).FirstOrDefaultAsync();
        if (account is null) return;

        using var scope = _scopeFactory.CreateScope();
        var orderSvc = scope.ServiceProvider.GetRequiredService<MeliOrderService>();
        var shipSvc = scope.ServiceProvider.GetRequiredService<MeliShipmentService>();

        try { await orderSvc.SyncSingleOrderAsync(num, account); } catch { }
        try { await orderSvc.SyncPackAsync(num, account); } catch { }
        try
        {
            await shipSvc.SyncSingleShipmentAsync(num);
            var sh = await _db.MeliShipments.FirstOrDefaultAsync(s => s.MeliShipmentId == num);
            if (sh?.MeliOrderId is not null)
            {
                try { await orderSvc.SyncSingleOrderAsync(sh.MeliOrderId.Value, account); } catch { }
            }
        }
        catch { }
    }
}
