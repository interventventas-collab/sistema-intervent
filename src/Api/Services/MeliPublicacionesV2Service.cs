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

    public MeliPublicacionesV2Service(AppDbContext db) => _db = db;

    public record ComponenteDto(string? Sku, string Nombre, decimal Cantidad, int Stock, int Alcanza, bool Frena);

    public record FilaDto(
        string MeliItemId, string? Sku, string Titulo, string? Thumbnail, string? Permalink,
        decimal Precio, string? Estado, string? Tipo, string? Cuotas, int StockMeli, int Vendidas,
        decimal? Costo, decimal? MargenPct,
        decimal? ComisionMonto, decimal? ComisionPct, decimal? ComisionPorcentaje, decimal? ComisionFija, decimal? ComisionEnvio,
        List<ComponenteDto> Receta, int? Arma,
        int PublisFamilia, decimal? PrecioMin, decimal? PrecioMax, bool VariosPrecios,
        bool SyncPrecio, bool SyncStock, decimal? ObjetivoPct,
        string? Cuenta);

    public record PageDto(int Total, int Pagina, int PorPagina, List<FilaDto> Items);

    public record Filtros(
        string? Texto = null, string? Sku = null, string? Estado = null, int? CuentaId = null,
        decimal? ComisionMinPct = null, string? Cuotas = null, string? Tipo = null,
        bool VariosPrecios = false, bool PrecioAMano = false, bool SinSincroPrecio = false,
        bool SinCosto = false, int Pagina = 1, int PorPagina = 100);

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
            var t = f.Texto.Trim();
            q = q.Where(m => m.Title.Contains(t) || m.MeliItemId.Contains(t) || (m.Sku != null && m.Sku.Contains(t)));
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
                             && (m.SaleFeeAmount.Value / m.Price * 100m) >= min);
        }

        // Config de sincronización (la fila puede no tener config todavía → se trata como apagada).
        if (f.PrecioAMano || f.SinSincroPrecio)
            q = q.Where(m => !_db.MeliItemSyncConfigs.Any(c => c.MeliItemId == m.MeliItemId && c.SyncPrecio));

        // Familias con varios precios: SKUs cuyas publicaciones activas no tienen todas el mismo precio.
        if (f.VariosPrecios)
        {
            var skusMulti = _db.MeliItems.AsNoTracking()
                .Where(x => x.VariationId == null && x.Status == "active" && x.Sku != null && x.Price > 0)
                .GroupBy(x => x.Sku!)
                .Where(g => g.Select(x => x.Price).Distinct().Count() > 1)
                .Select(g => g.Key);
            q = q.Where(m => m.Sku != null && skusMulti.Contains(m.Sku));
        }

        var total = await q.CountAsync(ct);

        var pageRows = await q
            .OrderBy(m => m.Title).ThenBy(m => m.MeliItemId)
            .Skip((pagina - 1) * porPagina).Take(porPagina)
            .Select(m => new
            {
                m.MeliItemId, m.Sku, m.Title, m.Thumbnail, m.Permalink, m.Price, m.Status,
                m.ListingTypeId, m.InstallmentTag, m.AvailableQuantity, m.SoldQuantity,
                m.SaleFeeAmount, m.SaleFeePercentageFee, m.SaleFeeFixedFee, m.SaleFeeShippingCost,
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
            select new { c.MeliItemId, c.CafeProductoId, c.Cantidad, p.Sku, p.Nombre, p.Costo }
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
            .Select(c => new { c.MeliItemId, c.SyncPrecio, c.SyncStock, c.GananciaObjetivoPct })
            .ToDictionaryAsync(c => c.MeliItemId, ct);

        // ── 4) Familia: cuántas publicaciones activas por SKU y su rango de precios ──
        var familias = await _db.MeliItems.AsNoTracking()
            .Where(x => x.VariationId == null && x.Status == "active" && x.Sku != null && x.Price > 0
                        && skus.Contains(x.Sku))
            .GroupBy(x => x.Sku!)
            .Select(g => new { Sku = g.Key, Cant = g.Count(), Min = g.Min(x => x.Price), Max = g.Max(x => x.Price) })
            .ToDictionaryAsync(g => g.Sku, ct);

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
                    receta.Add(new ComponenteDto(c.Sku, c.Nombre, c.Cantidad, stock, alcanza, false));
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
                receta.Add(new ComponenteDto(lp.Sku, lp.Nombre, 1m, stock, stock, false));
            }

            // Comisión real y margen
            decimal? comPct = (r.SaleFeeAmount.HasValue && r.Price > 0)
                ? Math.Round(r.SaleFeeAmount.Value / r.Price * 100m, 1) : null;

            decimal? margen = null;
            if (costo is > 0)
            {
                var neto = (r.Price - (r.SaleFeeAmount ?? 0m) - (r.SaleFeeShippingCost ?? 0m)) / IVA;
                margen = Math.Round((neto - costo.Value) / costo.Value * 100m, 1);
            }

            familias.TryGetValue(r.Sku ?? "", out var fam);
            var variosPrecios = fam != null && fam.Cant > 1 && fam.Min != fam.Max;

            cfgs.TryGetValue(r.MeliItemId, out var cfg);

            items.Add(new FilaDto(
                r.MeliItemId, r.Sku, r.Title, r.Thumbnail, r.Permalink,
                r.Price, r.Status, r.ListingTypeId, r.InstallmentTag, r.AvailableQuantity, r.SoldQuantity,
                costo, margen,
                r.SaleFeeAmount, comPct, r.SaleFeePercentageFee, r.SaleFeeFixedFee, r.SaleFeeShippingCost,
                receta, arma,
                fam?.Cant ?? 1, fam?.Min, fam?.Max, variosPrecios,
                cfg?.SyncPrecio ?? false, cfg?.SyncStock ?? false, cfg?.GananciaObjetivoPct,
                r.Cuenta));
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

        var skusMulti = _db.MeliItems.AsNoTracking()
            .Where(x => x.VariationId == null && x.Status == "active" && x.Sku != null && x.Price > 0)
            .GroupBy(x => x.Sku!)
            .Where(g => g.Select(x => x.Price).Distinct().Count() > 1)
            .Select(g => g.Key);

        return new Dictionary<string, int>
        {
            ["total"] = await baseQ.CountAsync(ct),
            ["activas"] = await baseQ.CountAsync(m => m.Status == "active", ct),
            ["pausadas"] = await baseQ.CountAsync(m => m.Status == "paused", ct),
            ["comisionAlta"] = await baseQ.CountAsync(m => m.Price > 0 && m.SaleFeeAmount != null
                                                           && (m.SaleFeeAmount.Value / m.Price * 100m) >= 30m, ct),
            ["variosPrecios"] = await baseQ.CountAsync(m => m.Sku != null && skusMulti.Contains(m.Sku), ct),
            ["precioAMano"] = await baseQ.CountAsync(m => !_db.MeliItemSyncConfigs.Any(c => c.MeliItemId == m.MeliItemId && c.SyncPrecio), ct),
        };
    }
}
