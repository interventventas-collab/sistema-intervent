using Api.Data;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace Api.Controllers;

/// <summary>2026-07-15: gestión masiva del "Stock mínimo para MeLi" (campo CafeProducto.StockMinimoMeLi)
/// vía Excel, desde la pantalla de Stock masivo. Tres endpoints:
///   GET  /export   -> baja un .xlsx con todos los productos activos y su mínimo actual.
///   POST /preview  -> lee el .xlsx editado y muestra qué cambiaría (dry-run, NO toca la base).
///   POST /apply    -> aplica los cambios.
/// El emparejamiento es por la columna 'id' (interna, no se toca); si falta, cae al 'codigo' (Sku).
/// Regla: celda vacía en 'stock_minimo' => se saca el mínimo (queda NULL = usa la reserva global).
/// Un número (incluye 0) => se asigna ese mínimo puntual. Filas borradas del Excel: no se tocan.</summary>
[ApiController]
[Route("api/stock/minimo")]
[Authorize]
public class StockMinimoController : ControllerBase
{
    private readonly AppDbContext _db;
    public StockMinimoController(AppDbContext db) { _db = db; }

    public record StockMinimoCambioDto(
        int ProductoId, string? Codigo, string Descripcion,
        int? MinimoViejo, int? MinimoNuevo, bool Asigna, bool Quita);

    public record StockMinimoPreviewDto(
        int TotalFilas, int SinCambios, int Asignan, int Quitan, int NoEncontrados,
        List<StockMinimoCambioDto> Cambios, List<string> Errores);

    public record StockMinimoApplyResultDto(
        int Actualizados, int Quitados, int NoEncontrados, List<string> Errores);

    // Proyección liviana para el dry-run (preview), sin traer la entidad entera.
    private record ProdInfo(int Id, string? Sku, string Nombre, int? StockMinimoMeLi);

    // ───────────────────────── EXPORT ─────────────────────────
    /// <summary>Baja un Excel con todos los productos activos: id · codigo · descripcion · marca · stock_actual · stock_minimo.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export()
    {
        var prods = await _db.CafeProductos
            .AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => new
            {
                p.Id,
                p.Sku,
                p.Nombre,
                Marca = p.Marca ?? (p.MarcaNav != null ? p.MarcaNav.Nombre : null),
                p.StockUnidades,
                p.StockMinimoMeLi
            })
            .OrderBy(p => p.Marca)
            .ThenBy(p => p.Nombre)
            .ToListAsync();

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Stock minimo");

        var headers = new[] { "id", "codigo", "descripcion", "marca", "stock_actual", "stock_minimo" };
        for (int i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        }
        // La columna editable (stock_minimo) en amarillo; las de solo-lectura en gris.
        var cEditable = XLColor.FromHtml("#fef08a");
        var cLectura = XLColor.FromHtml("#e5e7eb");
        for (int i = 0; i < headers.Length; i++)
            ws.Cell(1, i + 1).Style.Fill.BackgroundColor = (i == 5) ? cEditable : cLectura;
        ws.Cell(1, 1).GetComment().AddText("NO TOCAR. Se usa para emparejar el producto al subir el Excel.");
        ws.Cell(1, 2).GetComment().AddText("Código del producto (referencia, no se modifica).");
        ws.Cell(1, 5).GetComment().AddText("Stock actual (referencia, no se modifica).");
        ws.Cell(1, 6).GetComment().AddText("EDITÁ ACÁ. Poné un número para reservar esa cantidad para vos (MeLi ve stock − ese número). Dejá vacío para sacarle el mínimo.");

