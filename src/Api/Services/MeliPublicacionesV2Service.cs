using Api.Data;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-08-25 — Datos para la pantalla NUEVA de publicaciones (/publicaciones-nueva).
///
/// Es un servicio APARTE a propósito: la pantalla vieja (Publicaciones.razor, 10.000 líneas)
/// no se toca hasta que la nueva esté aprobada. Las dos miran los mismos datos.
///
/// Lo que arma para cada fila, además de lo de siempre:
///   • RECETA: de qué productos del sistema se compone y cuántas unidades hay de cada uno.
///   • ARMA: para cuántas publicaciones alcanza (manda el componente más escaso).
///     Ej: pack de 3 cajas + 3 tapas, con 60 cajas y 6 tapas → arma 2, y la tapa es la limitante.
///   • COMISIÓN REAL: el total que se lleva MeLi (porcentaje + cargo fijo + cuotas), no solo el %.
///     En productos baratos el cargo fijo pesa muchísimo: $2.960 con 14,5% + $1.250 fijo = 56,7%.
///   • FAMILIA: si el mismo SKU tiene varias publicaciones activas con precios distintos,
///     devuelve el rango — eso explica el "un precio afuera y otro adentro".
/// </summary>
public class MeliPublicacionesV2Service
{
    private readonly AppDbContext _db;
    private const int DEPOSITO_9_ABRIL = 1;
    private const decimal IVA = 1.21m;

    /// <summary>Por debajo de esta diferencia, dos hermanas al "mismo" precio no se marcan.
    /// Medido el 26/08: de 171 avisos, 39 eran diferencias de menos del 1% (el caso testigo fueron
    /// $99 sobre $641.300). Avisar por eso es ruido y hace que se ignoren los avisos que importan.</summary>
    private const decimal TOLERANCIA_PRECIO = 0.01m;

    public MeliPublicacionesV2Service(AppDbContext db) => _db = db;

    /// <summary>La palabra que Osmar escribe en el SKU para marcar "hay que arreglarla".
    /// Configurable en AppSettings (`meli.sku_marca_revisar`); si no está, es PAUSAR.</summary>
    private string? _marcaRevisar;

    private async Task<string> GetMarcaRevisarAsync(CancellationToken ct)
    {
        if (_marcaRevisar is not null) return _marcaRevisar;
        var v = await _db.AppSettings.AsNoTracking()
            .Where(x => x.Key == "meli.sku_marca_revisar")
            .Select(x => x.Value).FirstOrDefaultAsync(ct);
        _marcaRevisar = string.IsNullOrWhiteSpace(v) ? "PAUSAR" : v.Trim();
        return _marcaRevisar;
    }

    /// <summary>Un renglón de la receta. Desde el 29/08 viaja también el Id del componente, el id
    /// del producto y su costo: son los que hacen falta para poder EDITAR la receta desde la fila
    /// (cambiar la cantidad, cambiar el producto) y para marcar los costos que faltan.</summary>
    public record ComponenteDto(string? Sku, string Nombre, decimal Cantidad, int Stock, int Alcanza, bool Frena,
        int Id = 0, int ProductoId = 0, decimal Costo = 0m);

    public record FilaDto(
        string MeliItemId, string? Sku, string Titulo, string? Thumbnail, string? Permalink,
        decimal Precio, string? Estado, string? Tipo, string? Cuotas, bool EnvioGratis, string? Envio,
        int StockMeli, int Vendidas,
        decimal? Costo, decimal? MargenPct, decimal? GananciaPesos, decimal? NetoSinIva,
        decimal? ComisionMonto, decimal? ComisionPct, decimal? ComisionPorcentaje, decimal? ComisionFija, decimal? ComisionEnvio,
        bool ComisionVieja, List<ComponenteDto> Receta, int? Arma,
        int PublisFamilia, decimal? PrecioMin, decimal? PrecioMax, bool VariosPrecios,
        bool SyncPrecio, bool SyncStock, decimal? ObjetivoPct,
        string? Cuenta,
        // 2026-08-27: el SKU que tenía antes de que la marcaran para revisar (ver MeliItemSyncConfig).
        string? SkuAnterior,
        // 2026-08-31: si está en una campaña de MeLi, lo que el comprador paga DE VERDAD.
        decimal? PromoPrecio = null, string? PromoNombre = null, DateTime? PromoHasta = null);

