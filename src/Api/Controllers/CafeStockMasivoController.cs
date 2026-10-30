using Api.Data;
using Api.Models;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// Modificacion masiva de stock por deposito.
/// Permite ver y editar el stock de todos los productos en un deposito especifico,
/// o ver stocks por deposito y mover entre depositos.
/// </summary>
[ApiController]
[Route("api/cafe/stock-masivo")]
[Authorize]
public class CafeStockMasivoController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly CafeStockLogger _stockLogger;

    public CafeStockMasivoController(AppDbContext db, AuditLogService audit, IServiceScopeFactory scopeFactory, CafeStockLogger stockLogger)
    {
        _db = db; _audit = audit; _scopeFactory = scopeFactory; _stockLogger = stockLogger;
    }

    /// <summary>Dispara push de stock a MeLi en background para los productos cambiados (respeta kill switches).</summary>
    private void FireAndForgetPushMeli(List<int> cafeProductoIds)
    {
        if (cafeProductoIds.Count == 0) return;
        var scopeFactory = _scopeFactory;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var pushSvc = scope.ServiceProvider.GetRequiredService<MeliStockPushService>();
                foreach (var pid in cafeProductoIds)
                {
                    try { await pushSvc.PushStockForProductoAsync(pid); }
                    catch { /* swallow */ }
                }
            }
            catch { /* swallow */ }
        });
    }

    public record StockProductoDto(
        int ProductoId, string Codigo, string Nombre, string Categoria,
        decimal StockGramos, int StockUnidades, string? Composicion = null, bool IsActive = true);

    /// <summary>Lista productos con su stock en el deposito indicado.
    /// 2026-05-25: Si el depósito es 'Full MeLi', filtramos solo los productos que tienen
    /// al menos 1 publicación MeLi con logistic_type=fulfillment linkeada (sea via
    /// MeliItemComponente o legacy MeliItem.CafeProductoId). Para los demás depósitos
    /// se devuelven TODOS los productos (comportamiento original).</summary>
    // 20/10/2026: link del repartidor → Oficina → 📦 Stock. Primero los depósitos, después el stock de uno
    // (el mismo listado de la pantalla Stock de la compu; el celu muestra solo nombre y cantidades).
    [AllowAnonymous]
    [HttpGet("repartidor/{token}")]
    public async Task<IActionResult> DepositosParaRepartidor(string token)
    {
        if (await RepartidorOficina.ConPermisoAsync(_db, token, RepartidorOficina.Stock) is null) return NotFound(new { error = "No tenés habilitado ver esto" });
        var deps = await _db.CafeDepositos.AsNoTracking().Where(d => d.IsActive)
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.Orden).ThenBy(d => d.Nombre)
            .Select(d => new { d.Id, d.Nombre, d.IsDefault }).ToListAsync();
        return Ok(deps);
    }

    [AllowAnonymous]
    [HttpGet("repartidor/{token}/{depositoId:int}")]
    public async Task<IActionResult> StockParaRepartidor(string token, int depositoId)
    {
        if (await RepartidorOficina.ConPermisoAsync(_db, token, RepartidorOficina.Stock) is null) return NotFound(new { error = "No tenés habilitado ver esto" });
        return await ListarStockEnDeposito(depositoId);
    }

    [HttpGet("{depositoId:int}")]
    public async Task<IActionResult> ListarStockEnDeposito(int depositoId)
    {
        var dep = await _db.CafeDepositos.FindAsync(depositoId);
        if (dep is null) return NotFound(new { error = "Deposito no encontrado" });

        IQueryable<Models.CafeProducto> baseQ = _db.CafeProductos;

        if (dep.Nombre == "Full MeLi")
        {
            // Productos con al menos un MeliItem Full linkeado (componente o legacy).
            // Como LogisticType puede estar NULL para items que aún no se re-sincronizaron,
            // también incluimos productos que tienen registro en Cafe_StockPorDeposito[Full]
            // (ya pasaron por el sync de Full y se confirmó que están en meli_facility).
            var prodIdsFull = await (
                from p in _db.CafeProductos
                where _db.MeliItems.Any(mi =>
                          mi.LogisticType == "fulfillment" &&
                          (mi.CafeProductoId == p.Id ||
                           _db.MeliItemComponentes.Any(c => c.MeliItemId == mi.MeliItemId && c.CafeProductoId == p.Id)))
                   || _db.CafeStockPorDeposito.Any(s => s.DepositoId == depositoId && s.ProductoId == p.Id && (s.StockUnidades > 0 || s.StockGramos > 0))
                select p.Id
            ).Distinct().ToListAsync();

            baseQ = baseQ.Where(p => prodIdsFull.Contains(p.Id));
        }

        var query = from p in baseQ
                    join s in _db.CafeStockPorDeposito
                        on new { ProductoId = p.Id, DepositoId = depositoId }
                        equals new { s.ProductoId, s.DepositoId } into ss
                    from s in ss.DefaultIfEmpty()
                    orderby p.Categoria descending, p.Nombre
                    select new StockProductoDto(
                        p.Id, p.Sku ?? "", p.Nombre, p.Categoria,
                        s != null ? s.StockGramos : 0m,
                        s != null ? s.StockUnidades : 0, null, p.IsActive);

        var list = await query.ToListAsync();
        // 2026-09-28: productos "shell" (caja + tapa) muestran de qué piezas están hechos.
        var composicion = await ComposicionProductos.ShellsAsync(_db);
        if (composicion.Count > 0)
            list = list.Select(r => composicion.TryGetValue(r.ProductoId, out var c) ? r with { Composicion = c } : r).ToList();
        return Ok(list);
    }

    public record UpdateStockItem(int ProductoId, decimal StockGramos, int StockUnidades);
    // 2026-09-28: Comentario opcional (ej: "factura Colombraro 1234") que queda en el historial de cada producto.
    public record UpdateStockMasivoReq(int DepositoId, List<UpdateStockItem> Items, string? Comentario = null);

    /// <summary>
    /// Actualiza el stock de varios productos en un deposito.
    /// Tambien actualiza Cafe_Productos.StockGramos/Unidades como el TOTAL across all depositos.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> Actualizar([FromBody] UpdateStockMasivoReq req)
    {
        var dep = await _db.CafeDepositos.FindAsync(req.DepositoId);
        if (dep is null) return BadRequest(new { error = "Deposito no existe" });
        if (req.Items == null || req.Items.Count == 0) return BadRequest(new { error = "Sin items" });

        var productoIds = req.Items.Select(i => i.ProductoId).Distinct().ToList();
        var existingMap = await _db.CafeStockPorDeposito
            .Where(s => s.DepositoId == req.DepositoId && productoIds.Contains(s.ProductoId))
            .ToDictionaryAsync(s => s.ProductoId);
        var productosMap = await _db.CafeProductos
            .Where(p => productoIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        // 2026-06-02: capturamos el operador del usuario logueado para que quede en el historial.
        // El JWT trae el username en el claim "name" o "unique_name".
        var operadorNombre = User?.Identity?.Name ?? User?.FindFirst("name")?.Value ?? "admin";

        // Tracking de stock antes/después por producto para loguear en Stock_Movimientos al final.
        var changesParaHistorial = new List<(int ProdId, int AntesU, int DespuesU)>();

        int cambios = 0;
        foreach (var item in req.Items)
        {
            if (!productosMap.TryGetValue(item.ProductoId, out var prod)) continue;

            // 1) Actualizar / crear el registro StockPorDeposito
            int antesU = prod.StockUnidades;  // capturamos el TOTAL antes (sera el "antes" del historial)
            if (existingMap.TryGetValue(item.ProductoId, out var spd))
            {
                if (spd.StockGramos == item.StockGramos && spd.StockUnidades == item.StockUnidades) continue;
                spd.StockGramos = item.StockGramos;
                spd.StockUnidades = item.StockUnidades;
                spd.UpdatedAt = DateTime.UtcNow;
            }
            else
            {
                _db.CafeStockPorDeposito.Add(new CafeStockPorDeposito
                {
                    ProductoId = item.ProductoId,
                    DepositoId = req.DepositoId,
                    StockGramos = item.StockGramos,
                    StockUnidades = item.StockUnidades
                });
            }
            cambios++;
        }
        await _db.SaveChangesAsync();

        // 2) Re-calcular el TOTAL en Cafe_Productos (suma de todos los depositos)
        var productosModificados = new List<int>();
        if (cambios > 0)
        {
            var totales = await _db.CafeStockPorDeposito
                .Where(s => productoIds.Contains(s.ProductoId))
                .GroupBy(s => s.ProductoId)
                .Select(g => new { ProductoId = g.Key, TotalG = g.Sum(x => x.StockGramos), TotalU = g.Sum(x => x.StockUnidades) })
                .ToListAsync();
            foreach (var t in totales)
            {
                if (productosMap.TryGetValue(t.ProductoId, out var prod))
                {
                    var antesU = prod.StockUnidades;  // total ANTES del re-cálculo (lo que tenía sumando todos los depósitos)
                    var changed = (prod.StockGramos != t.TotalG) || (prod.StockUnidades != t.TotalU);
                    prod.StockGramos = t.TotalG;
                    prod.StockUnidades = t.TotalU;
                    prod.UpdatedAt = DateTime.UtcNow;
                    if (changed)
                    {
                        prod.StockChangedAt = DateTime.UtcNow;  // ← dispara que el push event-driven y el background lo procesen
                        productosModificados.Add(prod.Id);
                        changesParaHistorial.Add((prod.Id, antesU, t.TotalU));
                    }
                }
            }
            await _db.SaveChangesAsync();

            // 2026-06-02: registrar cada cambio en Stock_Movimientos para que aparezca en
            // /cafe/historial-stock. Tipo "SUMA" o "RESTA" segun el delta. Antes este endpoint
            // actualizaba el stock pero no logueaba nada -> el historial quedaba ciego para los
            // cambios masivos desde la pantalla web (solo aparecian los cambios desde mobile).
            foreach (var (prodId, antesU, despuesU) in changesParaHistorial)
            {
                if (antesU == despuesU) continue;
                var tipoMov = despuesU > antesU ? "SUMA" : "RESTA";
                await _stockLogger.LogAsync(prodId, tipoMov, antesU, despuesU,
                    operadorId: null,
                    operadorNombre: operadorNombre,
                    comentario: string.IsNullOrWhiteSpace(req.Comentario)
                        ? $"Stock masivo · {dep.Nombre}"
                        : $"Stock masivo · {dep.Nombre} · {req.Comentario.Trim()}",
                    saveChanges: false);
            }
            if (changesParaHistorial.Count > 0) await _db.SaveChangesAsync();
        }

        // 3) Push event-driven a MeLi para los productos que cambiaron
        FireAndForgetPushMeli(productosModificados);

        await _audit.LogAsync("CafeStockMasivo", req.DepositoId.ToString(), "BULK_UPDATE",
            $"Actualizados {cambios} productos en deposito {dep.Nombre}");

        return Ok(new { actualizados = cambios });
    }

    // ───────────────────────── PLANILLA EXCEL QUE SUMA ─────────────────────────
    // 30/10/2026: planilla para cargar mercaderia que llega. Hoja "Carga": se escribe el codigo y la
    // cantidad que llega; nombre y stock se completan solos (BUSCARV contra la hoja "Productos").
    // Al subirla SIEMPRE suma al stock que hay en ese momento (no al que tenia el Excel al bajarlo).
    // Un compuesto (ej. cesto = recipiente + tapa) suma N x lo que lleva cada pieza.
    // Si el mismo contenido ya se subio antes, avisa y pide confirmar (para no sumar dos veces).

    private const int FilasCarga = 300;

    public record PlanillaLineaDto(string Codigo, string Nombre, string Categoria,
        decimal Antes, decimal Suma, decimal Despues, string Origen);
    public record PlanillaYaSubidaDto(DateTime Fecha, string? Usuario);
    public record PlanillaPreviewDto(int FilasLeidas, List<PlanillaLineaDto> Lineas, List<string> Errores,
        PlanillaYaSubidaDto? YaSubida);

    private sealed record FilaPlanilla(int Fila, string Codigo, decimal Cantidad);

    [HttpGet("planilla/{depositoId:int}")]
    public async Task<IActionResult> BajarPlanilla(int depositoId)
    {
        var dep = await _db.CafeDepositos.AsNoTracking().FirstOrDefaultAsync(d => d.Id == depositoId);
        if (dep is null) return NotFound(new { error = "Deposito no encontrado" });

        var stock = await _db.CafeStockPorDeposito.AsNoTracking().Where(s => s.DepositoId == depositoId)
            .ToDictionaryAsync(s => s.ProductoId);
        var prods = await _db.CafeProductos.AsNoTracking().Where(p => p.IsActive && p.Sku != null && p.Sku != "")
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.Categoria }).ToListAsync();
        var lista = prods.Select(p =>
        {
            stock.TryGetValue(p.Id, out var s);
            var st = p.Categoria == "CAFE" ? (s?.StockGramos ?? 0m) / 1000m : (s?.StockUnidades ?? 0);
            return (Codigo: p.Sku!.Trim(), p.Nombre, Stock: (decimal?)st, Tipo: p.Categoria == "CAFE" ? "café (kg)" : "unidades");
        }).ToList();
        foreach (var c in await CompuestosAsync())
            lista.Add((c.Sku, c.Nombre, null, "compuesto: " + c.Detalle));
        lista = lista.OrderBy(x => x.Nombre).ToList();

        using var wb = new ClosedXML.Excel.XLWorkbook();
        var ws = wb.Worksheets.Add("Carga");
        var wp = wb.Worksheets.Add("Productos");

        // Hoja Productos: la "base de datos" contra la que busca la hoja Carga.
        string[] hp = { "Código", "Producto", $"Stock hoy ({dep.Nombre})", "Se carga en" };
        for (int i = 0; i < hp.Length; i++) { wp.Cell(1, i + 1).Value = hp[i]; wp.Cell(1, i + 1).Style.Font.Bold = true; }
        int r = 2;
        foreach (var p in lista)
        {
            wp.Cell(r, 1).SetValue(p.Codigo);   // como texto: "0101" no pierde el cero
            wp.Cell(r, 2).Value = p.Nombre;
            if (p.Stock.HasValue) wp.Cell(r, 3).Value = p.Stock.Value;
            wp.Cell(r, 4).Value = p.Tipo;
            r++;
        }
        int ultima = Math.Max(2, r - 1);
        wp.SheetView.FreezeRows(1);
        wp.Range(1, 1, ultima, 4).SetAutoFilter();
        wp.Column(1).Width = 16; wp.Column(2).Width = 55; wp.Column(3).Width = 20; wp.Column(4).Width = 40;

        // Hoja Carga: el usuario escribe A (código) y D (cantidad); B y C se completan solos.
        string[] hc = { "Código (escribilo vos)", "Producto (sale solo)", "Stock hoy (sale solo)", "Cantidad que llega (vos)" };
        var amarillo = ClosedXML.Excel.XLColor.FromHtml("#fef08a");
        var gris = ClosedXML.Excel.XLColor.FromHtml("#e5e7eb");
        for (int i = 0; i < hc.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = hc[i];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = (i == 0 || i == 3) ? amarillo : gris;
        }
        ws.Cell(1, 4).CreateComment().AddText("Unidades. Si es café, en kg (ej: 2,5). Se SUMA a lo que haya en el sistema al subir la planilla.");
        string rango = $"Productos!$A$2:$C${ultima}";
        for (int f = 2; f <= FilasCarga + 1; f++)
        {
            ws.Cell(f, 2).FormulaA1 = $"IF(TRIM(A{f}&\"\")=\"\",\"\",IFERROR(VLOOKUP(TRIM(A{f}&\"\"),{rango},2,FALSE),\"❌ CÓDIGO NO EXISTE\"))";
            ws.Cell(f, 3).FormulaA1 = $"IF(TRIM(A{f}&\"\")=\"\",\"\",IFERROR(VLOOKUP(TRIM(A{f}&\"\"),{rango},3,FALSE)&\"\",\"\"))";
        }
        ws.Range(2, 1, FilasCarga + 1, 1).Style.NumberFormat.Format = "@";   // así "0101" queda como texto
        ws.Range(2, 2, FilasCarga + 1, 3).Style.Font.FontColor = ClosedXML.Excel.XLColor.FromHtml("#374151");
        ws.Range(2, 2, FilasCarga + 1, 2).AddConditionalFormat().WhenStartsWith("❌")
            .Fill.SetBackgroundColor(ClosedXML.Excel.XLColor.FromHtml("#fecaca"));
        ws.Range(2, 3, FilasCarga + 1, 3).Style.Alignment.Horizontal = ClosedXML.Excel.XLAlignmentHorizontalValues.Right;
        ws.Column(1).Width = 22; ws.Column(2).Width = 55; ws.Column(3).Width = 20; ws.Column(4).Width = 24;
        ws.SheetView.FreezeRows(1);
        ws.Cell(2, 1).SetActive();

        // Hoja oculta: de qué depósito es la planilla (al subirla se avisa si no coincide).
        var wi = wb.Worksheets.Add("info");
        wi.Cell(1, 1).Value = "depositoId";
        wi.Cell(1, 2).Value = depositoId;
        wi.Visibility = ClosedXML.Excel.XLWorksheetVisibility.VeryHidden;

        wb.FullCalculationOnLoad = true;
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var nombreDep = new string(dep.Nombre.Where(char.IsLetterOrDigit).ToArray());
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"planilla-carga-stock-{nombreDep}-{PanoramaService.AhoraAr():dd-MM-yyyy}.xlsx");
    }

    private sealed record CompuestoInfo(string Sku, string Nombre, string Detalle, List<(int ProductoId, int Cantidad)> Piezas);

    /// <summary>Compuestos (no packs) con todas sus piezas de "unidades": se cargan sumando a cada pieza.</summary>
    private async Task<List<CompuestoInfo>> CompuestosAsync()
    {
        var combos = await _db.CafeCombos.AsNoTracking().Where(c => c.IsActive && c.EsCompuesto && c.Sku != null && c.Sku != "")
            .Select(c => new { c.Id, c.Sku, c.Nombre }).ToListAsync();
        if (combos.Count == 0) return new();
        var ids = combos.Select(c => c.Id).ToList();
        var items = await _db.CafeComboItems.AsNoTracking().Where(i => ids.Contains(i.ComboId))
            .Select(i => new { i.ComboId, i.ProductoId, i.Cantidad, Nombre = i.ProductoNav!.Nombre, Cat = i.ProductoNav!.Categoria })
            .ToListAsync();
        var res = new List<CompuestoInfo>();
        foreach (var c in combos)
        {
            var its = items.Where(i => i.ComboId == c.Id).ToList();
            if (its.Count == 0 || its.Any(i => i.Cat == "CAFE")) continue;
            if (System.Text.RegularExpressions.Regex.IsMatch(c.Sku!.Trim(), @"-X\d+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) continue;
            int mcd = 0;
            foreach (var i in its) { int a = mcd, b = Math.Abs(i.Cantidad); while (b != 0) (a, b) = (b, a % b); mcd = a; }
            if (mcd > 1) continue;   // es un pack (N del mismo), no un compuesto
            res.Add(new CompuestoInfo(c.Sku!.Trim(), c.Nombre,
                string.Join(" + ", its.Select(i => (i.Cantidad > 1 ? i.Cantidad + "× " : "") + i.Nombre)),
                its.Select(i => (i.ProductoId, i.Cantidad)).ToList()));
        }
        return res;
    }

    private static List<FilaPlanilla> LeerPlanilla(Stream stream, List<string> errores, out int? depositoArchivo)
    {
        depositoArchivo = null;
        var filas = new List<FilaPlanilla>();
        using var wb = new ClosedXML.Excel.XLWorkbook(stream);
        if (wb.TryGetWorksheet("info", out var wi) && wi.Cell(1, 2).TryGetValue<int>(out var depId)) depositoArchivo = depId;
        var ws = wb.TryGetWorksheet("Carga", out var wc) ? wc : wb.Worksheets.First();
        var last = ws.LastRowUsed()?.RowNumber() ?? 1;
        for (int f = 2; f <= last; f++)
        {
            var codigo = ws.Cell(f, 1).GetFormattedString().Trim();
            var cCant = ws.Cell(f, 4);
            if (codigo == "" && cCant.IsEmpty()) continue;
            if (codigo == "") { errores.Add($"Fila {f}: tiene cantidad pero no código."); continue; }
            decimal cant;
            if (cCant.IsEmpty()) { errores.Add($"Fila {f} ({codigo}): falta la cantidad."); continue; }
            if (cCant.DataType == ClosedXML.Excel.XLDataType.Number) cant = (decimal)cCant.GetDouble();
            else
            {
                var s = cCant.GetString().Trim().Replace(" ", "").Replace(",", ".");
                if (!decimal.TryParse(s, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out cant))
                { errores.Add($"Fila {f} ({codigo}): la cantidad \"{cCant.GetString()}\" no es un número."); continue; }
            }
            if (cant <= 0) { errores.Add($"Fila {f} ({codigo}): la cantidad tiene que ser mayor a 0 (esta planilla solo suma)."); continue; }
            filas.Add(new FilaPlanilla(f, codigo, cant));
        }
        return filas;
    }

    private static string HashPlanilla(int depositoId, List<FilaPlanilla> filas)
    {
        var txt = depositoId + "|" + string.Join(";", filas
            .GroupBy(x => x.Codigo.ToUpperInvariant())
            .OrderBy(g => g.Key)
            .Select(g => g.Key + ":" + g.Sum(x => x.Cantidad).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(txt)))[..32];
    }

    /// <summary>Lee la planilla y arma cuánto suma a cada producto (los compuestos se reparten en sus piezas).
    /// Las cuentas se hacen contra el stock de AHORA en la base.</summary>
    private async Task<(List<FilaPlanilla> Filas, List<PlanillaLineaDto> Lineas, Dictionary<int, decimal> Deltas, List<string> Errores, string? Fatal)>
        ArmarPlanillaAsync(int depositoId, IFormFile? file)
    {
        var errores = new List<string>();
        if (file is null || file.Length == 0) return (new(), new(), new(), errores, "Subí el archivo .xlsx de la planilla.");
        var dep = await _db.CafeDepositos.AsNoTracking().FirstOrDefaultAsync(d => d.Id == depositoId);
        if (dep is null) return (new(), new(), new(), errores, "Depósito no encontrado.");
        if (dep.Nombre == "Full MeLi") return (new(), new(), new(), errores, "El depósito Full se sincroniza solo desde MeLi, no se le puede cargar stock.");

        List<FilaPlanilla> filas;
        int? depArchivo;
        try { using var s = file.OpenReadStream(); filas = LeerPlanilla(s, errores, out depArchivo); }
        catch (Exception ex) { return (new(), new(), new(), errores, "No pude leer el Excel: " + ex.Message); }
        if (depArchivo is not null && depArchivo != depositoId)
        {
            var otro = await _db.CafeDepositos.AsNoTracking().Where(d => d.Id == depArchivo).Select(d => d.Nombre).FirstOrDefaultAsync();
            errores.Insert(0, $"Ojo: esta planilla la bajaste para el depósito \"{otro ?? "?"}\" y la estás subiendo en \"{dep.Nombre}\". Se va a sumar en \"{dep.Nombre}\".");
        }

        var prods = await _db.CafeProductos.AsNoTracking().Where(p => p.IsActive && p.Sku != null && p.Sku != "")
            .Select(p => new { p.Id, p.Sku, p.Nombre, p.Categoria }).ToListAsync();
        var porSku = prods.GroupBy(p => p.Sku!.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var porId = prods.ToDictionary(p => p.Id);
        var comps = (await CompuestosAsync()).GroupBy(c => c.Sku, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        // cuánto suma cada producto (en kg para café, unidades para el resto) y de dónde viene
        var deltas = new Dictionary<int, decimal>();
        var origen = new Dictionary<int, List<string>>();
        void Sumar(int pid, decimal cant, string de)
        {
            deltas[pid] = deltas.GetValueOrDefault(pid) + cant;
            if (!origen.TryGetValue(pid, out var l)) origen[pid] = l = new();
            l.Add(de);
        }
        foreach (var f in filas)
        {
            if (porSku.TryGetValue(f.Codigo, out var p))
            {
                if (p.Categoria != "CAFE" && f.Cantidad != Math.Floor(f.Cantidad))
                { errores.Add($"Fila {f.Fila} ({f.Codigo}): {p.Nombre} va en unidades, no puede llevar decimales ({f.Cantidad:0.###})."); continue; }
                Sumar(p.Id, f.Cantidad, $"fila {f.Fila}");
            }
            else if (comps.TryGetValue(f.Codigo, out var c))
            {
                if (f.Cantidad != Math.Floor(f.Cantidad)) { errores.Add($"Fila {f.Fila} ({f.Codigo}): un compuesto no puede llevar decimales."); continue; }
                foreach (var (pid, n) in c.Piezas) Sumar(pid, f.Cantidad * n, $"fila {f.Fila} ({c.Sku} × {f.Cantidad:0})");
            }
            else errores.Add($"Fila {f.Fila}: el código \"{f.Codigo}\" no existe. No se carga.");
        }

        var ids = deltas.Keys.ToList();
        var stock = await _db.CafeStockPorDeposito.AsNoTracking()
            .Where(s => s.DepositoId == depositoId && ids.Contains(s.ProductoId)).ToDictionaryAsync(s => s.ProductoId);
        var lineas = new List<PlanillaLineaDto>();
        foreach (var (pid, suma) in deltas)
        {
            var p = porId[pid];
            stock.TryGetValue(pid, out var s);
            decimal antes = p.Categoria == "CAFE" ? (s?.StockGramos ?? 0m) / 1000m : (s?.StockUnidades ?? 0);
            lineas.Add(new PlanillaLineaDto(p.Sku!.Trim(), p.Nombre, p.Categoria, antes, suma, antes + suma, string.Join(", ", origen[pid])));
        }
        return (filas, lineas.OrderBy(l => l.Nombre).ToList(), deltas, errores, null);
    }

    private async Task<PlanillaYaSubidaDto?> BuscarYaSubidaAsync(string hash)
    {
        var a = await _db.AuditLogs.AsNoTracking()
            .Where(x => x.EntityType == "CafeStockPlanilla" && x.EntityId == hash && x.Action == "SUMA")
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync();
        return a is null ? null : new PlanillaYaSubidaDto(a.CreatedAt, a.UserName);
    }

    [HttpPost("planilla/{depositoId:int}/preview")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> PreviewPlanilla(int depositoId, [FromForm] IFormFile? file)
    {
        var (filas, lineas, _, errores, fatal) = await ArmarPlanillaAsync(depositoId, file);
        if (fatal is not null) return BadRequest(new { error = fatal });
        var ya = filas.Count == 0 ? null : await BuscarYaSubidaAsync(HashPlanilla(depositoId, filas));
        return Ok(new PlanillaPreviewDto(filas.Count, lineas, errores, ya));
    }

    [HttpPost("planilla/{depositoId:int}/aplicar")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> AplicarPlanilla(int depositoId, [FromForm] IFormFile? file,
        [FromForm] string? comentario, [FromForm] bool confirmarRepetida = false)
    {
        var (filas, _, deltas, _, fatal) = await ArmarPlanillaAsync(depositoId, file);
        if (fatal is not null) return BadRequest(new { error = fatal });
        if (deltas.Count == 0) return BadRequest(new { error = "La planilla no tiene ningún producto válido para cargar." });
        var hash = HashPlanilla(depositoId, filas);
        var ya = await BuscarYaSubidaAsync(hash);
        if (ya is not null && !confirmarRepetida)
            return Conflict(new { error = $"Esta planilla ya se subió el {ya.Fecha.AddHours(-3):dd/MM HH:mm}. Confirmá si querés sumarla otra vez." });

        // Las cuentas se hacen recién acá, contra el stock de este momento: si se vendió algo
        // desde que se bajó la planilla, se respeta (solo se suma lo que llegó).
        var ids = deltas.Keys.ToList();
        var stock = await _db.CafeStockPorDeposito.AsNoTracking()
            .Where(s => s.DepositoId == depositoId && ids.Contains(s.ProductoId)).ToDictionaryAsync(s => s.ProductoId);
        var cats = await _db.CafeProductos.AsNoTracking().Where(p => ids.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Categoria);
        var items = deltas.Select(kv =>
        {
            stock.TryGetValue(kv.Key, out var s);
            var g = s?.StockGramos ?? 0m;
            var u = s?.StockUnidades ?? 0;
            if (cats[kv.Key] == "CAFE") g += kv.Value * 1000m; else u += (int)kv.Value;
            return new UpdateStockItem(kv.Key, g, u);
        }).ToList();

        var coment = "Planilla Excel" + (string.IsNullOrWhiteSpace(comentario) ? "" : " · " + comentario.Trim());
        var res = await Actualizar(new UpdateStockMasivoReq(depositoId, items, coment));
        if (res is not OkObjectResult) return res;

        await _audit.LogAsync("CafeStockPlanilla", hash, "SUMA",
            $"Depósito {depositoId} · {deltas.Count} productos · {coment}",
            User?.Identity?.Name ?? User?.FindFirst("name")?.Value);
        return Ok(new { actualizados = deltas.Count });
    }
}
