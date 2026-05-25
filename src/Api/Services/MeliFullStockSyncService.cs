using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Lee el stock de Full (meli_facility) de las publicaciones Full y lo guarda en el depósito
/// virtual "Full MeLi" (Cafe_StockPorDeposito[deposito_Full]).
///
/// Por qué existe:
///   Una publicación Full tiene 2 fuentes de stock en MeLi:
///     - selling_address  → tu depósito propio (9 de Abril). Este lo controla el sistema y
///       lo pushea MeliStockPushService cada vez que cambiás stock.
///     - meli_facility    → stock que ya enviaste físicamente al depósito Full de MeLi.
///       MeLi lo gestiona, vos NO tenés control directo. Solo cambia cuando enviás cajas
///       a Full o cuando MeLi vende del Full.
///   Sin este servicio, el sistema solo "ve" lo de 9 de Abril. El usuario no puede saber cuánto
///   tiene en Full sin entrar al panel de MeLi → confusión grande (ej: usuario ve 163 en 9 de
///   Abril, MeLi muestra "available_quantity" 38 en la publicación = el Full → no entiende).
///
/// Cómo funciona:
///   1) Junta todos los UserProductIds distintos (UPGs) linkeados a CafeProductos.
///   2) Para cada UPG: GET /user-products/{upg}/stock → parsea locations.
///   3) Si hay meli_facility, multiplica quantity × cantidad del componente (para
///      packs de N unidades, cada Full equivale a N unidades del producto base).
///   4) Suma por CafeProducto (un mismo producto puede estar en múltiples UPGs/packs
///      Full distintos) y guarda en Cafe_StockPorDeposito[Full MeLi].
///
/// IMPORTANTE: el stock total del producto (Cafe_Productos.StockUnidades) NO se modifica acá.
/// Este servicio solo pobla el depósito virtual Full — la "verdad real" sigue siendo lo que
/// el usuario maneja en 9 de Abril + lo que MeLi reporta en Full.
/// </summary>
public class MeliFullStockSyncService
{
    private readonly AppDbContext _db;
    private readonly IHttpClientFactory _httpFactory;
    private readonly MeliAccountService _accountService;
    private readonly ILogger<MeliFullStockSyncService> _logger;

    private const string DEPOSITO_FULL_NOMBRE = "Full MeLi";

    public MeliFullStockSyncService(AppDbContext db, IHttpClientFactory httpFactory,
        MeliAccountService accountService, ILogger<MeliFullStockSyncService> logger)
    {
        _db = db;
        _httpFactory = httpFactory;
        _accountService = accountService;
        _logger = logger;
    }

    public record FullSyncResult(int UpgsProcesados, int UpgsFull, int ProductosActualizados,
        int Errores, List<string> Mensajes);