    public record PageDto(int Total, int Pagina, int PorPagina, List<FilaDto> Items);

    public record Filtros(
        string? Texto = null, string? Sku = null, string? Estado = null, int? CuentaId = null,
        decimal? ComisionMinPct = null, string? Cuotas = null, string? Tipo = null,
        bool VariosPrecios = false, bool PrecioAMano = false, bool SinSincroPrecio = false,
        bool SinCosto = false, decimal? NoLleganAlPct = null, bool ComisionVieja = false, int Pagina = 1, int PorPagina = 100);

    public async Task<PageDto> GetAsync(Filtros f, CancellationToken ct = default)
    {
        var pagina = f.Pagina < 1 ? 1 : f.Pagina;
        var porPagina = f.PorPagina is < 1 or > 500 ? 100 : f.PorPagina;

        // ── 1) Base: una fila por publicación (las variantes se resuelven aparte) ──
        var q = _db.MeliItems.AsNoTracking().Where(m => m.VariationId == null);

        // Estado: por defecto no mostramos cerradas ni borradas.
        if (string.Equals(f.Estado, "activas", StringComparison.OrdinalIgnoreCase))
            q = q.Where(m => m.Status == "active");
        else if (string.Equals(f.Estado, "pausadas", StringComparison.OrdinalIgnoreCase))
            q = q.Where(m => m.Status == "paused");
        else
            q = q.Where(m => m.Status != "closed" && m.Status != "deleted");

        if (f.CuentaId.HasValue) q = q.Where(m => m.MeliAccountId == f.CuentaId.Value);
        if (!string.IsNullOrWhiteSpace(f.Texto))
        {
            // 2026-08-26 — Osmar pegaba un número y le venía UNA sola publicación, cuando lo que
            // quiere es la familia entera. MercadoLibre muestra los números así: "#1421759403".
            var t = f.Texto.Trim().TrimStart('#').Trim();

            // 2026-08-29 — CORRECCIÓN. Lo de arriba se estaba aplicando a los DOS casos y estaba mal.
            // Osmar, textual: *"si busco por número de publicación debería traerme número de
            // publicación, ¿no te parece?"*. Y tiene razón. Son dos búsquedas distintas:
            //   • un MLA (MLA1126305168)  → ESA publicación, sola.
            //   • un número suelto (#1421759403, el número de FAMILIA que muestra MeLi)
            //     → la familia entera, que es lo que pidió el 26/08.
            // El caso que lo destapó: buscó un MLA marcado con PAUSAR y le vinieron 171 juntas.
            var esMla = t.Length >= 6 && t.StartsWith("MLA", StringComparison.OrdinalIgnoreCase);
            var esNumeroDeFamilia = t.Length >= 6 && t.All(char.IsDigit);

            var abrioGrupo = false;

            if (esMla)
            {
                // Exacto. Es lo único que puede querer alguien que pega un número de publicación.
                var mla = t.ToUpperInvariant();
                q = q.Where(m => m.MeliItemId == mla);
                abrioGrupo = true;
            }
            else if (esNumeroDeFamilia)
            {
                var claves = await _db.MeliItems.AsNoTracking()
                    .Where(m => m.VariationId == null
                                && (m.MeliItemId.Contains(t)
                                    || (m.FamilyId != null && m.FamilyId.Contains(t))
                                    || (m.UserProductId != null && m.UserProductId.Contains(t))))
                    .Select(m => new { m.FamilyId, m.UserProductId, m.Sku })
                    .Take(50).ToListAsync(ct);

                if (claves.Count > 0)
                {
                    var fams = claves.Where(x => !string.IsNullOrEmpty(x.FamilyId)).Select(x => x.FamilyId!).Distinct().ToList();
                    var ups = claves.Where(x => !string.IsNullOrEmpty(x.UserProductId)).Select(x => x.UserProductId!).Distinct().ToList();
                    // 2026-08-29 — La marca de "para revisar" (PAUSAR) NO es una familia.
                    // Osmar la escribe en el SKU de MercadoLibre para marcar lo que hay que arreglar,
                    // así que la comparten CIENTOS de publicaciones que no tienen nada que ver entre
                    // sí. Sin esto, buscar un número de publicación marcada devolvía las 171 juntas y
                    // parecía que el buscador no andaba. Ver [[SkuAnterior]] en MeliItemSyncConfig.
                    var marcaRevisar = await GetMarcaRevisarAsync(ct);
                    var sks = claves.Where(x => !string.IsNullOrEmpty(x.Sku)
                                                && !string.Equals(x.Sku, marcaRevisar, StringComparison.OrdinalIgnoreCase))
                        .Select(x => x.Sku!).Distinct().ToList();

                    q = q.Where(m => (m.FamilyId != null && fams.Contains(m.FamilyId))
                                     || (m.UserProductId != null && ups.Contains(m.UserProductId))
                                     || (m.Sku != null && sks.Contains(m.Sku)));
                    abrioGrupo = true;
                }
            }

            if (!abrioGrupo)
                q = q.Where(m => m.Title.Contains(t) || m.MeliItemId.Contains(t)
                                 || (m.Sku != null && m.Sku.Contains(t))
                                 || (m.FamilyName != null && m.FamilyName.Contains(t))
                                 || (m.FamilyId != null && m.FamilyId.Contains(t))
                                 || (m.UserProductId != null && m.UserProductId.Contains(t)));
        }
        if (!string.IsNullOrWhiteSpace(f.Sku))
        {
            var sk = f.Sku.Trim();
            q = q.Where(m => m.Sku != null && m.Sku.Contains(sk));
        }
        if (!string.IsNullOrWhiteSpace(f.Tipo)) q = q.Where(m => m.ListingTypeId == f.Tipo);
        if (!string.IsNullOrWhiteSpace(f.Cuotas))
        {
            if (string.Equals(f.Cuotas, "sin", StringComparison.OrdinalIgnoreCase))
                q = q.Where(m => m.InstallmentTag == null);
            else
                q = q.Where(m => m.InstallmentTag == f.Cuotas);
        }

        // Comisión REAL: monto / precio. Sirve para "MeLi se lleva más de 30%".
        if (f.ComisionMinPct.HasValue)
        {
            var min = f.ComisionMinPct.Value;
            q = q.Where(m => m.Price > 0 && m.SaleFeeAmount != null
                             && ((m.SaleFeeAmount.Value + (m.SaleFeeShippingCost ?? 0m)) / m.Price * 100m) >= min);
        }

        // Config de sincronización (la fila puede no tener config todavía → se trata como apagada).
        if (f.PrecioAMano || f.SinSincroPrecio)
            q = q.Where(m => !_db.MeliItemSyncConfigs.Any(c => c.MeliItemId == m.MeliItemId && c.SyncPrecio));

        // Familias con varios precios: SKUs cuyas publicaciones activas no tienen todas el mismo precio.
        if (f.VariosPrecios)
        {
            // 2026-08-26 — Antes esto marcaba en rojo CUALQUIER diferencia de precio dentro del SKU.
            // Estaba mal: las "opciones de venta" del mismo producto (sin cuotas vs 3 a 12 cuotas)
            // TIENEN que valer distinto, porque MeLi les cobra distinto — medido en el armario
            // C9333GR: mismo precio y misma venta, y entre la mejor opción y la de cuotas hay
            // $35.032 de diferencia en lo que recibís (87,6% contra 72,6% sobre el costo).
            // Ahora solo se marca lo que SÍ es un descuido: mismas condiciones, distinto precio.
            var skusMulti = _db.MeliItems.AsNoTracking()
                .Where(x => x.VariationId == null && x.Status == "active" && x.Sku != null && x.Price > 0)
                .GroupBy(x => new { Sku = x.Sku!, Cuotas = x.InstallmentTag, Tipo = x.ListingTypeId, Envio = x.FreeShipping })
                .Where(g => g.Min(x => x.Price) / g.Max(x => x.Price) < 1m - TOLERANCIA_PRECIO)
                .Select(g => g.Key.Sku);
            q = q.Where(m => m.Sku != null && skusMulti.Contains(m.Sku));
        }

        // Datos viejos: la comisión se capturó a un precio que ya cambió más de 5%.
        if (f.ComisionVieja)
            q = q.Where(m => m.SaleFeeAmount != null && m.SaleFeeAmount > 0
                             && m.SaleFeePriceSnapshot != null && m.SaleFeePriceSnapshot > 0
                             && m.Price > 0
                             && (m.Price - m.SaleFeePriceSnapshot.Value) / m.Price > 0.05m
                                || (m.SaleFeePriceSnapshot != null && m.Price > 0
                                    && (m.SaleFeePriceSnapshot.Value - m.Price) / m.Price > 0.05m));

        // ── Filtro "no llegan al X% sobre el costo" ──
        // Se resuelve en dos consultas livianas en vez de una subconsulta correlacionada por fila:
        // (a) el costo de cada publicación (un GROUP BY sobre los componentes),
        // (b) precio y lo que se lleva MeLi de las candidatas. La cuenta se hace acá.
        // Es la misma que muestra la ficha: (precio − comisión − envío) / 1,21 − costo, sobre el costo.
        if (f.NoLleganAlPct.HasValue)
        {
            var piso = f.NoLleganAlPct.Value;

            var costoPorItem = await (
                from c in _db.MeliItemComponentes.AsNoTracking()
                join p in _db.CafeProductos.AsNoTracking() on c.CafeProductoId equals p.Id
                group p.Costo * c.Cantidad by c.MeliItemId into g
                select new { MeliItemId = g.Key, Costo = g.Sum() }
            ).ToDictionaryAsync(x => x.MeliItemId, x => x.Costo, ct);

            var candidatas = await q
                .Select(m => new { m.MeliItemId, m.Price, m.SaleFeeAmount, m.SaleFeeShippingCost })
                .ToListAsync(ct);

            var flojas = new List<string>();
            foreach (var c in candidatas)
            {
                if (!costoPorItem.TryGetValue(c.MeliItemId, out var costoItem) || costoItem <= 0) continue;
                var netoItem = (c.Price - (c.SaleFeeAmount ?? 0m) - (c.SaleFeeShippingCost ?? 0m)) / IVA;
                var margenItem = (netoItem - costoItem) / costoItem * 100m;
                if (margenItem < piso) flojas.Add(c.MeliItemId);
            }
            q = q.Where(m => flojas.Contains(m.MeliItemId));
        }

        var total = await q.CountAsync(ct);

        var pageRows = await q
            .OrderBy(m => m.Title).ThenBy(m => m.MeliItemId)
            .Skip((pagina - 1) * porPagina).Take(porPagina)
            .Select(m => new
            {
                m.MeliItemId, m.Sku, m.Title, m.Thumbnail, m.Permalink, m.Price, m.Status,
                m.ListingTypeId, m.InstallmentTag, m.FreeShipping, m.LogisticType, m.AvailableQuantity, m.SoldQuantity,
                m.SaleFeeAmount, m.SaleFeePercentageFee, m.SaleFeeFixedFee, m.SaleFeeShippingCost, m.SaleFeePriceSnapshot,
                m.PromoPrecio, m.PromoNombre, m.PromoHasta,
                m.CafeProductoId, m.CafeFormato, m.MeliAccountId,
                Cuenta = m.MeliAccount != null ? m.MeliAccount.Nickname : null
            })
            .ToListAsync(ct);

        var ids = pageRows.Select(r => r.MeliItemId).ToList();
        var skus = pageRows.Where(r => r.Sku != null).Select(r => r.Sku!).Distinct().ToList();

        // ── 2) Receta: componentes + stock del depósito 9 de Abril ──
        var comps = await (
            from c in _db.MeliItemComponentes.AsNoTracking()
            join p in _db.CafeProductos.AsNoTracking() on c.CafeProductoId equals p.Id
            where ids.Contains(c.MeliItemId)
            select new { c.Id, c.MeliItemId, c.CafeProductoId, c.Cantidad, p.Sku, p.Nombre, p.Costo }
        ).ToListAsync(ct);

        var prodIds = comps.Select(c => c.CafeProductoId).Distinct().ToList();
        // Productos linkeados por el modelo viejo (MeliItem.CafeProductoId), para los que no tienen componentes.
        var legacyIds = pageRows.Where(r => r.CafeProductoId.HasValue).Select(r => r.CafeProductoId!.Value).Distinct().ToList();
        var todosProdIds = prodIds.Concat(legacyIds).Distinct().ToList();

        var stockPorProd = await _db.CafeStockPorDeposito.AsNoTracking()
            .Where(s => s.DepositoId == DEPOSITO_9_ABRIL && todosProdIds.Contains(s.ProductoId))
            .ToDictionaryAsync(s => s.ProductoId, s => s.StockUnidades, ct);

        var legacyProds = await _db.CafeProductos.AsNoTracking()
            .Where(p => legacyIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.Costo })
            .ToDictionaryAsync(p => p.Id, ct);

