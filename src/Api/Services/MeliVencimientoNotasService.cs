using System.Net.Http.Headers;
using System.Text.Json;
using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// 2026-11-04: pedido del dueño — "en las ventas de MeLi, que en el comentario de la orden me diga qué
/// producto con qué fecha tiene que salir". Igual que la nota de Maps y la del teléfono: deja UNA nota
/// en la orden de MeLi, por ejemplo "📅 VENCE → 7790863008796: Sacar el que vence 15/01/2027".
/// Solo para productos que tienen vencimientos asignados (Asignar vencimientos). Se calcula con el stock
/// de ANTES de la venta (si la venta ya descontó stock, se le vuelven a sumar sus unidades), porque lo que
/// sale en esta venta es lo de la fecha más próxima.
/// Corre SOLO en producción (dev tiene la cuenta real de MeLi vinculada: no debe escribir en órdenes reales).
/// </summary>
public class MeliVencimientoNotasService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostEnvironment _env;
    private static readonly TimeSpan Periodo = TimeSpan.FromMinutes(5);
    private static readonly string[] EnvioTerminado = { "shipped", "delivered", "cancelled", "not_delivered" };

    public MeliVencimientoNotasService(IServiceScopeFactory scopeFactory, IHostEnvironment env)
    { _scopeFactory = scopeFactory; _env = env; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!_env.IsProduction()) return;
        try { await Task.Delay(TimeSpan.FromMinutes(2), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var acc = scope.ServiceProvider.GetRequiredService<MeliAccountService>();
                var http = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
                await ProcesarAsync(db, acc, http, ct);
                await ProcesarGananciaAsync(db, acc, http, ct);
            }
            catch (Exception ex) { Console.WriteLine($"[VencNota] error: {ex.Message}"); }
            try { await Task.Delay(Periodo, ct); } catch { return; }
        }
    }

    private static async Task ProcesarAsync(AppDbContext db, MeliAccountService accSvc, IHttpClientFactory httpF, CancellationToken ct)
    {
        // Solo los productos que tienen vencimientos: si no hay ninguno, no hay nada que hacer.
        if (!await db.CafeProductoVencimientos.AnyAsync(ct)) return;
        var desde = DateTime.UtcNow.AddDays(-2);
        var ordenes = await db.MeliOrders
            .Where(o => o.VencNoteSentAt == null && o.DateCreated >= desde && o.Status == "paid"
                && (o.LogisticType == null || o.LogisticType != "fulfillment")
                && (o.ShippingStatus == null || !EnvioTerminado.Contains(o.ShippingStatus)))
            .ToListAsync(ct);
        // 2026-11-04: los "paquetes" de MeLi (carrito con varios productos) son varias órdenes con el mismo
        // PackId, y en la vista del paquete MeLi muestra la nota de UNA sola. Se arma una nota para todo el
        // paquete y se deja en cada orden del paquete.
        foreach (var grupo in ordenes.GroupBy(o => (o.MeliAccountId, Clave: o.PackId ?? o.MeliOrderId)))
        {
            var filas = await FilasDelPaqueteAsync(db, grupo.ToList(), ct);
            if (filas.Any(f => f.VencNoteSentAt != null)) { foreach (var f in filas) f.VencNoteSentAt ??= DateTime.UtcNow; await db.SaveChangesAsync(ct); continue; }
            var notas = await TextosAsync(db, filas, ct);
            if (notas.Count == 0) continue;   // ningún producto con vencimiento: no se marca (por si le asignan después)
            var account = await db.MeliAccounts.FirstOrDefaultAsync(a => a.Id == grupo.Key.MeliAccountId, ct);
            if (account is null) continue;
            var token = await accSvc.GetValidTokenAsync(account);
            if (token is null) continue;
            var http = httpF.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var ok = true;
            // 2026-11-06: va dentro de la nota única de la venta (junto con ganancia y teléfono).
            var venc = notas[0] + string.Concat(notas.Skip(1).Select(n => " | " + n.Replace("📅 VENCE → ", "")));
            foreach (var oid in filas.Select(f => f.MeliOrderId).Distinct())
                if (!await MeliNotaUnica.PonerAsync(http, oid, MeliNotaUnica.Vence, venc, "VencNota", ct)) { ok = false; break; }
            if (ok)
            {
                foreach (var f in filas) f.VencNoteSentAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>Texto(s) de la nota para una orden (sus renglones). Vacío si ningún producto tiene vencimientos.
    /// Cada nota ≤ 290 caracteres (MeLi admite 300); si no entra, se parte en varias.</summary>
    public static async Task<List<string>> TextosAsync(AppDbContext db, List<MeliOrder> filas, CancellationToken ct = default)
    {
        var unidades = await UnidadesAsync(db, filas, ct);
        if (unidades.Count == 0) return new();
        return await TextosDeUnidadesAsync(db, filas, unidades, ct);
    }

    /// <summary>Unidades (café: kg) de cada producto que lleva la orden, según la receta de la publicación
    /// (o el producto atado directo si no tiene receta).</summary>
    public static async Task<Dictionary<int, decimal>> UnidadesAsync(AppDbContext db, List<MeliOrder> filas, CancellationToken ct = default)
        => (await UnidadesYDudaAsync(db, filas, ct)).Unidades;

    /// <summary>Igual que UnidadesAsync + si hubo algún renglón AMBIGUO (publicación con variantes mal
    /// vinculadas: varios productos sin variante y no se sabe cuál se vendió). Misma regla que el
    /// descuento de stock (MeliStockSyncService): en la duda no se adivina.</summary>
    public static async Task<(Dictionary<int, decimal> Unidades, bool Ambigua)> UnidadesYDudaAsync(AppDbContext db, List<MeliOrder> filas, CancellationToken ct = default)
    {
        var unidades = new Dictionary<int, decimal>();
        var ambigua = false;
        foreach (var o in filas)
        {
            var comps = await db.MeliItemComponentes.AsNoTracking().Where(c => c.MeliItemId == o.ItemId).ToListAsync(ct);
            if (comps.Count == 0)
            {
                // Vínculo viejo (producto atado directo a la publicación). Café fraccionado: igual que el
                // descuento de stock (MeliStockSyncService) — SKU que termina en .4 = ¼ kg, .2 = ½ kg;
                // si no, el CafeFormato de la publicación (MEDIO/CUARTO) o 1 kg.
                var mis = await db.MeliItems.AsNoTracking().Where(m => m.MeliItemId == o.ItemId && m.CafeProductoId != null)
                    .Select(m => new { m.CafeProductoId, m.VariationId, m.Sku, m.CafeFormato }).ToListAsync(ct);
                var mi = mis.FirstOrDefault(m => m.VariationId == o.VariationId) ?? mis.FirstOrDefault();
                if (mi?.CafeProductoId is int p)
                {
                    var esCafe = await db.CafeProductos.AsNoTracking().AnyAsync(x => x.Id == p && x.Categoria == "CAFE", ct);
                    var factor = !esCafe ? 1m
                        : mi.Sku is string sk && sk.EndsWith(".4") ? 0.25m
                        : mi.Sku is string sk2 && sk2.EndsWith(".2") ? 0.5m
                        : Factor(mi.CafeFormato);
                    unidades[p] = unidades.GetValueOrDefault(p) + o.Quantity * factor;
                }
                else ambigua = true;
                continue;
            }
            var compsNull = comps.Where(c => string.IsNullOrEmpty(c.MeliVariationId)).ToList();
            List<MeliItemComponente> aplicables;
            if (!string.IsNullOrEmpty(o.VariationId) && comps.Any(c => c.MeliVariationId == o.VariationId))
                aplicables = comps.Where(c => c.MeliVariationId == o.VariationId).ToList();
            else if (compsNull.Count > 0)
            {
                var distintos = compsNull.Select(c => c.CafeProductoId).Distinct().Count();
                var variantes = await db.MeliItems.AsNoTracking().CountAsync(m => m.MeliItemId == o.ItemId && m.VariationId != null, ct);
                if (distintos > 1 && (!string.IsNullOrEmpty(o.VariationId) || variantes > 1)) { ambigua = true; continue; }
                aplicables = compsNull.GroupBy(c => c.CafeProductoId).Select(g => g.First()).ToList();
            }
            else { ambigua = true; continue; }
            foreach (var c in aplicables)
                unidades[c.CafeProductoId] = unidades.GetValueOrDefault(c.CafeProductoId) + o.Quantity * c.Cantidad * Factor(c.Formato);
        }
        return (unidades, ambigua);
    }

    private static async Task<List<string>> TextosDeUnidadesAsync(AppDbContext db, List<MeliOrder> filas, Dictionary<int, decimal> unidades, CancellationToken ct)
    {
        var ids = unidades.Keys.ToList();
        var vencs = await db.CafeProductoVencimientos.AsNoTracking().Where(v => ids.Contains(v.ProductoId)).ToListAsync(ct);
        if (vencs.Count == 0) return new();
        var prods = await db.CafeProductos.AsNoTracking().Where(p => ids.Contains(p.Id)).ToListAsync(ct);
        var descontada = filas.Any(f => f.StockDiscounted);
        var partes = new List<string>();
        foreach (var p in prods)
        {
            var vs = vencs.Where(v => v.ProductoId == p.Id).ToList();
            if (vs.Count == 0) continue;
            var esCafe = VencimientosService.EsCafe(p);
            var u = unidades[p.Id];
            var stock = VencimientosService.StockDe(p) + (descontada ? u : 0);
            var r = VencimientosService.Calcular(p.Id, esCafe, stock, vs);
            var txt = VencimientosService.TextoSacar(r, u);
            if (txt is null) continue;
            partes.Add($"{(string.IsNullOrWhiteSpace(p.Sku) ? Corto(p.Nombre) : p.Sku)}: {txt}");
        }
        if (partes.Count == 0) return new();
        var notas = new List<string>();
        var actual = "📅 VENCE → ";
        foreach (var parte in partes)
        {
            var sep = actual.EndsWith("→ ") ? "" : " | ";
            if ((actual + sep + parte).Length > 290 && !actual.EndsWith("→ "))
            {
                notas.Add(actual);
                actual = "📅 VENCE → " + parte;
            }
            else actual += sep + parte;
        }
        notas.Add(actual.Length > 290 ? actual[..290] : actual);
        return notas;
    }

    // ════════════════════════════════════════════════════════════════════
    // 2026-11-04: nota de GANANCIA. "💲 Cobrás $X − com. $Y − ret. $Z − envío $W = $N · costo $C → gana $G (P%)"
    // Ganancia sobre el COSTO con IVA (mismo criterio que el piso de precios). Cuando el pago todavía no
    // trae el envío y la publicación tiene envío gratis arriba de $33.000, se usa el costo de envío de la
    // publicación (SaleFeeShippingCost) y se marca con "~" (estimado).
    // ════════════════════════════════════════════════════════════════════
    private static async Task ProcesarGananciaAsync(AppDbContext db, MeliAccountService accSvc, IHttpClientFactory httpF, CancellationToken ct)
    {
        var desde = DateTime.UtcNow.AddDays(-1);
        var ordenes = await db.MeliOrders
            .Where(o => o.GananciaNoteSentAt == null && o.FinConsultadoAt != null && o.FinNeto != null
                && o.DateCreated >= desde && o.Status == "paid")
            .ToListAsync(ct);
        foreach (var grupo in ordenes.GroupBy(o => (o.MeliAccountId, Clave: o.PackId ?? o.MeliOrderId)))
        {
            var filas = await FilasDelPaqueteAsync(db, grupo.ToList(), ct);
            if (filas.Any(f => f.GananciaNoteSentAt != null)) { foreach (var f in filas) f.GananciaNoteSentAt ??= DateTime.UtcNow; await db.SaveChangesAsync(ct); continue; }
            if (filas.Any(f => f.FinNeto is null)) continue;   // falta traer el cobro de alguna orden del paquete
            var account = await db.MeliAccounts.FirstOrDefaultAsync(a => a.Id == grupo.Key.MeliAccountId, ct);
            if (account is null) continue;
            var token = await accSvc.GetValidTokenAsync(account);
            if (token is null) continue;
            var http = httpF.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            // Flex: lo que se cobra por el envío va aparte. Si MeLi no contesta, se reintenta en la próxima vuelta.
            FlexEnvio? flex = null;
            if (EsFlex(filas))
            {
                flex = await FlexEnvioAsync(http, filas, ct);
                if (flex is null) continue;
            }
            var nota = await GananciaTextoAsync(db, filas, ct, flex);
            if (nota is null) { foreach (var f in filas) f.GananciaNoteSentAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); continue; }
            var okG = true;
            foreach (var oid in filas.Select(f => f.MeliOrderId).Distinct())
                if (!await MeliNotaUnica.PonerAsync(http, oid, MeliNotaUnica.Ganancia, nota, "GananciaNota", ct)) { okG = false; break; }
            if (okG)
            {
                foreach (var f in filas) f.GananciaNoteSentAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    /// <summary>Todas las órdenes del mismo paquete (PackId), aunque alguna no haya entrado en la consulta.</summary>
    public static async Task<List<MeliOrder>> FilasDelPaqueteAsync(AppDbContext db, List<MeliOrder> filas, CancellationToken ct)
    {
        var pack = filas.Select(f => f.PackId).FirstOrDefault(p => p != null);
        if (pack is null) return filas;
        var todas = await db.MeliOrders.Where(o => o.PackId == pack && o.MeliAccountId == filas[0].MeliAccountId && o.Status == "paid").ToListAsync(ct);
        return todas.Count > 0 ? todas : filas;
    }

    private static readonly System.Globalization.CultureInfo Ar = new("es-AR");
    private static string Pesos(decimal v) => "$" + Math.Round(v, 0).ToString("N0", Ar);

    /// <summary>Sugerido = precio sugerido del fabricante (OEM PvpConIva × multiplicador × unidades); null si algún producto no lo tiene.</summary>
    private record Cuenta(string Ref, decimal Cobra, decimal Com, decimal Ret, decimal Otros, decimal Envio, decimal Neto, decimal Costo, decimal? Sugerido = null);

    /// <summary>Cuenta de UNA orden (sus renglones): null si no se sabe bien qué se vendió o falta costo.</summary>
    private static async Task<Cuenta?> CuentaDeOrdenAsync(AppDbContext db, List<MeliOrder> filas, CancellationToken ct)
    {
        if (filas.Any(f => f.FinNeto is null)) return null;
        var (unidades, ambigua) = await UnidadesYDudaAsync(db, filas, ct);
        if (ambigua || unidades.Count == 0) return null;   // no se sabe bien qué se vendió: mejor no poner un número falso
        var ids = unidades.Keys.ToList();
        var prods = await db.CafeProductos.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Costo, p.IvaPct, p.Categoria, Pvp = p.OemNav != null ? p.OemNav.PvpConIva : null, p.MultiplicadorOem }).ToListAsync(ct);
        if (prods.Count != ids.Count || prods.Any(p => p.Costo <= 0)) return null;
        // 2026-11-06: el costo del café está mal cargado → no se pone ganancia en ventas con café (sería un número falso).
        if (prods.Any(p => string.Equals(p.Categoria, "CAFE", StringComparison.OrdinalIgnoreCase))) return null;
        var costo = prods.Sum(p => unidades[p.Id] * p.Costo * (1 + p.IvaPct / 100m));
        decimal? sugerido = prods.All(p => p.Pvp is > 0)
            ? prods.Sum(p => unidades[p.Id] * p.Pvp!.Value * (p.MultiplicadorOem is > 0 ? p.MultiplicadorOem.Value : 1m))
            : null;
        var item = filas[0].ItemId;
        var sku = await db.MeliItems.AsNoTracking().Where(m => m.MeliItemId == item && m.Sku != null)
            .OrderBy(m => m.VariationId == filas[0].VariationId ? 0 : 1).Select(m => m.Sku).FirstOrDefaultAsync(ct);
        var refTxt = !string.IsNullOrWhiteSpace(sku) ? sku! : (filas[0].ItemTitle.Length > 22 ? filas[0].ItemTitle[..22] : filas[0].ItemTitle);
        return new Cuenta(refTxt, filas.Sum(f => f.TotalAmount), filas.Sum(f => f.FinComision ?? 0), filas.Sum(f => f.FinRetenciones ?? 0),
            filas.Sum(f => f.FinOtros ?? 0), filas.Sum(f => f.FinEnvio ?? 0), filas.Sum(f => f.FinNeto ?? 0), costo, sugerido);
    }

    /// <summary>Texto de la nota de ganancia (null si no hay costo cargado, no hay datos de cobro o es dudosa).
    /// Varias órdenes del mismo paquete → una nota con el total del paquete + cuánto deja cada producto.</summary>
    public static async Task<string?> GananciaTextoAsync(AppDbContext db, List<MeliOrder> filas, CancellationToken ct = default, FlexEnvio? flex = null)
    {
        var esFlex = EsFlex(filas);
        var cuentas = new List<Cuenta>();
        foreach (var g in filas.GroupBy(f => f.MeliOrderId))
        {
            var c = await CuentaDeOrdenAsync(db, g.ToList(), ct);
            if (c is null) return null;
            cuentas.Add(c);
        }
        if (cuentas.Count == 0) return null;
        var cobra = cuentas.Sum(c => c.Cobra); var com = cuentas.Sum(c => c.Com); var ret = cuentas.Sum(c => c.Ret);
        var otros = cuentas.Sum(c => c.Otros); var envio = cuentas.Sum(c => c.Envio); var neto = cuentas.Sum(c => c.Neto);
        var costo = cuentas.Sum(c => c.Costo);
        if (esFlex)
        {
            // Flex: cuando el envío lo paga el cliente, MeLi lo deposita junto con la venta (entra en el neto).
            // Se saca de la cuenta del producto y se muestra aparte, así la ganancia es solo de la mercadería.
            cuentas = cuentas.Select(c => c with { Neto = c.Cobra - c.Com - c.Ret - c.Otros - c.Envio }).ToList();
            neto = cuentas.Sum(c => c.Neto);
        }
        var envioEstimado = false;
        if (!esFlex && envio <= 0 && cobra >= 33000m)
        {
            var itemIds = filas.Select(f => f.ItemId).Distinct().ToList();
            var est = await db.MeliItems.AsNoTracking()
                .Where(m => itemIds.Contains(m.MeliItemId) && m.FreeShipping && m.SaleFeeShippingCost != null)
                .Select(m => m.SaleFeeShippingCost!.Value).OrderByDescending(x => x).FirstOrDefaultAsync(ct);
            if (est > 0) { envio = est; neto -= est; envioEstimado = true; }
        }
        var gana = neto - costo;
        var pct = costo > 0 ? Math.Round(gana / costo * 100m, 0) : 0;
        var cab = cuentas.Count > 1 ? $"💲 Paquete de {cuentas.Count}: Cobrás" : "💲 Cobrás";
        var txt = $"{cab} {Pesos(cobra)} − com. {Pesos(com)} − ret. {Pesos(ret)}"
            + (otros > 0 ? $" − otros {Pesos(otros)}" : "")
            + (esFlex && envio <= 0 ? "" : $" − envío {(envioEstimado ? "~" : "")}{Pesos(envio)}")
            + $" = {Pesos(neto)} · costo {Pesos(costo)} → gana {Pesos(gana)} ({pct:0}%)"
            + (cuentas.All(c => c.Sugerido is > 0) ? $" · sug. fábrica {Pesos(cuentas.Sum(c => c.Sugerido!.Value))}" : "")
            + (esFlex ? " · " + FlexTexto(flex) : "");
        if (cuentas.Count > 1)
        {
            // cuánto deja cada producto (el envío estimado se reparte según lo que cobra cada uno)
            var det = cuentas.Select(c =>
            {
                var envC = envioEstimado && cobra > 0 ? envio * c.Cobra / cobra : 0;
                var g = c.Neto - envC - c.Costo;
                var p = c.Costo > 0 ? Math.Round(g / c.Costo * 100m, 0) : 0;
                return $"{c.Ref} {p:0}%";
            });
            txt += " | " + string.Join(" · ", det);
        }
        return txt.Length > 290 ? txt[..290] : txt;
    }

    // ─── Flex: el envío aparte ───
    /// <summary>Lo que entra por el envío de una venta Flex: lo que pagó el cliente y lo que bonifica MeLi
    /// (MeLi+ / descuento obligatorio del 10% en envío gratis). El descuento "ratio" es el envío gratis
    /// que pone el vendedor: no es plata que entra.</summary>
    public record FlexEnvio(decimal PagaCliente, decimal BonificaMeli);

    public static bool EsFlex(List<MeliOrder> filas) => filas.Any(f => f.LogisticType == "self_service");

    /// <summary>Consulta /shipments/{id}/costs. Null si MeLi no contesta.</summary>
    public static async Task<FlexEnvio?> FlexEnvioAsync(HttpClient http, List<MeliOrder> filas, CancellationToken ct = default)
    {
        var sid = filas.Select(f => f.ShippingId).FirstOrDefault(x => x != null);
        if (sid is null) return null;
        try
        {
            using var resp = await http.GetAsync($"https://api.mercadolibre.com/shipments/{sid}/costs", ct);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            var root = doc.RootElement;
            decimal cliente = 0, meli = 0;
            static decimal Num(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0;
            static decimal Bonif(JsonElement e, bool sinRatio)
            {
                decimal t = 0;
                if (e.TryGetProperty("discounts", out var ds) && ds.ValueKind == JsonValueKind.Array)
                    foreach (var d in ds.EnumerateArray())
                    {
                        var tipo = d.TryGetProperty("type", out var tp) && tp.ValueKind == JsonValueKind.String ? tp.GetString() : null;
                        if (sinRatio && tipo == "ratio") continue;
                        t += Num(d, "promoted_amount");
                    }
                return t;
            }
            if (root.TryGetProperty("receiver", out var rc) && rc.ValueKind == JsonValueKind.Object)
            {
                cliente = Num(rc, "cost");
                meli += Bonif(rc, sinRatio: true);
            }
            if (root.TryGetProperty("senders", out var ss) && ss.ValueKind == JsonValueKind.Array)
                foreach (var se in ss.EnumerateArray()) meli += Bonif(se, sinRatio: false);
            return new FlexEnvio(cliente, meli);
        }
        catch { return null; }
    }

    private static string FlexTexto(FlexEnvio? f)
    {
        if (f is null) return "🛵 Flex: envío aparte";
        var total = f.PagaCliente + f.BonificaMeli;
        if (total <= 0) return "🛵 Envío Flex aparte: $0";
        var quien = f.PagaCliente > 0 && f.BonificaMeli > 0 ? $"cliente {Pesos(f.PagaCliente)} + MeLi {Pesos(f.BonificaMeli)}"
            : f.PagaCliente > 0 ? "lo pagó el cliente" : "bonifica MeLi";
        return $"🛵 Envío Flex aparte: +{Pesos(total)} ({quien})";
    }

    /// <summary>Café: los formatos de la receta van en kg (1KG/MEDIO/CUARTO). Lo demás: 1 (BULTO no se usa en recetas MeLi).</summary>
    private static decimal Factor(string? formato) => (formato ?? "").ToUpperInvariant() switch
    {
        "1KG" => 1m, "MEDIO" => 0.5m, "CUARTO" => 0.25m, _ => 1m
    };

    private static string Corto(string nombre) => nombre.Length > 40 ? nombre[..40] : nombre;
}
