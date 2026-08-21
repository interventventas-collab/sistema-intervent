using Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-08-21: MODO MELI — todo lo que sabemos de un comprador de MercadoLibre, junto, para
/// resolverle por WhatsApp sin salir del chat. Pedido del dueño: "si me escribe alguien
/// preguntando por una publicación o porque compró, pongo su usuario y me aparece toda su
/// información para resolver".
///
/// No trae nada nuevo de MeLi: junta lo que ya tenemos guardado (base propia de clientes,
/// órdenes, envíos y publicaciones) y lo traduce a castellano.
/// </summary>
[ApiController]
[Route("api/meli/ficha")]
[Authorize]
public class MeliFichaController : ControllerBase
{
    private readonly AppDbContext _db;
    public MeliFichaController(AppDbContext db) { _db = db; }

    // Argentina no cambia de hora desde 2009: UTC-3 fijo (igual criterio que el front).
    private static DateTime Ar(DateTime utc) => utc.AddHours(-3);
    private static DateTime? Ar(DateTime? utc) => utc.HasValue ? utc.Value.AddHours(-3) : null;

    public record MatchDto(
        long BuyerId, string? Nickname, string? Nombre, string? Telefono, string? Ciudad, string? Provincia,
        int Compras, decimal TotalGastado, DateTime? UltimaCompra, string? UltimoItem, string PorQue);

    public record CompraDto(
        long OrderId, long? PackId, DateTime? Fecha, string Items, int Cantidad, decimal Total,
        string? Cuenta, string EstadoTexto, string? Seguimiento, string? TipoEnvio,
        DateTime? Entregado, DateTime? EstimadaHasta, string? Thumbnail, string? Permalink,
        string? ItemId, string RespuestaSugerida,
        // 2026-08-21: dónde se entrega, quién lo llevó (Flex) y el punto en el mapa.
        string? DomicilioEntrega, string? ComentarioEntrega, string? MapsLink,
        string? EntregadoPor, string? RepartidorAsignado);

    /// <summary>Una pregunta que hizo este usuario en alguna de nuestras publicaciones.</summary>
    public record PreguntaDto(
        int Id, string ItemId, string? ItemTitulo, string? Thumbnail, string Texto,
        string? Respuesta, bool Contestada, DateTime Fecha, DateTime? FechaRespuesta,
        string? Cuenta, string? Permalink, decimal? Precio, int? Stock, string? EstadoPubli);

    /// <summary>La publicación como está AHORA (precio, stock, activa o pausada).</summary>
    public record PublicacionDto(
        string ItemId, string Titulo, decimal Precio, int Stock, string EstadoTexto,
        string? Sku, string? Thumbnail, string? Permalink, string? Cuenta, string? TipoEnvio,
        int Vendidos, string? RespuestaSugerida);

    public record FichaDto(
        long BuyerId, string? Nickname, string? Nombre, string? Telefono, string? Direccion,
        string? Ciudad, string? Provincia, string? CodigoPostal, int Compras, decimal TotalGastado,
        DateTime? PrimeraCompra, DateTime? UltimaCompra,
        int? ClienteVinculadoId, string? ClienteVinculadoNombre,
        List<CompraDto> UltimasCompras, string? Aviso,
        // 2026-08-21 (parte 2)
        List<PreguntaDto> Preguntas, string? PerfilUrl);

    /// <summary>Las columnas de la publicación que usamos acá (proyección, no la fila entera).</summary>
    private record PubliFila(string MeliItemId, string Title, decimal Price, int AvailableQuantity,
        int SoldQuantity, string Status, string? Sku, string? Thumbnail, string? Permalink,
        string? LogisticType, int MeliAccountId);

    /// <summary>El cartelito de arriba del chat: quién es y qué tiene pendiente.</summary>
    public record AvisoDto(long BuyerId, string? Nickname, int Compras, DateTime? UltimaCompra,
        int PedidosEnCurso, int PreguntasSinContestar, bool Guardado, string Texto);

    /// <summary>Un usuario de MeLi ya atado a un teléfono de WhatsApp.</summary>
    public record UsuarioAtadoDto(long BuyerId, string? Nickname, DateTime CreatedAt, string? CreatedBy);