        // ── 3) Config de sincronización ──
        var cfgs = await _db.MeliItemSyncConfigs.AsNoTracking()
            .Where(c => ids.Contains(c.MeliItemId))
            .Select(c => new { c.MeliItemId, c.SyncPrecio, c.SyncStock, c.GananciaObjetivoPct, c.SkuAnterior })
            .ToDictionaryAsync(c => c.MeliItemId, ct);

        // ── 4) Familia: cuántas publicaciones activas por SKU y su rango de precios ──
        var familias = await _db.MeliItems.AsNoTracking()
            .Where(x => x.VariationId == null && x.Status == "active" && x.Sku != null && x.Price > 0
                        && skus.Contains(x.Sku))
            .GroupBy(x => x.Sku!)
            .Select(g => new { Sku = g.Key, Cant = g.Count(), Min = g.Min(x => x.Price), Max = g.Max(x => x.Price) })
            .ToDictionaryAsync(g => g.Sku, ct);

        // Y el mismo corte pero POR CONDICIONES DE VENTA: dos publicaciones del mismo producto solo
        // deberían valer lo mismo si venden en LAS MISMAS CONDICIONES. Son tres cosas, no una:
        //   • las CUOTAS      — 12 cuotas paga ~19 puntos más de financiación que sin cuotas
        //   • el TIPO         — Premium cobra ~26% donde Clásica cobra ~14%
        //   • el ENVÍO        — si lo pagás vos, el precio tiene que absorberlo
        // 2026-08-26: faltaban el tipo y el envío. Caso real, C9334GR: marcaba en rojo una Clásica
        // a $1.021.399 contra una Premium a $1.031.299 — y está bien que no valgan lo mismo.
        var porCondicion = (await _db.MeliItems.AsNoTracking()
            .Where(x => x.VariationId == null && x.Status == "active" && x.Sku != null && x.Price > 0
                        && skus.Contains(x.Sku))
            .GroupBy(x => new { Sku = x.Sku!, Cuotas = x.InstallmentTag, Tipo = x.ListingTypeId, Envio = x.FreeShipping })
            .Select(g => new { g.Key.Sku, g.Key.Cuotas, g.Key.Tipo, g.Key.Envio,
                               Cant = g.Count(), Min = g.Min(x => x.Price), Max = g.Max(x => x.Price) })
            .ToListAsync(ct))
            .ToDictionary(g => (g.Sku, g.Cuotas ?? "", g.Tipo ?? "", g.Envio), g => (g.Cant, g.Min, g.Max));

