using Api.Data;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace Api.Controllers;

/// <summary>2026-09-02: "Stock ideal" — cuantas unidades queremos tener SIEMPRE de cada producto.
/// Sirve para armar los pedidos al proveedor: los productos que quedaron por debajo de su ideal
/// aparecen en la lista de faltantes con cuanto falta de cada uno, y de ahi sale el Excel del pedido.
///
/// OJO: no confundir con StockMinimoMeLi (la reserva que se le esconde a MeLi). Son dos numeros
/// distintos a proposito: si se usara uno solo, poner un ideal alto dejaria a MeLi sin stock.
///
/// Endpoints:
///   GET  /lista        -> filas (todas o solo las que faltan) con stock, ideal, faltan y ultima entrada.
///   PUT  /bulk         -> guarda varios ideales de una (la planilla de la pantalla).
///   GET  /pedido.xlsx  -> Excel del pedido, solo los faltantes, con los mismos filtros de /lista.
///   GET  /export       -> Excel para configurar ideales (bajar -> editar -> subir).
///   POST /preview      -> lee el Excel editado y muestra que cambiaria (dry-run, NO toca la base).
///   POST /apply        -> aplica esos cambios.
/// El emparejamiento del Excel es por la columna 'id'; si falta, cae al 'codigo' (Sku).</summary>
[ApiController]
[Route("api/stock/ideal")]
[Authorize]
public class StockIdealController : ControllerBase
{
    private readonly AppDbContext _db;
    public StockIdealController(AppDbContext db) { _db = db; }

    // ───────────────────────── DTOs ─────────────────────────

    /// <summary>Una fila de la pantalla. OJO con la unidad: los productos de CAFE llevan el stock
    /// en GRAMOS (StockUnidades siempre 0), asi que para ellos el stock y el ideal se cuentan en
    /// KILOS. Los de OTROS van en unidades. Por eso StockActual es decimal y viaja la Unidad.</summary>
    public record StockIdealRow(
        int ProductoId, string? Codigo, string Nombre, string? Marca, string? Categoria,
        decimal StockActual, int? StockIdeal, decimal Faltan, string Unidad, DateTime? UltimaEntrada);

    public record StockIdealListResult(
        int Total, int ConIdeal, int Faltantes, int EnCero, decimal UnidadesFaltantes,
        List<string> Marcas, List<StockIdealRow> Filas);

    public record StockIdealBulkItem(int ProductoId, int? StockIdeal);
    public record StockIdealBulkRequest(List<StockIdealBulkItem> Items);
    public record StockIdealBulkResult(int Actualizados, int Quitados, int NoEncontrados);

    public record StockIdealCambioDto(
        int ProductoId, string? Codigo, string Descripcion,
        int? IdealViejo, int? IdealNuevo, bool Asigna, bool Quita);

    public record StockIdealPreviewDto(
        int TotalFilas, int SinCambios, int Asignan, int Quitan, int NoEncontrados,
        List<StockIdealCambioDto> Cambios, List<string> Errores);

    public record StockIdealApplyResultDto(
        int Actualizados, int Quitados, int NoEncontrados, List<string> Errores);

    // Proyeccion liviana: lo justo para armar las filas sin traer la entidad entera.
    private record ProdInfo(int Id, string? Sku, string Nombre, string? Marca, string? Categoria,
        int StockUnidades, decimal StockGramos, int? StockIdeal);

    /// <summary>El cafe se mide en kilos (viene guardado en gramos); todo lo demas, en unidades.</summary>
    private static bool EsCafe(string? categoria)
        => string.Equals(categoria, "CAFE", StringComparison.OrdinalIgnoreCase);

    private static decimal StockDe(ProdInfo p)
        => EsCafe(p.Categoria) ? Math.Round(p.StockGramos / 1000m, 2) : p.StockUnidades;

    private static string UnidadDe(string? categoria) => EsCafe(categoria) ? "kg" : "u";

    // ───────────────────────── LISTA ─────────────────────────