    /// <summary>
    /// Buscador de una sola caja: acepta el usuario de MeLi, el nombre del que recibe, el
    /// teléfono, el número de venta (o de pack/envío) y el código de la publicación (MLA...).
    /// </summary>
    [HttpGet("buscar")]
    public async Task<IActionResult> Buscar([FromQuery] string? q, [FromQuery] int limit = 12)
    {
        var texto = (q ?? "").Trim();
        if (texto.Length < 3) return Ok(new List<MatchDto>());
        limit = Math.Clamp(limit, 1, 50);

        // buyerId -> por qué apareció (se lo mostramos al operador para que entienda el resultado)
        var encontrados = new Dictionary<long, string>();

        // 1) Código de publicación: quiénes compraron ESA publicación.
        if (texto.StartsWith("ML", StringComparison.OrdinalIgnoreCase) && texto.Length >= 6)
        {
            var itemId = texto.Replace(" ", "").ToUpperInvariant();
            var buyers = await _db.MeliOrders.Where(o => o.ItemId == itemId)
                .Select(o => o.BuyerId).Distinct().Take(limit).ToListAsync();
            foreach (var b in buyers) encontrados.TryAdd(b, $"compró la publicación {itemId}");
        }

        var digitos = new string(texto.Where(char.IsDigit).ToArray());

        // 2) Número de venta / pack / envío pegado por el cliente.
        if (digitos.Length >= 9 && long.TryParse(digitos, out var num))
        {
            var b = await _db.MeliOrders
                .Where(o => o.MeliOrderId == num || o.PackId == num || o.ShippingId == num)
                .Select(o => o.BuyerId).FirstOrDefaultAsync();
            if (b != 0) encontrados.TryAdd(b, $"es el comprador de la venta {num}");
        }

        // 3) Teléfono: comparamos por los últimos 8 dígitos (así no molesta el 54 9 11 ni los guiones).
        if (digitos.Length >= 8)
        {
            var cola = digitos.Substring(digitos.Length - 8);
            var porTel = await _db.MeliClientes
                .Where(c => c.Phone != null && EF.Functions.Like(c.Phone, $"%{cola}%"))
                .OrderByDescending(c => c.LastPurchaseAt).Take(limit)
                .Select(c => c.BuyerId).ToListAsync();
            foreach (var b in porTel) encontrados.TryAdd(b, "coincide el teléfono");
        }

        // 4) Usuario de MeLi o nombre del que recibe, en la base propia de clientes.
        var porTexto = await _db.MeliClientes
            .Where(c => (c.Nickname != null && EF.Functions.Like(c.Nickname, $"%{texto}%"))
                     || (c.ReceiverName != null && EF.Functions.Like(c.ReceiverName, $"%{texto}%")))
            .OrderByDescending(c => c.LastPurchaseAt).Take(limit)
            .Select(c => c.BuyerId).ToListAsync();
        foreach (var b in porTexto) encontrados.TryAdd(b, "coincide el usuario o el nombre");

        // 5) Último intento: el usuario tal como quedó escrito en las ventas (compradores que
        //    todavía no entraron a la base propia).
        if (encontrados.Count == 0)
        {
            var porOrden = await _db.MeliOrders
                .Where(o => EF.Functions.Like(o.BuyerNickname, $"%{texto}%"))
                .Select(o => o.BuyerId).Distinct().Take(limit).ToListAsync();
            foreach (var b in porOrden) encontrados.TryAdd(b, "coincide el usuario en una venta");
        }

        if (encontrados.Count == 0) return Ok(new List<MatchDto>());

        var ids = encontrados.Keys.ToList();
        var clientes = await _db.MeliClientes.Where(c => ids.Contains(c.BuyerId)).ToListAsync();

        // Compradores que no están en la base propia: los armamos con lo que diga su última venta.
        var faltantes = ids.Where(id => clientes.All(c => c.BuyerId != id)).ToList();
        var desdeOrden = await _db.MeliOrders
            .Where(o => faltantes.Contains(o.BuyerId))
            .GroupBy(o => o.BuyerId)
            .Select(g => new
            {
                BuyerId = g.Key,
                Nickname = g.OrderByDescending(o => o.DateCreated).Select(o => o.BuyerNickname).FirstOrDefault(),
                Ultima = g.Max(o => o.DateCreated),
                UltimoItem = g.OrderByDescending(o => o.DateCreated).Select(o => o.ItemTitle).FirstOrDefault()
            })
            .ToListAsync();

        var res = new List<MatchDto>();
        foreach (var c in clientes)
            res.Add(new MatchDto(c.BuyerId, c.Nickname, c.ReceiverName, c.Phone, c.City, c.State,
                c.OrdersCount, c.TotalSpent, Ar(c.LastPurchaseAt), c.LastItems, encontrados[c.BuyerId]));
        foreach (var o in desdeOrden)
            res.Add(new MatchDto(o.BuyerId, o.Nickname, null, null, null, null,
                0, 0m, Ar(o.Ultima), o.UltimoItem, encontrados[o.BuyerId]));

        return Ok(res.OrderByDescending(x => x.UltimaCompra ?? DateTime.MinValue).Take(limit).ToList());
    }