        // ── 5) Armar cada fila ──
        var items = new List<FilaDto>(pageRows.Count);
        foreach (var r in pageRows)
        {
            var misComps = comps.Where(c => c.MeliItemId == r.MeliItemId).ToList();

            var receta = new List<ComponenteDto>();
            int? arma = null;
            decimal? costo = null;

            if (misComps.Count > 0)
            {
                // Costo: dedup por SKU (mismo criterio que el motor de precios).
                costo = misComps.GroupBy(c => c.Sku).Select(g => g.First())
                    .Sum(c => c.Costo * c.Cantidad);

                foreach (var c in misComps)
                {
                    var stock = stockPorProd.GetValueOrDefault(c.CafeProductoId, 0);
                    var alcanza = c.Cantidad > 0 ? (int)Math.Floor(stock / c.Cantidad) : 0;
                    receta.Add(new ComponenteDto(c.Sku, c.Nombre, c.Cantidad, stock, alcanza, false,
                        c.Id, c.CafeProductoId, c.Costo));
                    if (arma is null || alcanza < arma) arma = alcanza;
                }
                // Marcar cuál frena (el más escaso). Si empatan, se marcan todos los que empatan.
                if (arma.HasValue)
                    receta = receta.Select(c => c.Alcanza == arma.Value ? c with { Frena = true } : c).ToList();
            }
            else if (r.CafeProductoId.HasValue && legacyProds.TryGetValue(r.CafeProductoId.Value, out var lp))
            {
                var stock = stockPorProd.GetValueOrDefault(r.CafeProductoId.Value, 0);
                costo = lp.Costo;
                arma = stock;
                receta.Add(new ComponenteDto(lp.Sku, lp.Nombre, 1m, stock, stock, false,
                    0, r.CafeProductoId.Value, lp.Costo));
            }

            // Comisión real: comisión + cargo fijo + ENVÍO a tu cargo.
            // 2026-08-25: el envío estaba afuera y escondía el número de verdad. Medido en prod:
            // 1.508 publicaciones con envío gratis pasan de 17,2% promedio (solo comisión) a 33,4%
            // con el envío. Hay casos de 94,7% (mesa de $98.400 con $74.490 de envío).
            // SaleFeeShippingCost solo viene cargado cuando el envío es gratis (lo pagás vos).
            var seLlevaMeli = (r.SaleFeeAmount ?? 0m) + (r.SaleFeeShippingCost ?? 0m);
            decimal? comPct = (r.SaleFeeAmount.HasValue && r.Price > 0)
                ? Math.Round(seLlevaMeli / r.Price * 100m, 1) : null;

            // Lo que REALMENTE queda: se descuenta lo que se lleva MeLi (comisión + envío) y el IVA.
            // Se muestran las dos cosas juntas — el % sobre el costo y la plata — porque el %
            // solo no dice nada: 200% sobre un costo de $981 son $2.000, no una fortuna.
            // 2026-08-26 — SIN COMISIÓN NO HAY MARGEN. Antes, si MeLi todavía no nos había dicho
            // cuánto cobra, `seLlevaMeli` valía 0 y la cuenta salía igual: la fila mostraba un
            // número verde y tranquilizador calculado COMO SI MELI NO COBRARA NADA.
            // Caso real: MLA1683493719 mostraba 80,3% cuando su hermana, con la comisión ya
            // cargada, daba 25,4%. Ahora se devuelve vacío y la pantalla dice qué falta.
            // 2026-08-31 — SI ESTÁ EN PROMOCIÓN, LA CUENTA VA CON EL PRECIO DE LA PROMOCIÓN.
            // `Price` es el precio de lista; si la publicación entró a una campaña el comprador
            // paga menos, y calcular el margen sobre el precio de lista lo infla — que es el lado
            // peligroso del error. Caso real del 31/08: el azúcar MLA2048049400 figuraba a
            // $23.998,99 y se vendía a $17.999,24 por "CYBER FEST 09.09".
            //
            // La comisión guardada se capturó al precio de lista, así que se la escala: MeLi cobra
            // un porcentaje del precio, y ese porcentaje no cambia. El envío NO se escala — cuesta
            // lo mismo la caja, valga lo que valga adentro.
            var precioReal = (r.PromoPrecio is > 0 && r.PromoPrecio < r.Price) ? r.PromoPrecio.Value : r.Price;

            decimal? neto = null, ganancia = null, margen = null;
            if (r.Price > 0 && r.SaleFeeAmount.HasValue)
            {
                var comisionReal = r.SaleFeeAmount.Value;
                if (precioReal != r.Price)
                    comisionReal = Math.Round(r.SaleFeeAmount.Value / r.Price * precioReal, 2);

                neto = Math.Round((precioReal - comisionReal - (r.SaleFeeShippingCost ?? 0m)) / IVA, 2);
                if (costo is > 0)
                {
                    ganancia = Math.Round(neto.Value - costo.Value, 2);
                    margen = Math.Round(ganancia.Value / costo.Value * 100m, 1);
                }
            }

            // ¿La comisión guardada sigue valiendo? Se capturó a un precio dado; si el precio se movió
            // más de un 5%, el número que muestra (y el margen que sale de él) ya no es confiable.
            var comisionVieja = r.SaleFeeAmount is > 0 && r.SaleFeePriceSnapshot is > 0
                                && Math.Abs(r.Price - r.SaleFeePriceSnapshot.Value) / (r.Price == 0 ? 1m : r.Price) > 0.05m;

            familias.TryGetValue(r.Sku ?? "", out var fam);
            // Ojo: la alarma mira SOLO a las hermanas que venden en las MISMAS condiciones
            // (mismas cuotas, mismo tipo y mismo envío). Además hay una tolerancia: por menos
            // de 1% de diferencia no vale la pena molestar — medido, eran 39 avisos de $99.
            porCondicion.TryGetValue((r.Sku ?? "", r.InstallmentTag ?? "", r.ListingTypeId ?? "", r.FreeShipping), out var cond);
            var variosPrecios = cond.Cant > 1 && cond.Max > 0
                                && (cond.Max - cond.Min) / cond.Max >= TOLERANCIA_PRECIO;

            cfgs.TryGetValue(r.MeliItemId, out var cfg);

            items.Add(new FilaDto(
                r.MeliItemId, r.Sku, r.Title, r.Thumbnail, r.Permalink,
                r.Price, r.Status, r.ListingTypeId, r.InstallmentTag, r.FreeShipping, r.LogisticType, r.AvailableQuantity, r.SoldQuantity,
                costo, margen, ganancia, neto,
                (r.SaleFeeAmount.HasValue ? seLlevaMeli : (decimal?)null), comPct, r.SaleFeePercentageFee, r.SaleFeeFixedFee, r.SaleFeeShippingCost,
                comisionVieja, receta, arma,
                fam?.Cant ?? 1,
                variosPrecios ? cond.Min : fam?.Min,
                variosPrecios ? cond.Max : fam?.Max,
                variosPrecios,
                cfg?.SyncPrecio ?? false, cfg?.SyncStock ?? false, cfg?.GananciaObjetivoPct,
                r.Cuenta, cfg?.SkuAnterior,
                r.PromoPrecio, r.PromoNombre, r.PromoHasta));
        }