    /// <summary>Sincroniza el stock Full de TODOS los UPGs linkeados. Se llama desde job
    /// background cada 30min o desde endpoint admin. Filtro opcional por CafeProductoId.</summary>
    public async Task<FullSyncResult> SyncAllAsync(int? soloProductoId = null, CancellationToken ct = default)
    {
        var mensajes = new List<string>();
        int upgsProcesados = 0, upgsFull = 0, errores = 0;

        // 1) Buscar depósito Full
        var depFull = await _db.CafeDepositos.FirstOrDefaultAsync(d => d.Nombre == DEPOSITO_FULL_NOMBRE, ct);
        if (depFull is null)
        {
            mensajes.Add($"No existe el depósito '{DEPOSITO_FULL_NOMBRE}' en Cafe_Depositos");
            return new FullSyncResult(0, 0, 0, 1, mensajes);
        }

        // 2) Juntar UPGs distintos + sus componentes (necesitamos multiplicar por cantidad de pack).
        // Cada fila MeliItem puede mapear a 1+ componentes (via MeliItemComponentes) o al legacy
        // CafeProductoId. Filtramos solo items activos con UserProductId no nulo.
        var meliItemsQuery = _db.MeliItems
            .Where(mi => mi.UserProductId != null && mi.Status == "active");
        if (soloProductoId.HasValue)
        {
            var pid = soloProductoId.Value;
            meliItemsQuery = meliItemsQuery.Where(mi =>
                mi.CafeProductoId == pid ||
                _db.MeliItemComponentes.Any(c => c.MeliItemId == mi.MeliItemId && c.CafeProductoId == pid));
        }

        var meliItems = await meliItemsQuery
            .Select(mi => new {
                mi.MeliItemId,
                mi.UserProductId,
                mi.MeliAccountId,
                mi.CafeProductoId,
                mi.CafeFormato,
                mi.LogisticType
            }).ToListAsync(ct);

        if (meliItems.Count == 0)
            return new FullSyncResult(0, 0, 0, 0, new() { "Sin items linkeados activos" });

        // Componentes (mapping producto×cantidad) por MeliItemId
        var meliItemIds = meliItems.Select(x => x.MeliItemId).Distinct().ToList();
        var componentes = await _db.MeliItemComponentes
            .Where(c => meliItemIds.Contains(c.MeliItemId))
            .ToListAsync(ct);
        var compsByItem = componentes.GroupBy(c => c.MeliItemId).ToDictionary(g => g.Key, g => g.ToList());

        // Agrupar por (cuenta, UPG) — un UPG puede tener varios MeliItems pero la quantity de Full
        // es POR UPG, no por item. Necesitamos saber qué CafeProducto y con qué cantidad le toca.
        var grupos = meliItems.GroupBy(x => new { x.MeliAccountId, x.UserProductId }).ToList();

        // Cuentas → token
        var accounts = await _db.MeliAccounts.AsNoTracking().ToListAsync(ct);
        var tokenCache = new Dictionary<int, string?>();

        // Stock por producto que vamos a ESCRIBIR en Cafe_StockPorDeposito[Full]
        // Acumulamos por CafeProductoId.
        var fullStockPorProducto = new Dictionary<int, int>();

        foreach (var grupo in grupos)
        {
            if (ct.IsCancellationRequested) break;
            upgsProcesados++;

            var account = accounts.FirstOrDefault(a => a.Id == grupo.Key.MeliAccountId);
            if (account is null) { errores++; continue; }

            if (!tokenCache.TryGetValue(account.Id, out var token))
            {
                token = await _accountService.GetValidTokenAsync(account);
                tokenCache[account.Id] = token;
            }
            if (string.IsNullOrEmpty(token)) { errores++; continue; }

            try
            {
                using var http = _httpFactory.CreateClient();
                http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                http.Timeout = TimeSpan.FromSeconds(20);

                var resp = await http.GetAsync($"https://api.mercadolibre.com/user-products/{grupo.Key.UserProductId}/stock", ct);
                if (!resp.IsSuccessStatusCode)
                {
                    // 404 = UPG inválido (probablemente la pub fue borrada). Lo ignoramos.
                    if (resp.StatusCode != System.Net.HttpStatusCode.NotFound)
                    {
                        errores++;
                        mensajes.Add($"UPG {grupo.Key.UserProductId}: GET {(int)resp.StatusCode}");
                    }
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct);
                var doc = JsonDocument.Parse(json).RootElement;

                // Buscar location meli_facility
                int fullQty = 0;
                bool tieneMeliFacility = false;
                if (doc.TryGetProperty("locations", out var locs) && locs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var loc in locs.EnumerateArray())
                    {
                        if (loc.TryGetProperty("type", out var t) && t.GetString() == "meli_facility")
                        {
                            tieneMeliFacility = true;
                            if (loc.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qi))
                                fullQty = qi;
                            break;
                        }
                    }
                }

                if (!tieneMeliFacility) continue; // No es Full, salta
                upgsFull++;

                // Repartir fullQty entre los componentes (productos) que mapean estos items.
                // Si el UPG tiene varios MeliItems con el MISMO componente (raro), evitamos
                // contar duplicado: usamos DISTINCT por (MeliItemId, CafeProductoId, Cantidad).
                var componentesAplicables = new List<(int CafeProductoId, decimal Cantidad)>();
                foreach (var mi in grupo)
                {
                    if (compsByItem.TryGetValue(mi.MeliItemId, out var comps) && comps.Count > 0)
                    {
                        // Caso normal: 1 componente por item (pack de N unidades del producto base).
                        // Si fueran combos de N productos distintos, se reparte cada uno.
                        foreach (var c in comps)
                        {
                            componentesAplicables.Add((c.CafeProductoId, c.Cantidad));
                        }
                    }
                    else if (mi.CafeProductoId.HasValue)
                    {
                        // Legacy fallback: cantidad 1 por unidad (no hay packs).
                        componentesAplicables.Add((mi.CafeProductoId.Value, 1m));
                    }
                }
                // Deduplicar (mismo producto con misma cantidad cuenta una sola vez)
                var compsUnicos = componentesAplicables
                    .GroupBy(x => new { x.CafeProductoId, x.Cantidad })
                    .Select(g => g.First()).ToList();

                // Cada Full equivale a (Cantidad) unidades del producto base. Si pack de 3 con Full=2,
                // significa 6 unidades del producto base están en Full.
                foreach (var (cafeProductoId, cantidad) in compsUnicos)
                {
                    var unidadesEnFull = (int)Math.Round(fullQty * cantidad);
                    if (fullStockPorProducto.ContainsKey(cafeProductoId))
                        fullStockPorProducto[cafeProductoId] += unidadesEnFull;
                    else
                        fullStockPorProducto[cafeProductoId] = unidadesEnFull;
                }

                // Rate limit defensivo (~6 req/s).
                await Task.Delay(150, ct);
            }
            catch (Exception ex)
            {
                errores++;
                mensajes.Add($"UPG {grupo.Key.UserProductId}: {ex.Message}");
                _logger.LogWarning(ex, "Error sincronizando Full stock para UPG {Upg}", grupo.Key.UserProductId);
            }
        }

        // 3) Persistir en Cafe_StockPorDeposito[Full]
        var prodIds = fullStockPorProducto.Keys.ToList();
        var existentes = await _db.CafeStockPorDeposito
            .Where(s => s.DepositoId == depFull.Id && prodIds.Contains(s.ProductoId))
            .ToListAsync(ct);
        var existentesMap = existentes.ToDictionary(s => s.ProductoId);

        int productosActualizados = 0;
        foreach (var kv in fullStockPorProducto)
        {
            if (existentesMap.TryGetValue(kv.Key, out var row))
            {
                if (row.StockUnidades != kv.Value)
                {
                    row.StockUnidades = kv.Value;
                    row.UpdatedAt = DateTime.UtcNow;
                    productosActualizados++;
                }
            }
            else
            {
                _db.CafeStockPorDeposito.Add(new CafeStockPorDeposito
                {
                    ProductoId = kv.Key,
                    DepositoId = depFull.Id,
                    StockUnidades = kv.Value,
                    StockGramos = 0m,
                    UpdatedAt = DateTime.UtcNow
                });
                productosActualizados++;
            }
        }

        // Productos que ANTES tenían Full > 0 pero ahora no aparecen → resetear a 0.
        // (puede pasar si la pub fue borrada o el usuario sacó stock del Full)
        if (!soloProductoId.HasValue)
        {
            foreach (var ex in existentes.Where(s => !fullStockPorProducto.ContainsKey(s.ProductoId)))
            {
                if (ex.StockUnidades != 0)
                {
                    ex.StockUnidades = 0;
                    ex.UpdatedAt = DateTime.UtcNow;
                    productosActualizados++;
                }
            }
        }

        await _db.SaveChangesAsync(ct);

        return new FullSyncResult(upgsProcesados, upgsFull, productosActualizados, errores, mensajes);
    }
}
