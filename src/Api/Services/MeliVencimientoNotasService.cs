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
        foreach (var grupo in ordenes.GroupBy(o => (o.MeliAccountId, o.MeliOrderId)))
        {
            var filas = grupo.ToList();
            var notas = await TextosAsync(db, filas, ct);
            if (notas.Count == 0) continue;   // ningún producto con vencimiento: no se marca (por si le asignan después)
            var account = await db.MeliAccounts.FirstOrDefaultAsync(a => a.Id == grupo.Key.MeliAccountId, ct);
            if (account is null) continue;
            var token = await accSvc.GetValidTokenAsync(account);
            if (token is null) continue;
            var http = httpF.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var ok = true;
            foreach (var nota in notas)
            {
                var body = JsonSerializer.Serialize(new { note = nota });
                using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
                var resp = await http.PostAsync($"https://api.mercadolibre.com/orders/{grupo.Key.MeliOrderId}/notes", content, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    ok = false;
                    var err = await resp.Content.ReadAsStringAsync(ct);
                    Console.WriteLine($"[VencNota] order {grupo.Key.MeliOrderId} {(int)resp.StatusCode}: {err[..Math.Min(err.Length, 200)]}");
                    break;
                }
            }
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
                var pid = await db.MeliItems.AsNoTracking().Where(m => m.MeliItemId == o.ItemId && m.CafeProductoId != null)
                    .Select(m => m.CafeProductoId).FirstOrDefaultAsync(ct);
                if (pid is int p) unidades[p] = unidades.GetValueOrDefault(p) + o.Quantity;
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
        foreach (var grupo in ordenes.GroupBy(o => (o.MeliAccountId, o.MeliOrderId)))
        {
            var filas = grupo.ToList();
            var nota = await GananciaTextoAsync(db, filas, ct);
            if (nota is null) { foreach (var f in filas) f.GananciaNoteSentAt = DateTime.UtcNow; await db.SaveChangesAsync(ct); continue; }
            var account = await db.MeliAccounts.FirstOrDefaultAsync(a => a.Id == grupo.Key.MeliAccountId, ct);
            if (account is null) continue;
            var token = await accSvc.GetValidTokenAsync(account);
            if (token is null) continue;
            var http = httpF.CreateClient();
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var body = JsonSerializer.Serialize(new { note = nota });
            using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.PostAsync($"https://api.mercadolibre.com/orders/{grupo.Key.MeliOrderId}/notes", content, ct);
            if (resp.IsSuccessStatusCode)
            {
                foreach (var f in filas) f.GananciaNoteSentAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            else
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                Console.WriteLine($"[GananciaNota] order {grupo.Key.MeliOrderId} {(int)resp.StatusCode}: {err[..Math.Min(err.Length, 200)]}");
            }
        }
    }

    private static readonly System.Globalization.CultureInfo Ar = new("es-AR");
    private static string Pesos(decimal v) => "$" + Math.Round(v, 0).ToString("N0", Ar);

    /// <summary>Texto de la nota de ganancia (null si no hay costo cargado o no hay datos de cobro).</summary>
    public static async Task<string?> GananciaTextoAsync(AppDbContext db, List<MeliOrder> filas, CancellationToken ct = default)
    {
        if (filas.Any(f => f.FinNeto is null)) return null;
        var (unidades, ambigua) = await UnidadesYDudaAsync(db, filas, ct);
        if (ambigua || unidades.Count == 0) return null;   // no se sabe bien qué se vendió: mejor no poner un número falso
        var ids = unidades.Keys.ToList();
        var prods = await db.CafeProductos.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Costo, p.IvaPct }).ToListAsync(ct);
        if (prods.Count != ids.Count || prods.Any(p => p.Costo <= 0)) return null;
        var costo = prods.Sum(p => unidades[p.Id] * p.Costo * (1 + p.IvaPct / 100m));
        var cobra = filas.Sum(f => f.TotalAmount);
        var com = filas.Sum(f => f.FinComision ?? 0);
        var ret = filas.Sum(f => f.FinRetenciones ?? 0);
        var otros = filas.Sum(f => f.FinOtros ?? 0);
        var envio = filas.Sum(f => f.FinEnvio ?? 0);
        var neto = filas.Sum(f => f.FinNeto ?? 0);
        var envioEstimado = false;
        if (envio <= 0 && cobra >= 33000m)
        {
            var itemIds = filas.Select(f => f.ItemId).Distinct().ToList();
            var est = await db.MeliItems.AsNoTracking()
                .Where(m => itemIds.Contains(m.MeliItemId) && m.FreeShipping && m.SaleFeeShippingCost != null)
                .Select(m => m.SaleFeeShippingCost!.Value).FirstOrDefaultAsync(ct);
            if (est > 0) { envio = est; neto -= est; envioEstimado = true; }
        }
        var gana = neto - costo;
        var pct = costo > 0 ? Math.Round(gana / costo * 100m, 0) : 0;
        var txt = $"💲 Cobrás {Pesos(cobra)} − com. {Pesos(com)} − ret. {Pesos(ret)}"
            + (otros > 0 ? $" − otros {Pesos(otros)}" : "")
            + $" − envío {(envioEstimado ? "~" : "")}{Pesos(envio)} = {Pesos(neto)} · costo {Pesos(costo)} → gana {Pesos(gana)} ({pct:0}%)";
        return txt.Length > 290 ? txt[..290] : txt;
    }

    /// <summary>Café: los formatos de la receta van en kg (1KG/MEDIO/CUARTO). Lo demás: 1 (BULTO no se usa en recetas MeLi).</summary>
    private static decimal Factor(string? formato) => (formato ?? "").ToUpperInvariant() switch
    {
        "1KG" => 1m, "MEDIO" => 0.5m, "CUARTO" => 0.25m, _ => 1m
    };

    private static string Corto(string nombre) => nombre.Length > 40 ? nombre[..40] : nombre;
}