    /// <summary>La ficha completa de un comprador: sus datos + las últimas compras con el estado
    /// del envío en castellano y una respuesta ya escrita para pegarle en el chat.</summary>
    [HttpGet("{buyerId:long}")]
    public async Task<IActionResult> Ficha(long buyerId, [FromQuery] int limitCompras = 8)
    {
        limitCompras = Math.Clamp(limitCompras, 1, 30);

        var cliente = await _db.MeliClientes.FirstOrDefaultAsync(c => c.BuyerId == buyerId);

        // Traemos los renglones de las últimas ventas (MeLi guarda UNA fila por producto).
        var filas = await _db.MeliOrders
            .Where(o => o.BuyerId == buyerId)
            .OrderByDescending(o => o.DateCreated)
            .Take(limitCompras * 8)
            .ToListAsync();

        // MercadoLibre parte un carrito de varios productos en VARIAS ventas con el mismo "pack".
        // Para el que atiende eso es UNA sola compra (un paquete, un envío), así que las juntamos.
        var grupos = filas.GroupBy(o => o.PackId ?? o.MeliOrderId)
            .OrderByDescending(g => g.Max(o => o.DateCreated))
            .Take(limitCompras).ToList();

        var cuentas = await _db.MeliAccounts.ToDictionaryAsync(a => a.Id, a => a.Nickname);

        var envioIds = grupos.Select(g => g.First().ShippingId).Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        List<Api.Models.MeliShipment> envios = envioIds.Count == 0
            ? new List<Api.Models.MeliShipment>()
            : await _db.MeliShipments.Where(s => envioIds.Contains(s.MeliShipmentId)).ToListAsync();

        var itemIds = grupos.SelectMany(g => g.Select(o => o.ItemId)).Distinct().ToList();
        var publis = await _db.MeliItems
            .Where(i => itemIds.Contains(i.MeliItemId))
            .Select(i => new { i.MeliItemId, i.Thumbnail, i.Permalink })
            .ToListAsync();

        // 2026-08-21: quién llevó el paquete cuando es Flex/ME1 (lo entregamos nosotros).
        var repIds = envios.SelectMany(e => new[] { e.RepartidorAsignadoId, e.EntregadoPorRepartidorId })
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToList();
        var repartidores = repIds.Count == 0
            ? new Dictionary<int, string>()
            : await _db.CafeRepartidores.Where(r => repIds.Contains(r.Id))
                .ToDictionaryAsync(r => r.Id, r => r.Nombre);

        var compras = new List<CompraDto>();
        var yaAgrupadas = new HashSet<long>();   // ventas ya mostradas (incluidas las del mismo pack)
        foreach (var g in grupos)
        {
            var head = g.OrderBy(o => o.Id).First();
            var envio = envios.FirstOrDefault(s => head.ShippingId.HasValue && s.MeliShipmentId == head.ShippingId.Value);
            var publi = publis.FirstOrDefault(p => p.MeliItemId == head.ItemId);

            var items = string.Join(" + ", g.Select(o => o.Quantity > 1 ? $"{o.Quantity}× {o.ItemTitle}" : o.ItemTitle));
            // Para el mensaje que se le manda al cliente, el título entero queda larguísimo:
            // va cortado, y si el paquete trae varias cosas se dice cuántas.
            var titulos = g.Select(o => o.ItemTitle).Distinct().ToList();
            var comoNombrarlo = titulos.Count > 1
                ? $"tu pedido de {titulos.Count} productos"
                : $"tu pedido de {Recortar(titulos.FirstOrDefault() ?? "", 55)}";
            // El importe de cada venta viene repetido en cada renglón: se suma UNA vez por venta.
            var total = g.GroupBy(o => o.MeliOrderId).Sum(x => x.First().TotalAmount);
            var cancelada = string.Equals(head.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
            var estado = envio?.Status ?? head.ShippingStatus;
            var subestado = envio?.Substatus ?? head.ShippingSubstatus;
            var estadoTexto = cancelada ? "🚫 Venta cancelada" : EstadoEnCastellano(estado, subestado);
            var entregado = Ar(envio?.DateDelivered);
            var estimada = Ar(envio?.EstimatedDeliveryFinal ?? envio?.EstimatedDeliveryLimit);

            // Domicilio: SIEMPRE el del envío (es el que vale), y si no hay envío el último
            // que le conocemos al comprador.
            var domicilio = DomicilioDe(envio) ?? DomicilioDe(cliente);
            var maps = (envio?.Latitude is decimal la && envio?.Longitude is decimal lo)
                ? $"https://www.google.com/maps/search/?api=1&query={la.ToString(Inv)},{lo.ToString(Inv)}"
                : null;
            string? entregadoPor = null;
            if (envio?.EntregadoPorRepartidorId is int epr && repartidores.TryGetValue(epr, out var nEpr))
            {
                var cuando = Ar(envio.EntregadoPorRepartidorAt ?? envio.DateDelivered);
                entregadoPor = cuando.HasValue ? $"{nEpr} — {cuando.Value:dd/MM HH:mm}" : nEpr;
            }
            string? asignado = null;
            if (envio?.RepartidorAsignadoId is int rap && repartidores.TryGetValue(rap, out var nRap)
                && envio.EntregadoPorRepartidorId is null)
                asignado = nRap;

            compras.Add(new CompraDto(
                head.MeliOrderId, head.PackId, Ar(head.DateCreated), items,
                g.Sum(o => o.Quantity), total,
                cuentas.TryGetValue(head.MeliAccountId, out var cn) ? cn : null,
                estadoTexto, envio?.TrackingNumber,
                TipoEnvio(head.LogisticType ?? envio?.LogisticType, head.ShippingMode),
                entregado, estimada, publi?.Thumbnail, publi?.Permalink, head.ItemId,
                RespuestaSugerida(cancelada, estado, subestado, comoNombrarlo, envio?.TrackingNumber, entregado, estimada),
                domicilio, envio?.Comment, maps, entregadoPor, asignado));
            foreach (var o in g) yaAgrupadas.Add(o.MeliOrderId);
        }

        // Compras viejas que ya no están en las órdenes (quedan guardadas aparte para siempre).
        var yaListadas = yaAgrupadas;
        var viejas = await _db.MeliClienteCompras
            .Where(c => c.BuyerId == buyerId)
            .OrderByDescending(c => c.Fecha)
            .Take(limitCompras).ToListAsync();
        foreach (var v in viejas.Where(v => !yaListadas.Contains(v.MeliOrderId)))
        {
            if (compras.Count >= limitCompras) break;
            compras.Add(new CompraDto(
                v.MeliOrderId, v.PackId, Ar(v.Fecha), v.Items ?? "—", v.Cantidad, v.Total,
                null, "📄 Compra vieja (sin detalle del envío)", null, v.Canal, null, null, null, null, null,
                "Hola! Ya tengo tu compra a la vista, contame en qué te ayudo 😊",
                DomicilioDe(cliente), null, null, null, null));
        }
        compras = compras.OrderByDescending(c => c.Fecha ?? DateTime.MinValue).ToList();

        var vinculado = await _db.CafeClientes
            .Where(c => c.MeliBuyerId == buyerId)
            .Select(c => new { c.Id, c.Nombre }).FirstOrDefaultAsync();

        // 2026-08-21 (parte 2): las preguntas que hizo este usuario en nuestras publicaciones,
        // con el precio y el stock de HOY de cada publicación (que es lo que hay que contestarle).
        var preguntas = await PreguntasDeAsync(buyerId, 8);

        string? aviso = null;
        if (compras.Count == 0 && preguntas.Count > 0)
            aviso = "Todavía no nos compró: solo preguntó en las publicaciones.";
        else if (compras.Count == 0)
            aviso = "No encontré compras ni preguntas de este usuario en nuestras cuentas.";
        else if (string.IsNullOrWhiteSpace(cliente?.Phone))
            aviso = "MercadoLibre no nos da el teléfono de las ventas por correo: solo aparece en las que entregamos nosotros (Flex/ME1).";

        var nickname = cliente?.Nickname ?? filas.FirstOrDefault()?.BuyerNickname
                       ?? await _db.MeliQuestions.Where(q => q.FromUserId == buyerId)
                              .OrderByDescending(q => q.DateCreated)
                              .Select(q => q.FromNickname).FirstOrDefaultAsync();
        var ficha = new FichaDto(
            buyerId, nickname, cliente?.ReceiverName, cliente?.Phone, cliente?.AddressLine,
            cliente?.City, cliente?.State, cliente?.ZipCode,
            cliente?.OrdersCount ?? compras.Count, cliente?.TotalSpent ?? compras.Sum(c => c.Total),
            Ar(cliente?.FirstPurchaseAt), Ar(cliente?.LastPurchaseAt ?? filas.FirstOrDefault()?.DateCreated),
            vinculado?.Id, vinculado?.Nombre, compras, aviso,
            preguntas, PerfilUrl(nickname));

        return Ok(ficha);
    }

    /// <summary>Deja atado este comprador de MeLi al cliente del sistema, así la próxima vez que
    /// escriba lo reconocemos solo.</summary>
    [HttpPost("{buyerId:long}/vincular/{clienteId:int}")]
    public async Task<IActionResult> Vincular(long buyerId, int clienteId, [FromQuery] string? nickname = null)
    {
        var cli = await _db.CafeClientes.FindAsync(clienteId);
        if (cli is null) return NotFound(new { error = "No encontré ese cliente" });

        var otro = await _db.CafeClientes.FirstOrDefaultAsync(c => c.MeliBuyerId == buyerId && c.Id != clienteId);
        if (otro is not null)
            return Conflict(new { error = $"Ese usuario de MercadoLibre ya está vinculado al cliente \"{otro.Nombre}\"." });

        cli.MeliBuyerId = buyerId;
        cli.MeliNickname = string.IsNullOrWhiteSpace(nickname)
            ? (await _db.MeliClientes.Where(c => c.BuyerId == buyerId).Select(c => c.Nickname).FirstOrDefaultAsync())
            : nickname.Trim();
        cli.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, clienteId = cli.Id, clienteNombre = cli.Nombre, nickname = cli.MeliNickname });
    }

    /// <summary>Las preguntas que hizo este usuario en nuestras publicaciones, con el precio y
    /// el stock de HOY (que es lo que hay que contestarle).</summary>
    private async Task<List<PreguntaDto>> PreguntasDeAsync(long buyerId, int limite)
    {
        var qs = await _db.MeliQuestions.Include(q => q.MeliAccount)
            .Where(q => q.FromUserId == buyerId)
            .OrderByDescending(q => q.DateCreated)
            .Take(limite).ToListAsync();
        if (qs.Count == 0) return new List<PreguntaDto>();

        // OJO: proyección, NO la entidad entera. El modelo tiene columnas (SaleFee*) que en
        // algunas bases todavía no existen y traer la fila completa revienta con "Invalid column name".
        var ids = qs.Select(q => q.ItemId).Distinct().ToList();
        var items = await _db.MeliItems.Where(i => ids.Contains(i.MeliItemId))
            .Select(i => new PubliFila(i.MeliItemId, i.Title, i.Price, i.AvailableQuantity, i.SoldQuantity,
                i.Status, i.Sku, i.Thumbnail, i.Permalink, i.LogisticType, i.MeliAccountId))
            .ToListAsync();

        return qs.Select(q =>
        {
            // Una publicación puede tener varias filas (variantes): el stock es la suma.
            var filas = items.Where(i => i.MeliItemId == q.ItemId).ToList();
            var first = filas.FirstOrDefault();
            return new PreguntaDto(
                q.Id, q.ItemId, first?.Title ?? q.ItemTitle,
                string.IsNullOrWhiteSpace(first?.Thumbnail) ? q.ItemThumbnail : first!.Thumbnail,
                q.Text, q.AnswerText,
                string.Equals(q.Status, "ANSWERED", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(q.AnswerText),
                Ar(q.DateCreated), Ar(q.DateAnswered),
                q.MeliAccount?.Nickname,
                first?.Permalink ?? $"https://articulo.mercadolibre.com.ar/{q.ItemId}",
                first?.Price, filas.Count == 0 ? null : filas.Sum(i => i.AvailableQuantity),
                first is null ? null : EstadoPublicacion(first.Status));
        }).ToList();
    }

    /// <summary>La publicación como está AHORA. Se usa cuando el que escribe pregunta por una
    /// publicación puntual (pegó el código MLA… o se toca desde una de sus preguntas).</summary>
    [HttpGet("publicacion/{itemId}")]
    public async Task<IActionResult> Publicacion(string itemId)
    {
        itemId = (itemId ?? "").Trim().ToUpperInvariant();
        var filas = await _db.MeliItems
            .Where(i => i.MeliItemId == itemId)
            .Select(i => new PubliFila(i.MeliItemId, i.Title, i.Price, i.AvailableQuantity, i.SoldQuantity,
                i.Status, i.Sku, i.Thumbnail, i.Permalink, i.LogisticType, i.MeliAccountId))
            .ToListAsync();
        if (filas.Count == 0) return NotFound(new { error = "Esa publicación no está en el sistema." });

        var first = filas[0];
        var cuenta = await _db.MeliAccounts.Where(a => a.Id == first.MeliAccountId)
            .Select(a => a.Nickname).FirstOrDefaultAsync();
        var stock = filas.Sum(i => i.AvailableQuantity);
        var activa = string.Equals(first.Status, "active", StringComparison.OrdinalIgnoreCase);

        // Respuesta ya escrita para el que pregunta "¿tenés?".
        string sugerida;
        if (activa && stock > 0)
            sugerida = $"Hola! Sí, tenemos {Recortar(first.Title, 55)}. Está publicado a {Plata(first.Price)} y hay stock 😊";
        else if (activa)
            sugerida = $"Hola! {Recortar(first.Title, 55)} está publicado pero justo me quedé sin stock. Te aviso apenas entre 😊";
        else
            sugerida = $"Hola! Esa publicación está pausada por ahora. Contame qué necesitás y te paso precio y disponibilidad 😊";

        return Ok(new PublicacionDto(
            first.MeliItemId, first.Title, first.Price, stock, EstadoPublicacion(first.Status),
            first.Sku, first.Thumbnail, first.Permalink, cuenta,
            TipoEnvio(first.LogisticType, null), filas.Sum(i => i.SoldQuantity), sugerida));
    }

    /// <summary>2026-08-21 (parte 3): el cartelito de arriba del chat. Dice de una si el que
    /// escribe es un comprador de MercadoLibre — porque ya lo dejamos guardado en el teléfono
    /// (📌) o porque el teléfono coincide con el de una venta Flex/ME1 — y si tiene algún
    /// pedido en camino. Se pide en cada chat que se abre, así que es a propósito liviano.</summary>
    [HttpGet("aviso")]
    public async Task<IActionResult> Aviso([FromQuery] string numero)
    {
        var num = (numero ?? "").Trim();
        if (num.Length == 0) return Ok(null);

        long? buyerId = null;
        var guardado = false;

        // 1) ¿Ya lo dejamos guardado en este teléfono? Es lo más confiable.
        var atado = await _db.WhatsAppMeliUsuarios.Where(x => x.Numero == num)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        if (atado is not null) { buyerId = atado.BuyerId; guardado = true; }

        // 2) Si no, probamos por teléfono (solo lo tenemos en las ventas Flex/ME1).
        if (buyerId is null)
        {
            var digitos = new string(num.Where(char.IsDigit).ToArray());
            if (digitos.Length >= 8)
            {
                var cola = digitos.Substring(digitos.Length - 8);
                var porTel = await _db.MeliClientes
                    .Where(c => c.Phone != null && EF.Functions.Like(c.Phone, $"%{cola}%"))
                    .OrderByDescending(c => c.LastPurchaseAt)
                    .Select(c => new { c.BuyerId }).FirstOrDefaultAsync();
                if (porTel is not null) buyerId = porTel.BuyerId;
            }
        }

        if (buyerId is null) return Ok(null);

        var cli = await _db.MeliClientes.FirstOrDefaultAsync(c => c.BuyerId == buyerId.Value);
        var nickname = cli?.Nickname
            ?? atado?.Nickname
            ?? await _db.MeliOrders.Where(o => o.BuyerId == buyerId.Value)
                   .OrderByDescending(o => o.DateCreated).Select(o => o.BuyerNickname).FirstOrDefaultAsync();

        var compras = cli?.OrdersCount ?? 0;
        if (compras == 0)
            compras = await _db.MeliOrders.Where(o => o.BuyerId == buyerId.Value)
                .Select(o => o.PackId ?? o.MeliOrderId).Distinct().CountAsync();

        // Pedidos que todavía están en la calle (o sin despachar): es lo primero que pregunta.
        var desde = DateTime.UtcNow.AddDays(-60);
        var enCurso = await _db.MeliOrders
            .Where(o => o.BuyerId == buyerId.Value && o.DateCreated >= desde
                        && o.Status != "cancelled"
                        && o.ShippingStatus != null
                        && o.ShippingStatus != "delivered" && o.ShippingStatus != "not_delivered"
                        && o.ShippingStatus != "cancelled")
            .Select(o => o.PackId ?? o.MeliOrderId).Distinct().CountAsync();

        var preguntasSinContestar = await _db.MeliQuestions
            .CountAsync(q => q.FromUserId == buyerId.Value && q.Status == "UNANSWERED");

        var quien = string.IsNullOrWhiteSpace(nickname) ? "un comprador de MercadoLibre" : nickname!;
        string texto;
        if (compras > 1) texto = $"{quien} — te compró {compras} veces por MercadoLibre";
        else if (compras == 1) texto = $"{quien} — te compró una vez por MercadoLibre";
        else texto = $"{quien} — preguntó por MercadoLibre";
        if (enCurso > 0) texto += enCurso == 1 ? " · 1 pedido en camino" : $" · {enCurso} pedidos en camino";
        if (preguntasSinContestar > 0)
            texto += preguntasSinContestar == 1 ? " · 1 pregunta sin contestar" : $" · {preguntasSinContestar} preguntas sin contestar";

        return Ok(new AvisoDto(buyerId.Value, nickname, compras, Ar(cli?.LastPurchaseAt),
            enCurso, preguntasSinContestar, guardado, texto));
    }

    /// <summary>Los usuarios de MercadoLibre ya atados a ESTE teléfono de WhatsApp.</summary>
    [HttpGet("por-telefono")]
    public async Task<IActionResult> PorTelefono([FromQuery] string numero)
    {
        var num = (numero ?? "").Trim();
        if (num.Length == 0) return Ok(new List<UsuarioAtadoDto>());
        var lista = await _db.WhatsAppMeliUsuarios
            .Where(x => x.Numero == num)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new UsuarioAtadoDto(x.BuyerId, x.Nickname, x.CreatedAt, x.CreatedBy))
            .ToListAsync();
        return Ok(lista);
    }

    /// <summary>Deja el usuario de MeLi atado a este teléfono: la próxima vez que escriba, la
    /// ficha se abre sola. Es aparte del vínculo con el cliente del sistema porque el que
    /// pregunta por una publicación muchas veces todavía no es cliente nuestro.</summary>
    [HttpPost("{buyerId:long}/asociar-telefono")]
    public async Task<IActionResult> AsociarTelefono(long buyerId, [FromQuery] string numero,
        [FromQuery] string? nickname = null, [FromQuery] string? quien = null)
    {
        var num = (numero ?? "").Trim();
        if (num.Length == 0) return BadRequest(new { error = "Falta el número de WhatsApp" });

        var ya = await _db.WhatsAppMeliUsuarios.FirstOrDefaultAsync(x => x.Numero == num && x.BuyerId == buyerId);
        var nick = string.IsNullOrWhiteSpace(nickname)
            ? await _db.MeliClientes.Where(c => c.BuyerId == buyerId).Select(c => c.Nickname).FirstOrDefaultAsync()
            : nickname.Trim();
        if (ya is not null)
        {
            ya.Nickname = nick ?? ya.Nickname;
            await _db.SaveChangesAsync();
            return Ok(new { ok = true, yaEstaba = true, buyerId, nickname = ya.Nickname });
        }

        _db.WhatsAppMeliUsuarios.Add(new Api.Models.WhatsAppMeliUsuario
        {
            Numero = num, BuyerId = buyerId, Nickname = nick,
            CreatedBy = string.IsNullOrWhiteSpace(quien) ? User?.Identity?.Name : quien.Trim()
        });
        await _db.SaveChangesAsync();
        return Ok(new { ok = true, yaEstaba = false, buyerId, nickname = nick });
    }

    /// <summary>Suelta el usuario de MeLi de este teléfono (se ató por error o cambió de dueño).</summary>
    [HttpDelete("{buyerId:long}/asociar-telefono")]
    public async Task<IActionResult> DesasociarTelefono(long buyerId, [FromQuery] string numero)
    {
        var num = (numero ?? "").Trim();
        var fila = await _db.WhatsAppMeliUsuarios.FirstOrDefaultAsync(x => x.Numero == num && x.BuyerId == buyerId);
        if (fila is null) return Ok(new { ok = true });
        _db.WhatsAppMeliUsuarios.Remove(fila);
        await _db.SaveChangesAsync();
        return Ok(new { ok = true });
    }

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    // OJO: dentro del contenedor la cultura es la invariante, así que la plata se formatea
    // SIEMPRE pidiendo es-AR a mano (si no sale "$25,570" en vez de "$25.570").
    private static readonly System.Globalization.CultureInfo EsAr = System.Globalization.CultureInfo.GetCultureInfo("es-AR");
    private static string Plata(decimal v) => "$" + v.ToString("N0", EsAr);

    /// <summary>El domicilio de entrega del envío, armado en una línea.</summary>
    private static string? DomicilioDe(Api.Models.MeliShipment? e)
    {
        if (e is null) return null;
        var calle = !string.IsNullOrWhiteSpace(e.AddressLine)
            ? e.AddressLine
            : string.Join(" ", new[] { e.StreetName, e.StreetNumber }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var partes = new[] { calle, e.Neighborhood, e.City, e.State, string.IsNullOrWhiteSpace(e.ZipCode) ? null : $"CP {e.ZipCode}" }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToList();
        return partes.Count == 0 ? null : string.Join(", ", partes);
    }

    /// <summary>El último domicilio conocido del comprador (cuando la venta no tiene envío propio).</summary>
    private static string? DomicilioDe(Api.Models.MeliCliente? c)
    {
        if (c is null) return null;
        var partes = new[] { c.AddressLine, c.Neighborhood, c.City, c.State, string.IsNullOrWhiteSpace(c.ZipCode) ? null : $"CP {c.ZipCode}" }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x!.Trim()).ToList();
        return partes.Count == 0 ? null : string.Join(", ", partes);
    }

    private static string EstadoPublicacion(string? status) => (status ?? "").ToLowerInvariant() switch
    {
        "active" => "✅ Activa",
        "paused" => "⏸️ Pausada",
        "closed" => "🚫 Finalizada",
        "under_review" => "🔍 En revisión",
        "inactive" => "💤 Inactiva",
        _ => status ?? "—"
    };

    private static string? PerfilUrl(string? nickname)
        => string.IsNullOrWhiteSpace(nickname) ? null : $"https://perfil.mercadolibre.com.ar/{Uri.EscapeDataString(nickname)}";

    /// <summary>El estado del envío como lo diría una persona, no como lo dice MercadoLibre.</summary>
    private static string EstadoEnCastellano(string? estado, string? subestado)
    {
        var e = (estado ?? "").ToLowerInvariant();
        var s = (subestado ?? "").ToLowerInvariant();
        if (string.IsNullOrEmpty(e)) return "🏠 Sin envío (retira o acuerda con el vendedor)";
        return e switch
        {
            "delivered" => "✅ Entregado",
            "not_delivered" when s.Contains("returning") => "↩️ No se pudo entregar — vuelve a nosotros",
            "not_delivered" => "❌ No se pudo entregar",
            "shipped" when s == "out_for_delivery" => "🚚 Salió a entregar hoy",
            "shipped" => "📦 En camino",
            "ready_to_ship" when s.Contains("print") => "🖨️ Listo, falta despacharlo",
            "ready_to_ship" => "📋 Preparado para despachar",
            "handling" => "🕗 Lo estamos preparando",
            "pending" => "🕗 Recién entrado, todavía sin preparar",
            "cancelled" => "🚫 Envío cancelado",
            _ => $"ℹ️ {estado}"
        };
    }

    private static string? TipoEnvio(string? logistic, string? modo)
    {
        var l = (logistic ?? "").ToLowerInvariant();
        if (l == "self_service") return "Flex (lo entregamos nosotros)";
        if (l == "xd_drop_off" || l == "drop_off") return "Correo (lo despachamos)";
        if (l == "cross_docking") return "Correo (pasan a buscarlo)";
        if (l == "fulfillment") return "Full (lo manda MercadoLibre)";
        if (l == "custom") return "Acordado con el comprador";
        return string.IsNullOrWhiteSpace(modo) ? null : modo;
    }

    /// <summary>Una respuesta ya escrita para pegarle en el chat, según cómo viene el pedido.</summary>
    private static string RespuestaSugerida(bool cancelada, string? estado, string? subestado,
        string comoNombrarlo, string? seguimiento, DateTime? entregado, DateTime? estimada)
    {
        var que = string.IsNullOrWhiteSpace(comoNombrarlo) ? "tu pedido" : comoNombrarlo;
        if (cancelada)
            return $"Hola! Veo que {que} figura como cancelado en MercadoLibre. Si querés lo volvemos a armar, avisame 😊";

        var e = (estado ?? "").ToLowerInvariant();
        var s = (subestado ?? "").ToLowerInvariant();
        var track = string.IsNullOrWhiteSpace(seguimiento) ? "" : $" El número de seguimiento es {seguimiento}.";
        var eta = estimada.HasValue ? $" Según MercadoLibre llega alrededor del {estimada.Value:dd/MM}." : "";

        return e switch
        {
            "delivered" => entregado.HasValue
                ? $"Hola! {Mayus(que)} figura entregado el {entregado.Value:dd/MM} 😊 Si no lo recibiste, avisame y lo reclamamos."
                : $"Hola! {Mayus(que)} figura como entregado 😊 Si no lo recibiste, avisame y lo reclamamos.",
            "not_delivered" => $"Hola! El correo no pudo entregar {que} y está volviendo. Ya lo estoy revisando y te aviso apenas lo tenga resuelto.",
            "shipped" when s == "out_for_delivery" => $"Hola! {Mayus(que)} salió a entregar hoy, tendría que llegarte en el día.{track}",
            "shipped" => $"Hola! {Mayus(que)} ya está en camino.{track}{eta}",
            "ready_to_ship" => $"Hola! {Mayus(que)} ya está preparado y sale en el próximo despacho.{eta}",
            "handling" or "pending" => $"Hola! Estamos preparando {que}, sale en las próximas horas y te paso el seguimiento apenas lo despachemos.",
            "" or null => $"Hola! Tengo {que} a la vista, contame en qué te ayudo 😊",
            _ => $"Hola! Estoy mirando {que} y te confirmo en un ratito 😊"
        };
    }

    /// <summary>Corta un título largo sin partir una palabra al medio.</summary>
    private static string Recortar(string t, int max)
    {
        t = (t ?? "").Trim();
        if (t.Length <= max) return t;
        var corte = t.LastIndexOf(' ', Math.Min(max, t.Length - 1));
        if (corte < max / 2) corte = max;
        return t.Substring(0, corte).TrimEnd(',', '.', ' ') + "…";
    }

    private static string Mayus(string t) => string.IsNullOrEmpty(t) ? t : char.ToUpper(t[0]) + t.Substring(1);
}