    /// <summary>GET /api/stock/ideal/lista — una fila por producto activo. Con soloFaltantes=true
    /// devuelve unicamente los que tienen ideal cargado y estan por debajo.</summary>
    [HttpGet("lista")]
    public async Task<IActionResult> Lista(
        [FromQuery] bool soloFaltantes = true,
        [FromQuery] string? marca = null,
        [FromQuery] string? q = null,
        [FromQuery] string? categoria = null)
    {
        var prods = await CargarProductosAsync(marca, q, categoria);

        // Todas las marcas activas (para el combo del filtro), sin depender de los filtros actuales.
        var marcasRaw = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => new { p.Marca, MarcaTabla = p.MarcaNav != null ? p.MarcaNav.Nombre : null })
            .ToListAsync();
        var marcas = marcasRaw
            .Select(m => m.Marca ?? m.MarcaTabla)
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Ultima entrada de stock, solo de los productos que vamos a mostrar.
        var ids = prods.Select(p => p.Id).ToList();
        var ultimasEntradas = await UltimasEntradasAsync(ids);

        var filas = prods
            .Select(p =>
            {
                var stock = StockDe(p);
                return new StockIdealRow(
                    p.Id, p.Sku, p.Nombre, p.Marca, p.Categoria,
                    stock, p.StockIdeal, Faltante(p.StockIdeal, stock), UnidadDe(p.Categoria),
                    ultimasEntradas.TryGetValue(p.Id, out var f) ? f : null);
            })
            .ToList();

        // Los totales se calculan SIEMPRE sobre todo lo filtrado, aunque despues mostremos
        // solo los faltantes: asi el resumen de arriba no cambia al tildar la casilla.
        int conIdeal = filas.Count(f => f.StockIdeal.HasValue);
        var faltantes = filas.Where(f => f.Faltan > 0).ToList();
        int enCero = faltantes.Count(f => f.StockActual <= 0);
        decimal unidadesFaltantes = faltantes.Sum(f => f.Faltan);

        var visibles = soloFaltantes ? faltantes : filas;

        // Los que mas faltan primero; los que estan en cero, arriba de todo.
        visibles = visibles
            .OrderByDescending(f => f.StockActual <= 0 && f.Faltan > 0)
            .ThenByDescending(f => f.Faltan)
            .ThenBy(f => f.Marca ?? "")
            .ThenBy(f => f.Nombre)
            .ToList();