        int r = 2;
        foreach (var p in prods)
        {
            ws.Cell(r, 1).Value = p.Id;
            ws.Cell(r, 2).Value = p.Sku ?? "";
            ws.Cell(r, 3).Value = p.Nombre;
            ws.Cell(r, 4).Value = p.Marca ?? "";
            ws.Cell(r, 5).Value = p.StockUnidades;
            if (p.StockMinimoMeLi.HasValue) ws.Cell(r, 6).Value = p.StockMinimoMeLi.Value;
            // columnas de referencia en gris clarito para que se note que no se editan
            for (int c = 1; c <= 5; c++) ws.Cell(r, c).Style.Fill.BackgroundColor = XLColor.FromHtml("#f9fafb");
            r++;
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
        foreach (var c in ws.Columns()) if (c.Width < 12) c.Width = 12;
        ws.Column(3).Width = 45; // descripcion

        // Hoja de instrucciones
        var info = wb.Worksheets.Add("LEEME");
        info.Cell(1, 1).Value = "CÓMO EDITAR EL STOCK MÍNIMO";
        info.Cell(1, 1).Style.Font.Bold = true;
        info.Cell(1, 1).Style.Font.FontSize = 14;
        int rr = 3;
        void L(string t, bool bold = false) { var cc = info.Cell(rr, 1); cc.Value = t; cc.Style.Font.Bold = bold; rr++; }
        L("1) Editá SOLO la columna amarilla 'stock_minimo' en la hoja 'Stock minimo'.");
        L("2) Poné un número para reservar esa cantidad para vos: MeLi va a ver (stock − ese número).");
        L("   Ejemplo: si ponés 2, te guardás 2 unidades y MeLi ve el resto.");
        L("3) Dejá la celda VACÍA para sacarle el mínimo a ese producto.");
        L("4) NO cambies la columna 'id' (se usa para reconocer cada producto).");
        L("5) Podés borrar las filas de los productos que no querés tocar: solo se aplican los que dejes.");
        L("6) Guardá el archivo y subilo con el botón 'Subir Excel'. Antes de aplicar vas a ver una vista previa.");
        info.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "stock-minimo.xlsx");
    }

    // ───────────────────────── PARSE (compartido) ─────────────────────────
    private sealed class ParsedRow
    {
        public int? Id;
        public string? Codigo;
        public bool MinimoPresente;   // true si la celda tenía algún valor
        public int? MinimoValor;      // valor numérico si MinimoPresente (celda vacía => null)
        public int FilaExcel;
    }

    private static List<ParsedRow> ParseExcel(Stream stream, out string? error)
    {
        error = null;
        var filas = new List<ParsedRow>();
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        var range = ws.RangeUsed();
        if (range is null) { error = "El Excel está vacío."; return filas; }

        var header = range.FirstRow();
        var colIx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in header.Cells())
        {
            var name = (c.GetString() ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(name) && !colIx.ContainsKey(name)) colIx[name] = c.Address.ColumnNumber;
        }
        int? Find(params string[] names)
        {
            foreach (var n in names) if (colIx.TryGetValue(n, out var idx)) return idx;
            return null;
        }
        var cId = Find("id");
        var cCodigo = Find("codigo", "sku", "código");
        var cMin = Find("stock_minimo", "stock minimo", "minimo", "mínimo");
        if (cMin is null) { error = "No encontré la columna 'stock_minimo'. ¿Bajaste el Excel desde acá?"; return filas; }
        if (cId is null && cCodigo is null) { error = "El Excel no tiene la columna 'id' ni 'codigo' para reconocer los productos."; return filas; }

        int firstData = header.RowNumber() + 1;
        int lastRow = range.LastRow().RowNumber();
        for (int rr = firstData; rr <= lastRow; rr++)
        {
            var row = ws.Row(rr);
            int? id = null;
            if (cId is not null)
            {
                var idCell = row.Cell(cId.Value);
                if (!idCell.IsEmpty())
                {
                    if (idCell.DataType == XLDataType.Number) id = (int)idCell.GetDouble();
                    else if (int.TryParse(idCell.GetString().Trim(), out var pid)) id = pid;
                }
            }
            string? codigo = cCodigo is null ? null : (row.Cell(cCodigo.Value).IsEmpty() ? null : row.Cell(cCodigo.Value).GetString().Trim());

            // fila totalmente vacía => saltar
            if (id is null && string.IsNullOrWhiteSpace(codigo)) continue;

            var minCell = row.Cell(cMin.Value);
            bool presente = !minCell.IsEmpty();
            int? valor = null;
            if (presente)
            {
                if (minCell.DataType == XLDataType.Number) valor = (int)Math.Round(minCell.GetDouble());
                else
                {
                    var s = minCell.GetString().Trim().Replace(" ", "");
                    if (string.IsNullOrEmpty(s)) { presente = false; }
                    else if (int.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)) valor = v;
                    else { presente = true; valor = null; } // texto no numérico -> se marca error abajo
                }
                if (valor.HasValue && valor.Value < 0) valor = 0;
            }

            filas.Add(new ParsedRow { Id = id, Codigo = codigo, MinimoPresente = presente, MinimoValor = valor, FilaExcel = rr });
        }
        return filas;
    }

    // ───────────────────────── PREVIEW ─────────────────────────
    [HttpPost("preview")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> Preview([FromForm] IFormFile? file)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Subí un archivo .xlsx" });
        List<ParsedRow> filas;
        try { using var s = file.OpenReadStream(); filas = ParseExcel(s, out var err); if (err is not null) return BadRequest(new { error = err }); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }

        var prods = await _db.CafeProductos.AsNoTracking()
            .Where(p => p.IsActive)
            .Select(p => new ProdInfo(p.Id, p.Sku, p.Nombre, p.StockMinimoMeLi))
            .ToListAsync();
        var porId = prods.ToDictionary(p => p.Id);
        var porSku = prods.Where(p => !string.IsNullOrWhiteSpace(p.Sku))
            .GroupBy(p => p.Sku!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var errores = new List<string>();
        var cambios = new List<StockMinimoCambioDto>();
        int sinCambios = 0, asignan = 0, quitan = 0, noEnc = 0;

        foreach (var f in filas)
        {
            ProdInfo? prod = null;
            if (f.Id is not null && porId.TryGetValue(f.Id.Value, out var byId)) prod = byId;
            else if (f.Codigo is not null && porSku.TryGetValue(f.Codigo, out var bySku)) prod = bySku;
            if (prod is null) { noEnc++; if (errores.Count < 20) errores.Add($"Fila {f.FilaExcel}: no encontré el producto (id={f.Id}, codigo={f.Codigo})."); continue; }

            if (f.MinimoPresente && f.MinimoValor is null)
            { if (errores.Count < 20) errores.Add($"Fila {f.FilaExcel}: el stock mínimo no es un número válido."); continue; }

            int? viejo = prod.StockMinimoMeLi;
            int? nuevo = f.MinimoPresente ? f.MinimoValor : null;
            if (viejo == nuevo) { sinCambios++; continue; }

            bool asigna = nuevo.HasValue;
            bool quita = !nuevo.HasValue;
            if (asigna) asignan++; else quitan++;
            cambios.Add(new StockMinimoCambioDto(prod.Id, prod.Sku, prod.Nombre, viejo, nuevo, asigna, quita));
        }

        return Ok(new StockMinimoPreviewDto(filas.Count, sinCambios, asignan, quitan, noEnc, cambios, errores));
    }

    // ───────────────────────── APPLY ─────────────────────────
    [HttpPost("apply")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> Apply([FromForm] IFormFile? file)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Subí un archivo .xlsx" });
        List<ParsedRow> filas;
        try { using var s = file.OpenReadStream(); filas = ParseExcel(s, out var err); if (err is not null) return BadRequest(new { error = err }); }
        catch (Exception ex) { return StatusCode(500, new { error = ex.Message }); }

        var prods = await _db.CafeProductos.Where(p => p.IsActive).ToListAsync();
        var porId = prods.ToDictionary(p => p.Id);
        var porSku = prods.Where(p => !string.IsNullOrWhiteSpace(p.Sku))
            .GroupBy(p => p.Sku!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var errores = new List<string>();
        int actualizados = 0, quitados = 0, noEnc = 0;

        foreach (var f in filas)
        {
            Models.CafeProducto? prod = null;
            if (f.Id is not null && porId.TryGetValue(f.Id.Value, out var byId)) prod = byId;
            else if (f.Codigo is not null && porSku.TryGetValue(f.Codigo, out var bySku)) prod = bySku;
            if (prod is null) { noEnc++; continue; }

            if (f.MinimoPresente && f.MinimoValor is null)
            { if (errores.Count < 20) errores.Add($"Fila {f.FilaExcel}: el stock mínimo no es un número válido, se salteó."); continue; }

            int? nuevo = f.MinimoPresente ? f.MinimoValor : null;
            if (prod.StockMinimoMeLi == nuevo) continue;
            prod.StockMinimoMeLi = nuevo;
            if (nuevo.HasValue) actualizados++; else quitados++;
        }

        await _db.SaveChangesAsync();
        return Ok(new StockMinimoApplyResultDto(actualizados, quitados, noEnc, errores));
    }
}