        // Filtros que dependen de datos calculados (se aplican sobre la página).
        if (f.SinCosto) items = items.Where(i => i.Costo is null or <= 0).ToList();

        return new PageDto(total, pagina, porPagina, items);
    }

    /// <summary>Números para los chips de arriba: cuántas caen en cada filtro. Una sola pasada.</summary>
    public async Task<Dictionary<string, int>> GetResumenAsync(CancellationToken ct = default)
    {
        var baseQ = _db.MeliItems.AsNoTracking()
            .Where(m => m.VariationId == null && m.Status != "closed" && m.Status != "deleted");

        // Mismo criterio que la lista: solo cuenta como problema si las condiciones de venta
        // son las mismas (ver el comentario largo en GetAsync).
        var skusMulti = _db.MeliItems.AsNoTracking()
            .Where(x => x.VariationId == null && x.Status == "active" && x.Sku != null && x.Price > 0)
            .GroupBy(x => new { Sku = x.Sku!, Cuotas = x.InstallmentTag, Tipo = x.ListingTypeId, Envio = x.FreeShipping })
            .Where(g => g.Min(x => x.Price) / g.Max(x => x.Price) < 1m - TOLERANCIA_PRECIO)
            .Select(g => g.Key.Sku);

        return new Dictionary<string, int>
        {
            ["total"] = await baseQ.CountAsync(ct),
            ["activas"] = await baseQ.CountAsync(m => m.Status == "active", ct),
            ["pausadas"] = await baseQ.CountAsync(m => m.Status == "paused", ct),
            ["comisionAlta"] = await baseQ.CountAsync(m => m.Price > 0 && m.SaleFeeAmount != null
                                                           && ((m.SaleFeeAmount.Value + (m.SaleFeeShippingCost ?? 0m)) / m.Price * 100m) >= 30m, ct),
            ["variosPrecios"] = await baseQ.CountAsync(m => m.Sku != null && skusMulti.Contains(m.Sku), ct),
            ["precioAMano"] = await baseQ.CountAsync(m => !_db.MeliItemSyncConfigs.Any(c => c.MeliItemId == m.MeliItemId && c.SyncPrecio), ct),
        };
    }
}