        return Ok(new StockIdealListResult(
            filas.Count, conIdeal, faltantes.Count, enCero, unidadesFaltantes,
            marcas.Select(m => m!).ToList(), visibles));
    }

    /// <summary>Cuanto falta para llegar al ideal. Sin ideal cargado no se controla (0).</summary>
    private static decimal Faltante(int? ideal, decimal stock)
        => ideal.HasValue ? Math.Max(0m, ideal.Value - stock) : 0m;

    private async Task<List<ProdInfo>> CargarProductosAsync(string? marca, string? q, string? categoria)
    {
        var query = _db.CafeProductos.AsNoTracking().Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(categoria))
        {
            var cat = categoria.Trim().ToUpperInvariant();
            query = query.Where(p => p.Categoria == cat);
        }
        if (!string.IsNullOrWhiteSpace(marca))
        {
            var m = marca.Trim();
            // Se escribe como dos condiciones (y no con ??) para que EF lo pueda pasar a SQL.
            query = query.Where(p =>
                (p.Marca != null && p.Marca == m) ||
                (p.Marca == null && p.MarcaNav != null && p.MarcaNav.Nombre == m));
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            query = query.Where(p => p.Nombre.Contains(t) || (p.Sku != null && p.Sku.Contains(t)));
        }

        // OJO: el OrderBy por marca NO va en el query. La marca sale de dos lados (el texto suelto
        // o la tabla de marcas) y EF no sabe traducir ese "uno u otro" a SQL → tira 500.
        var lista = await query
            .Select(p => new ProdInfo(
                p.Id, p.Sku, p.Nombre,
                p.Marca ?? (p.MarcaNav != null ? p.MarcaNav.Nombre : null),
                p.Categoria, p.StockUnidades, p.StockGramos, p.StockIdeal))
            .ToListAsync();

        return lista
            .OrderBy(p => p.Marca ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Nombre, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Fecha de la ultima entrada de stock de cada producto (movimientos que SUMARON
    /// y no fueron deshechos). Mismo criterio que la tarjeta "ultimo ingreso" de Stock.</summary>
    private async Task<Dictionary<int, DateTime>> UltimasEntradasAsync(List<int> productoIds)
    {
        if (productoIds.Count == 0) return new();
        return await _db.StockMovimientos.AsNoTracking()
            .Where(m => productoIds.Contains(m.ProductoId) && !m.Reverted && m.StockDespues > m.StockAntes)
            .GroupBy(m => m.ProductoId)
            .Select(g => new { ProductoId = g.Key, Fecha = g.Max(m => m.CreatedAt) })
            .ToDictionaryAsync(x => x.ProductoId, x => x.Fecha);
    }

    // ───────────────────────── GUARDAR (planilla) ─────────────────────────

    /// <summary>PUT /api/stock/ideal/bulk — guarda varios ideales de una. StockIdeal null saca el ideal.</summary>
    [HttpPut("bulk")]
    public async Task<IActionResult> Bulk([FromBody] StockIdealBulkRequest req)
    {
        if (req?.Items is null || req.Items.Count == 0)
            return Ok(new StockIdealBulkResult(0, 0, 0));

        var ids = req.Items.Select(i => i.ProductoId).Distinct().ToList();
        var prods = await _db.CafeProductos.Where(p => ids.Contains(p.Id)).ToListAsync();
        var dic = prods.ToDictionary(p => p.Id);

        int actualizados = 0, quitados = 0, noEncontrados = 0;
        foreach (var item in req.Items)
        {
            if (!dic.TryGetValue(item.ProductoId, out var p)) { noEncontrados++; continue; }

            int? nuevo = item.StockIdeal.HasValue ? Math.Max(0, item.StockIdeal.Value) : (int?)null;
            if (p.StockIdeal == nuevo) continue;

            p.StockIdeal = nuevo;
            p.UpdatedAt = DateTime.UtcNow;
            if (nuevo.HasValue) actualizados++; else quitados++;
        }

        if (actualizados > 0 || quitados > 0) await _db.SaveChangesAsync();
        return Ok(new StockIdealBulkResult(actualizados, quitados, noEncontrados));
    }

    // ───────────────────────── EXCEL DEL PEDIDO ─────────────────────────

    /// <summary>GET /api/stock/ideal/pedido.xlsx — Excel con los faltantes para mandarle al proveedor.</summary>
    [HttpGet("pedido.xlsx")]
    public async Task<IActionResult> ExportPedido(
        [FromQuery] string? marca = null,
        [FromQuery] string? q = null,
        [FromQuery] string? categoria = null)
    {
        var prods = await CargarProductosAsync(marca, q, categoria);
        var faltantes = prods
            .Select(p => new { P = p, Stock = StockDe(p), Faltan = Faltante(p.StockIdeal, StockDe(p)) })
            .Where(x => x.Faltan > 0)
            .OrderBy(x => x.P.Marca).ThenBy(x => x.P.Nombre)
            .ToList();

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Pedido");

        var hoyAr = DateTime.UtcNow.AddHours(-3);
        ws.Cell(1, 1).Value = "PEDIDO A PROVEEDOR";
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 14;
        ws.Cell(2, 1).Value = $"Generado el {hoyAr:dd/MM/yyyy HH:mm}"
            + (string.IsNullOrWhiteSpace(marca) ? "" : $" · Marca: {marca}");
        ws.Cell(2, 1).Style.Font.FontColor = XLColor.FromHtml("#6b7280");

        var headers = new[] { "codigo", "producto", "marca", "stock actual", "stock ideal", "PEDIR", "unidad" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(4, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            cell.Style.Fill.BackgroundColor = (i == 5)
                ? XLColor.FromHtml("#dbeafe") : XLColor.FromHtml("#e5e7eb");
            // (la columna 'unidad' aclara si se pide en unidades o en kilos — el cafe va en kilos)
        }

        int r = 5;
        foreach (var x in faltantes)
        {
            ws.Cell(r, 1).Value = x.P.Sku ?? "";
            ws.Cell(r, 2).Value = x.P.Nombre;
            ws.Cell(r, 3).Value = x.P.Marca ?? "";
            ws.Cell(r, 4).Value = x.Stock;
            ws.Cell(r, 5).Value = x.P.StockIdeal ?? 0;
            ws.Cell(r, 6).Value = x.Faltan;
            ws.Cell(r, 6).Style.Font.Bold = true;
            ws.Cell(r, 6).Style.Fill.BackgroundColor = XLColor.FromHtml("#eff6ff");
            ws.Cell(r, 7).Value = UnidadDe(x.P.Categoria) == "kg" ? "kilos" : "unidades";
            // Los que estan en cero van marcados: son los urgentes.
            if (x.Stock <= 0)
                ws.Cell(r, 4).Style.Font.FontColor = XLColor.FromHtml("#b91c1c");
            r++;
        }

        if (faltantes.Count > 0)
        {
            ws.Cell(r, 5).Value = "TOTAL";
            ws.Cell(r, 5).Style.Font.Bold = true;
            // Solo se suma si todo el pedido va en la misma unidad: sumar kilos con unidades no significa nada.
            var unidades = faltantes.Select(x => UnidadDe(x.P.Categoria)).Distinct().ToList();
            if (unidades.Count == 1)
            {
                ws.Cell(r, 6).Value = faltantes.Sum(x => x.Faltan);
                ws.Cell(r, 7).Value = unidades[0] == "kg" ? "kilos" : "unidades";
            }
            else ws.Cell(r, 6).Value = "—";
            ws.Cell(r, 6).Style.Font.Bold = true;
            ws.Range(r, 1, r, 7).Style.Border.TopBorder = XLBorderStyleValues.Thin;
        }
        else
        {
            ws.Cell(5, 1).Value = "No hay productos por debajo del stock ideal.";
        }

        ws.SheetView.FreezeRows(4);
        ws.Columns().AdjustToContents();
        foreach (var c in ws.Columns()) if (c.Width < 12) c.Width = 12;
        ws.Column(2).Width = 45;

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"pedido-{hoyAr:yyyyMMdd-HHmm}.xlsx");
    }

    // ───────────────────────── EXCEL DE CONFIGURACION ─────────────────────────

    /// <summary>GET /api/stock/ideal/export — Excel para cargar los ideales de a muchos.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export()
    {
        var prods = await CargarProductosAsync(null, null, null);

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Stock ideal");

        var headers = new[] { "id", "codigo", "descripcion", "marca", "stock_actual", "stock_ideal", "unidad" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            // La columna editable en amarillo; las de solo-lectura en gris.
            cell.Style.Fill.BackgroundColor = (i == 5)
                ? XLColor.FromHtml("#fef08a") : XLColor.FromHtml("#e5e7eb");
        }
        ws.Cell(1, 1).GetComment().AddText("NO TOCAR. Se usa para emparejar el producto al subir el Excel.");
        ws.Cell(1, 2).GetComment().AddText("Codigo del producto (referencia, no se modifica).");
        ws.Cell(1, 5).GetComment().AddText("Stock actual (referencia, no se modifica).");
        ws.Cell(1, 6).GetComment().AddText("EDITA ACA. Cuantas unidades queres tener siempre. Dejalo vacio para no controlar este producto.");

        int r = 2;
        foreach (var p in prods)
        {
            ws.Cell(r, 1).Value = p.Id;
            ws.Cell(r, 2).Value = p.Sku ?? "";
            ws.Cell(r, 3).Value = p.Nombre;
            ws.Cell(r, 4).Value = p.Marca ?? "";
            ws.Cell(r, 5).Value = StockDe(p);
            if (p.StockIdeal.HasValue) ws.Cell(r, 6).Value = p.StockIdeal.Value;
            ws.Cell(r, 7).Value = UnidadDe(p.Categoria) == "kg" ? "kilos" : "unidades";
            ws.Cell(r, 7).Style.Fill.BackgroundColor = XLColor.FromHtml("#f9fafb");
            for (int c = 1; c <= 5; c++) ws.Cell(r, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#f9fafb");
            r++;
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
        foreach (var c in ws.Columns()) if (c.Width < 12) c.Width = 12;
        ws.Column(3).Width = 45;

        var info = wb.Worksheets.Add("LEEME");
        info.Cell(1, 1).Value = "COMO CARGAR EL STOCK IDEAL";
        info.Cell(1, 1).Style.Font.Bold = true;
        info.Cell(1, 1).Style.Font.FontSize = 14;
        int rr = 3;
        void L(string t, bool bold = false) { var cc = info.Cell(rr, 1); cc.Value = t; cc.Style.Font.Bold = bold; rr++; }
        L("1) Edita SOLO la columna amarilla 'stock_ideal' en la hoja 'Stock ideal'.");
        L("2) Poné cuantas unidades queres tener SIEMPRE de ese producto en el deposito.");
        L("   Ejemplo: si ponés 40 y te quedan 18, el producto aparece en Faltantes y dice 'faltan 22'.");
        L("3) Deja la celda VACIA para no controlar ese producto (no va a aparecer nunca en Faltantes).");
        L("   Mira la columna 'unidad': el cafe se cuenta en KILOS y todo lo demas en UNIDADES.");
        L("4) NO cambies la columna 'id' (se usa para reconocer cada producto).");
        L("5) Podes borrar las filas de los productos que no queres tocar: solo se aplican los que dejes.");
        L("6) Guarda el archivo y subilo con el boton 'Subir Excel'. Antes de aplicar vas a ver una vista previa.");
        L("");
        L("OJO: esto NO es el 'stock minimo para MeLi'.", true);
        L("El stock minimo para MeLi son unidades que se le esconden a Mercado Libre.");
        L("El stock ideal es solo para saber cuanto pedirle al proveedor. Son dos numeros distintos.");
        info.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"stock-ideal-{DateTime.UtcNow.AddHours(-3):yyyyMMdd-HHmm}.xlsx");
    }

    // ───────────────────────── PREVIEW / APPLY ─────────────────────────

    /// <summary>POST /api/stock/ideal/preview — dry-run del Excel editado. NO toca la base.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview(IFormFile file)
    {
        var (cambios, errores, noEncontrados, totalFilas) = await LeerExcelAsync(file);
        if (cambios is null) return BadRequest(new { error = errores.FirstOrDefault() ?? "No se pudo leer el archivo." });

        return Ok(new StockIdealPreviewDto(
            totalFilas,
            SinCambios: totalFilas - cambios.Count - noEncontrados,
            Asignan: cambios.Count(c => c.Asigna),
            Quitan: cambios.Count(c => c.Quita),
            NoEncontrados: noEncontrados,
            Cambios: cambios,
            Errores: errores));
    }

    /// <summary>POST /api/stock/ideal/apply — aplica los cambios del Excel.</summary>
    [HttpPost("apply")]
    public async Task<IActionResult> Apply(IFormFile file)
    {
        var (cambios, errores, noEncontrados, _) = await LeerExcelAsync(file);
        if (cambios is null) return BadRequest(new { error = errores.FirstOrDefault() ?? "No se pudo leer el archivo." });

        var ids = cambios.Select(c => c.ProductoId).ToList();
        var prods = await _db.CafeProductos.Where(p => ids.Contains(p.Id)).ToListAsync();
        var dic = prods.ToDictionary(p => p.Id);

        int actualizados = 0, quitados = 0;
        foreach (var c in cambios)
        {
            if (!dic.TryGetValue(c.ProductoId, out var p)) continue;
            p.StockIdeal = c.IdealNuevo;
            p.UpdatedAt = DateTime.UtcNow;
            if (c.IdealNuevo.HasValue) actualizados++; else quitados++;
        }

        if (actualizados > 0 || quitados > 0) await _db.SaveChangesAsync();
        return Ok(new StockIdealApplyResultDto(actualizados, quitados, noEncontrados, errores));
    }

    /// <summary>Lee el Excel y arma la lista de cambios (sin tocar la base). Devuelve null en Cambios
    /// si el archivo no se pudo abrir. Filas cuyo ideal no cambia respecto de la base: se descartan.</summary>
    private async Task<(List<StockIdealCambioDto>? Cambios, List<string> Errores, int NoEncontrados, int TotalFilas)>
        LeerExcelAsync(IFormFile file)
    {
        var errores = new List<string>();
        if (file is null || file.Length == 0)
            return (null, new List<string> { "No llegó ningún archivo." }, 0, 0);

        XLWorkbook wb;
        try
        {
            using var input = new MemoryStream();
            await file.CopyToAsync(input);
            input.Position = 0;
            wb = new XLWorkbook(input);
        }
        catch (Exception ex)
        {
            return (null, new List<string> { $"No se pudo abrir el Excel: {ex.Message}" }, 0, 0);
        }

        using (wb)
        {
            var ws = wb.Worksheets.FirstOrDefault(w => w.Name.Equals("Stock ideal", StringComparison.OrdinalIgnoreCase))
                     ?? wb.Worksheets.FirstOrDefault();
            if (ws is null)
                return (null, new List<string> { "El Excel no tiene ninguna hoja." }, 0, 0);

            // Mapa de columnas por nombre de encabezado (fila 1), para aguantar que las muevan de lugar.
            var cols = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in ws.Row(1).CellsUsed())
            {
                var name = cell.GetString().Trim();
                if (!string.IsNullOrEmpty(name) && !cols.ContainsKey(name)) cols[name] = cell.Address.ColumnNumber;
            }
            if (!cols.ContainsKey("id") && !cols.ContainsKey("codigo"))
                return (null, new List<string> { "El Excel no tiene la columna 'id' ni 'codigo'. Bajá el Excel de nuevo y editá ese." }, 0, 0);
            if (!cols.ContainsKey("stock_ideal"))
                return (null, new List<string> { "El Excel no tiene la columna 'stock_ideal'. Bajá el Excel de nuevo y editá ese." }, 0, 0);

            int colId = cols.GetValueOrDefault("id");
            int colCodigo = cols.GetValueOrDefault("codigo");
            int colIdeal = cols["stock_ideal"];

            // Base actual, para comparar. Traigo todos los activos: son pocos miles.
            var todos = await _db.CafeProductos.AsNoTracking()
                .Where(p => p.IsActive)
                .Select(p => new { p.Id, p.Sku, p.Nombre, p.StockIdeal })
                .ToListAsync();
            var porId = todos.ToDictionary(p => p.Id);
            var porSku = todos.Where(p => !string.IsNullOrWhiteSpace(p.Sku))
                .GroupBy(p => p.Sku!.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            var cambios = new List<StockIdealCambioDto>();
            var vistos = new HashSet<int>();
            int noEncontrados = 0, totalFilas = 0;
            var ultimaFila = ws.LastRowUsed()?.RowNumber() ?? 1;

            for (int r = 2; r <= ultimaFila; r++)
            {
                var row = ws.Row(r);
                if (row.IsEmpty()) continue;

                // Emparejar: primero por id, si no hay, por codigo.
                dynamic? prod = null;
                if (colId > 0)
                {
                    var raw = row.Cell(colId).GetString().Trim();
                    if (int.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var pid)
                        && porId.TryGetValue(pid, out var byId)) prod = byId;
                }
                if (prod is null && colCodigo > 0)
                {
                    var sku = row.Cell(colCodigo).GetString().Trim();
                    if (!string.IsNullOrWhiteSpace(sku) && porSku.TryGetValue(sku, out var bySku)) prod = bySku;
                }

                totalFilas++;
                if (prod is null) { noEncontrados++; continue; }

                int prodId = (int)prod.Id;
                if (!vistos.Add(prodId))
                {
                    errores.Add($"Fila {r}: el producto aparece más de una vez, se toma la primera.");
                    continue;
                }

                // Celda vacia => sacar el ideal (null). Numero (incluye 0) => asignar.
                var celda = row.Cell(colIdeal).GetString().Trim();
                int? nuevo;
                if (string.IsNullOrWhiteSpace(celda)) nuevo = null;
                else if (decimal.TryParse(celda.Replace(".", "").Replace(",", "."),
                            NumberStyles.Any, CultureInfo.InvariantCulture, out var dec))
                    nuevo = Math.Max(0, (int)Math.Round(dec));
                else
                {
                    errores.Add($"Fila {r}: '{celda}' no es un número, se ignora esa fila.");
                    continue;
                }

                int? viejo = (int?)prod.StockIdeal;
                if (viejo == nuevo) continue;

                cambios.Add(new StockIdealCambioDto(
                    prodId, (string?)prod.Sku, (string)prod.Nombre,
                    viejo, nuevo, Asigna: nuevo.HasValue, Quita: !nuevo.HasValue));
            }

            return (cambios, errores, noEncontrados, totalFilas);
        }
    }
}
