using Api.Data;
using Api.DTOs;
using Api.Models;
using Api.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Globalization;

namespace Api.Controllers;

[ApiController]
[Route("api/cafe/oems")]
[Authorize]
public class CafeOemsController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly OemWebScrapingService _scraper;
    private readonly OemMassiveScrapeState _massiveState;
    private readonly IServiceScopeFactory _scopeFactory;
    public CafeOemsController(AppDbContext db, OemWebScrapingService scraper, OemMassiveScrapeState massiveState, IServiceScopeFactory scopeFactory)
    {
        _db = db; _scraper = scraper; _massiveState = massiveState; _scopeFactory = scopeFactory;
    }

    private static CafeOemDto Map(CafeOem o, int variantesCount = 0) => new(
        o.Id, o.Codigo, o.Descripcion, o.Marca,
        o.MarcaId, o.MarcaNav?.Nombre,
        o.Costo, o.PvpConIva, o.IvaPct,
        o.Barcode, o.Proveedor, o.UxB,
        o.IsActive, o.CreatedAt, o.UpdatedAt, o.LastImportAt,
        variantesCount,
        UrlWeb: o.UrlWeb,
        ImagenUrl: o.ImagenUrl,
        DescripcionWeb: o.DescripcionWeb,
        EspecificacionesJson: o.EspecificacionesJson,
        ScrapedAt: o.ScrapedAt);

    /// <summary>Copia los campos heredables del OEM (costo, PVP, UxB, barcode) a todas las variantes vinculadas.
    /// El % BAR sobre costo es per-variante y NO se toca aca. Devuelve cuantas variantes se actualizaron.</summary>
    private async Task<int> PropagarAVariantesAsync(int oemId)
    {
        var oem = await _db.CafeOems.FindAsync(oemId);
        if (oem is null) return 0;
        var variantes = await _db.CafeProductos.Where(p => p.OemId == oemId).ToListAsync();
        if (variantes.Count == 0) return 0;
        var ahora = DateTime.UtcNow;
        foreach (var v in variantes)
        {
            v.Costo = oem.Costo;
            if (oem.PvpConIva.HasValue) v.Pvp2 = oem.PvpConIva.Value;
            if (oem.UxB.HasValue) v.UxB = oem.UxB.Value;
            if (!string.IsNullOrWhiteSpace(oem.Barcode)) v.Barcode = oem.Barcode;
            v.UpdatedAt = ahora;
        }
        return variantes.Count;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? proveedor = null, [FromQuery] string? marca = null, [FromQuery] string? q = null)
    {
        var query = _db.CafeOems.Include(o => o.MarcaNav).AsQueryable();
        if (!string.IsNullOrWhiteSpace(proveedor)) query = query.Where(o => o.Proveedor == proveedor);
        if (!string.IsNullOrWhiteSpace(marca)) query = query.Where(o => o.Marca == marca);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            query = query.Where(o => o.Codigo.Contains(t)
                || (o.Descripcion != null && o.Descripcion.Contains(t))
                || (o.Marca != null && o.Marca.Contains(t))
                || (o.Barcode != null && o.Barcode.Contains(t)));
        }
        var oems = await query.OrderBy(o => o.Codigo).Take(2000).ToListAsync();

        // Conteo de variantes vinculadas por OEM (en una sola consulta)
        var ids = oems.Select(x => x.Id).ToList();
        var counts = await _db.CafeProductos
            .Where(p => p.OemId != null && ids.Contains(p.OemId.Value))
            .GroupBy(p => p.OemId!.Value)
            .Select(g => new { OemId = g.Key, N = g.Count() })
            .ToDictionaryAsync(x => x.OemId, x => x.N);

        return Ok(oems.Select(o => Map(o, counts.GetValueOrDefault(o.Id, 0))).ToList());
    }

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var o = await _db.CafeOems.Include(x => x.MarcaNav).FirstOrDefaultAsync(x => x.Id == id);
        if (o is null) return NotFound(new { error = "OEM no encontrado" });
        var n = await _db.CafeProductos.CountAsync(p => p.OemId == id);
        return Ok(Map(o, n));
    }

    /// <summary>Lista las variantes vinculadas a un OEM (para verlas y propagar precios).</summary>
    [HttpGet("{id:int}/variantes")]
    public async Task<IActionResult> GetVariantes(int id)
    {
        var o = await _db.CafeOems.FindAsync(id);
        if (o is null) return NotFound(new { error = "OEM no encontrado" });
        var prods = await _db.CafeProductos
            .Where(p => p.OemId == id)
            .OrderBy(p => p.Sku).ThenBy(p => p.Nombre)
            .ToListAsync();
        return Ok(prods.Select(p => new
        {
            p.Id, p.Sku, p.Nombre, p.Categoria, p.Marca,
            p.Costo, p.Pvp1, p.Pvp2, p.BarPctSobreCosto, p.UxB,
            p.StockUnidades, p.IsActive
        }).ToList());
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateCafeOemRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Codigo)) return BadRequest(new { error = "El codigo es obligatorio" });
        var codigo = req.Codigo.Trim();
        if (await _db.CafeOems.AnyAsync(x => x.Codigo == codigo))
            return BadRequest(new { error = "Ya existe un OEM con ese codigo" });

        var (marcaId, marcaNombre) = await ResolveMarcaAsync(req.MarcaId, req.Marca);

        var o = new CafeOem
        {
            Codigo = codigo,
            Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim(),
            Marca = marcaNombre,
            MarcaId = marcaId,
            Costo = req.Costo,
            PvpConIva = req.PvpConIva,
            IvaPct = req.IvaPct,
            Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim(),
            Proveedor = string.IsNullOrWhiteSpace(req.Proveedor) ? null : req.Proveedor.Trim().ToUpperInvariant(),
            UxB = req.UxB,
            UrlWeb = string.IsNullOrWhiteSpace(req.UrlWeb) ? null : req.UrlWeb.Trim(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeOems.Add(o);
        await _db.SaveChangesAsync();
        var saved = await _db.CafeOems.Include(x => x.MarcaNav).FirstAsync(x => x.Id == o.Id);
        return Ok(Map(saved));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateCafeOemRequest req)
    {
        var o = await _db.CafeOems.FindAsync(id);
        if (o is null) return NotFound(new { error = "OEM no encontrado" });

        if (req.Codigo is not null)
        {
            var nuevo = req.Codigo.Trim();
            if (string.IsNullOrEmpty(nuevo)) return BadRequest(new { error = "El codigo no puede estar vacio" });
            if (nuevo != o.Codigo && await _db.CafeOems.AnyAsync(x => x.Codigo == nuevo && x.Id != id))
                return BadRequest(new { error = "Ya existe otro OEM con ese codigo" });
            o.Codigo = nuevo;
        }
        if (req.Descripcion is not null) o.Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim();
        if (req.MarcaId.HasValue && req.MarcaId.Value > 0)
        {
            var (mid, mnombre) = await ResolveMarcaAsync(req.MarcaId, null);
            o.MarcaId = mid; o.Marca = mnombre;
        }
        else if (req.ClearMarcaId)
        {
            o.MarcaId = null; o.Marca = null;
        }
        else if (req.Marca is not null)
        {
            var (mid, mnombre) = await ResolveMarcaAsync(null, req.Marca);
            o.MarcaId = mid; o.Marca = mnombre;
        }
        if (req.Costo.HasValue) o.Costo = req.Costo.Value;
        if (req.PvpConIva.HasValue) o.PvpConIva = req.PvpConIva.Value;
        if (req.IvaPct.HasValue) o.IvaPct = req.IvaPct.Value;
        if (req.Barcode is not null) o.Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim();
        if (req.Proveedor is not null) o.Proveedor = string.IsNullOrWhiteSpace(req.Proveedor) ? null : req.Proveedor.Trim().ToUpperInvariant();
        if (req.UxB.HasValue) o.UxB = req.UxB.Value;
        else if (req.ClearUxB) o.UxB = null;
        if (req.UrlWeb is not null) o.UrlWeb = string.IsNullOrWhiteSpace(req.UrlWeb) ? null : req.UrlWeb.Trim();
        else if (req.ClearUrlWeb) o.UrlWeb = null;
        if (req.IsActive.HasValue) o.IsActive = req.IsActive.Value;
        o.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Propagar costo + PVP + UxB + barcode a TODAS las variantes vinculadas.
        var n = await PropagarAVariantesAsync(o.Id);
        if (n > 0) await _db.SaveChangesAsync();

        var saved = await _db.CafeOems.Include(x => x.MarcaNav).FirstAsync(x => x.Id == o.Id);
        return Ok(Map(saved, n));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var o = await _db.CafeOems.FindAsync(id);
        if (o is null) return NotFound(new { error = "OEM no encontrado" });
        // Si hay variantes vinculadas, las desvinculo (OemId = null).
        var vinculados = await _db.CafeProductos.Where(p => p.OemId == id).ToListAsync();
        foreach (var p in vinculados) p.OemId = null;
        _db.CafeOems.Remove(o);
        await _db.SaveChangesAsync();
        return Ok(new { deleted = true, desvinculados = vinculados.Count });
    }

    // 2026-07-10: fila ya parseada de un Excel de OEMs (usada por Import y por Preview).
    private sealed class ParsedOemRow
    {
        public string Codigo = "";
        public string? Titulo;
        public string? Marca;
        public decimal? Costo;
        public decimal? Pvp;
        public decimal? Iva;
        public string? Barcode;
        public int? Uxb;
        public string? UrlWeb;
    }

    // 2026-07-10: lee el .xlsx y devuelve las filas parseadas. NO toca la base.
    // Compartido por Import (aplica) y Preview (dry-run) para que no se desincronicen.
    // 'error' != null => problema bloqueante (hoja vacia / falta columna codigo).
    private static List<ParsedOemRow> ParseOemExcel(
        Stream stream,
        out bool tieneColumnaCosto,
        out bool tieneColumnaPvp,
        out int omitidos,
        out string? error)
    {
        tieneColumnaCosto = false;
        tieneColumnaPvp = false;
        omitidos = 0;
        error = null;
        var filas = new List<ParsedOemRow>();

        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        var range = ws.RangeUsed();
        if (range is null) { error = "Hoja vacia"; return filas; }

        // Header -> column index (1-based en ClosedXML)
        var header = range.FirstRow();
        var colIx = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in header.Cells())
        {
            var name = (c.GetString() ?? "").Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(name) && !colIx.ContainsKey(name))
                colIx[name] = c.Address.ColumnNumber;
        }

        int? Find(params string[] names)
        {
            foreach (var n in names)
                if (colIx.TryGetValue(n, out var idx)) return idx;
            return null;
        }

        var cCodigo = Find("codigo_oem", "codigo", "oem");
        var cTitulo = Find("titulo", "descripcion", "nombre");
        var cMarca = Find("marca");
        var cCosto = Find("precio_costo", "costo");
        var cPvp = Find("precio_venta_con_iva", "pvp", "precio_venta");
        var cIva = Find("iva", "iva_pct");
        var cBarcode = Find("codigo_de_barras", "barcode", "codigo_barras", "ean");
        var cUxB = Find("uxb", "u_x_b", "unidades_por_bulto", "unidad_por_bulto", "ud_x_bulto", "u_bulto");
        var cUrlWeb = Find("web", "url", "url_web", "enlace", "link", "url_producto", "pagina_web");

        if (cCodigo is null) { error = "Falta la columna 'codigo_oem' (o 'codigo'/'oem')"; return filas; }
        tieneColumnaCosto = cCosto.HasValue;
        tieneColumnaPvp = cPvp.HasValue;

        int firstDataRow = header.RowNumber() + 1;
        int lastRow = range.LastRow().RowNumber();

        for (int r = firstDataRow; r <= lastRow; r++)
        {
            var row = ws.Row(r);
            var codigo = (row.Cell(cCodigo.Value).GetString() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(codigo)) { omitidos++; continue; }
            // 2026-07-10: la fila de ejemplo de la plantilla arranca con "EJEMPLO" -> no se importa.
            if (codigo.StartsWith("EJEMPLO", StringComparison.OrdinalIgnoreCase)) { omitidos++; continue; }

            string? Get(int? col) => col is null ? null : (row.Cell(col.Value).IsEmpty() ? null : row.Cell(col.Value).GetString().Trim());
            decimal? GetNum(int? col)
            {
                if (col is null) return null;
                var cell = row.Cell(col.Value);
                if (cell.IsEmpty()) return null;
                if (cell.DataType == XLDataType.Number) return (decimal)cell.GetDouble();
                var s = cell.GetString().Trim().Replace("$", "").Replace(" ", "").Replace(".", "").Replace(",", ".");
                return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
            }

            var uxbDec = GetNum(cUxB);
            filas.Add(new ParsedOemRow
            {
                Codigo = codigo,
                Titulo = Get(cTitulo),
                Marca = Get(cMarca),
                // Solo trae costo/pvp/iva si la COLUMNA existe (si no, queda null y no se pisa nada).
                Costo = cCosto.HasValue ? GetNum(cCosto) : null,
                Pvp = cPvp.HasValue ? GetNum(cPvp) : null,
                Iva = cIva.HasValue ? GetNum(cIva) : null,
                Barcode = Get(cBarcode),
                Uxb = uxbDec.HasValue ? (int)uxbDec.Value : null,
                UrlWeb = Get(cUrlWeb),
            });
        }

        return filas;
    }

    /// <summary>Importa una lista del proveedor desde un Excel (.xlsx).
    /// Columnas esperadas (case-insensitive, primera fila es el header):
    ///   codigo_oem, titulo, marca, precio_costo, precio_venta_con_iva, iva, codigo_de_barras
    /// Hace upsert por codigo_oem. Acepta un parametro 'proveedor' en el form (default 'COLOMBRARO').</summary>
    [HttpPost("import")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> Import([FromForm] IFormFile? file, [FromForm] string? proveedor = "COLOMBRARO")
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Subi un archivo .xlsx" });

        var prov = string.IsNullOrWhiteSpace(proveedor) ? "COLOMBRARO" : proveedor.Trim().ToUpperInvariant();
        var creados = 0;
        var actualizados = 0;
        var omitidos = 0;
        var errores = new List<string>();
        var ahora = DateTime.UtcNow;

        var oemsTocados = new HashSet<int>();
        try
        {
            List<ParsedOemRow> filas;
            string? error;
            using (var stream = file.OpenReadStream())
                filas = ParseOemExcel(stream, out _, out _, out omitidos, out error);
            if (error is not null) return BadRequest(new { error });

            // Cache existentes por codigo
            var existentes = await _db.CafeOems.ToDictionaryAsync(x => x.Codigo, x => x);

            foreach (var fila in filas)
            {
                if (existentes.TryGetValue(fila.Codigo, out var existente))
                {
                    existente.Descripcion = fila.Titulo ?? existente.Descripcion;
                    existente.Marca = fila.Marca ?? existente.Marca;
                    // Solo pisar costo/pvp/iva si vino valor (columna presente + celda con dato).
                    if (fila.Costo.HasValue) existente.Costo = fila.Costo.Value;
                    if (fila.Pvp.HasValue) existente.PvpConIva = fila.Pvp.Value;
                    if (fila.Iva.HasValue) existente.IvaPct = fila.Iva.Value;
                    existente.Barcode = fila.Barcode ?? existente.Barcode;
                    existente.UxB = fila.Uxb ?? existente.UxB;
                    existente.UrlWeb = fila.UrlWeb ?? existente.UrlWeb;
                    existente.Proveedor = prov;
                    existente.UpdatedAt = ahora;
                    existente.LastImportAt = ahora;
                    actualizados++;
                    if (existente.Id != 0) oemsTocados.Add(existente.Id);
                }
                else
                {
                    var nuevo = new CafeOem
                    {
                        Codigo = fila.Codigo,
                        Descripcion = fila.Titulo,
                        Marca = fila.Marca,
                        Costo = fila.Costo ?? 0m,
                        PvpConIva = fila.Pvp,
                        IvaPct = fila.Iva,
                        Barcode = fila.Barcode,
                        UxB = fila.Uxb,
                        UrlWeb = fila.UrlWeb,
                        Proveedor = prov,
                        IsActive = true,
                        CreatedAt = ahora,
                        LastImportAt = ahora
                    };
                    _db.CafeOems.Add(nuevo);
                    existentes[fila.Codigo] = nuevo;  // por si aparece el mismo codigo dos veces en el excel
                    creados++;
                }
            }

            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            errores.Add(ex.Message);
            return StatusCode(500, new CafeOemImportResultDto(creados, actualizados, omitidos, prov, 0, errores));
        }

        // Propagar a variantes vinculadas SOLO de los OEMs que se actualizaron
        // (los recien creados no tienen variantes vinculadas todavia).
        var totalVariantesPropagadas = 0;
        foreach (var oemId in oemsTocados)
            totalVariantesPropagadas += await PropagarAVariantesAsync(oemId);
        if (totalVariantesPropagadas > 0)
            await _db.SaveChangesAsync();

        return Ok(new CafeOemImportResultDto(creados, actualizados, omitidos, prov, totalVariantesPropagadas, errores));
    }

    /// <summary>2026-07-10: vista previa (dry-run) de la importacion. NO aplica nada.
    /// Muestra cuantos se crearian / actualizarian y los cambios de precio (viejo -> nuevo).</summary>
    [HttpPost("import/preview")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(50_000_000)]
    public async Task<IActionResult> ImportPreview([FromForm] IFormFile? file, [FromForm] string? proveedor = "COLOMBRARO")
    {
        if (file is null || file.Length == 0)
            return BadRequest(new { error = "Subi un archivo .xlsx" });

        var prov = string.IsNullOrWhiteSpace(proveedor) ? "COLOMBRARO" : proveedor.Trim().ToUpperInvariant();
        var errores = new List<string>();
        bool tieneCosto, tienePvp;
        int omitidos;
        List<ParsedOemRow> filas;
        try
        {
            using var stream = file.OpenReadStream();
            filas = ParseOemExcel(stream, out tieneCosto, out tienePvp, out omitidos, out var error);
            if (error is not null) return BadRequest(new { error });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }

        // Lectura de existentes sin trackear (no modificamos nada).
        var existentes = await _db.CafeOems.AsNoTracking().ToDictionaryAsync(x => x.Codigo, x => x);

        var creados = 0;
        var actualizados = 0;
        var cambios = new List<CafeOemImportCambioDto>();
        foreach (var fila in filas)
        {
            existentes.TryGetValue(fila.Codigo, out var ex);
            var esNuevo = ex is null;
            if (esNuevo) creados++; else actualizados++;

            decimal? costoViejo = ex?.Costo;
            decimal? pvpViejo = ex?.PvpConIva;
            decimal? costoNuevo = fila.Costo.HasValue ? fila.Costo : (esNuevo ? 0m : costoViejo);
            decimal? pvpNuevo = fila.Pvp.HasValue ? fila.Pvp : pvpViejo;
            var cambiaCosto = fila.Costo.HasValue && costoViejo != fila.Costo;
            var cambiaPvp = fila.Pvp.HasValue && pvpViejo != fila.Pvp;

            cambios.Add(new CafeOemImportCambioDto(
                fila.Codigo,
                fila.Titulo ?? ex?.Descripcion,
                esNuevo,
                costoViejo, costoNuevo,
                pvpViejo, pvpNuevo,
                cambiaCosto, cambiaPvp));
        }

        return Ok(new CafeOemImportPreviewDto(
            creados, actualizados, omitidos, prov,
            tieneCosto, tienePvp,
            cambios, errores));
    }

    /// <summary>2026-07-10: descarga un Excel plantilla con todas las columnas posibles,
    /// marcando cuales son obligatorias, para que el usuario lo complete y lo importe.</summary>
    [HttpGet("plantilla")]
    public IActionResult Plantilla()
    {
        // Columnas: nombre exacto que reconoce el importador + descripcion + obligatoriedad + ejemplo.
        // nivel: 0 = obligatorio, 1 = recomendado (precios), 2 = opcional
        var cols = new (string Header, int Nivel, string Ayuda, string Ejemplo)[]
        {
            ("codigo_oem",            0, "OBLIGATORIO. Codigo unico del proveedor. Por este campo se empareja/actualiza.", "9381"),
            ("titulo",                2, "Descripcion del producto.", "COL BOX CUADRADO X 15 LTS"),
            ("marca",                 2, "Marca del producto.", "COLOMBRARO"),
            ("precio_costo",          1, "Costo SIN IVA. Complétalo para actualizar el costo.", "8232.75"),
            ("precio_venta_con_iva",  1, "Precio publico CON IVA. Complétalo para actualizar el PVP.", "19500"),
            ("iva",                   2, "% de IVA (solo el numero). Opcional.", "21"),
            ("codigo_de_barras",      2, "Codigo de barras / EAN. Opcional.", "7790733093815"),
            ("uxb",                   2, "Unidades por bulto. Opcional.", "6"),
            ("web",                   2, "Link al producto en la web del proveedor. Opcional.", "https://colombraro.com.ar/..."),
        };

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("OEMs");

        // Colores por nivel de obligatoriedad
        var cRojo = XLColor.FromHtml("#fecaca");    // obligatorio
        var cAmar = XLColor.FromHtml("#fef08a");    // recomendado (precios)
        var cGris = XLColor.FromHtml("#e5e7eb");    // opcional

        for (int i = 0; i < cols.Length; i++)
        {
            var (headerName, nivel, ayuda, ejemplo) = cols[i];
            var cell = ws.Cell(1, i + 1);
            cell.Value = headerName;
            cell.Style.Font.Bold = true;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Fill.BackgroundColor = nivel == 0 ? cRojo : (nivel == 1 ? cAmar : cGris);
            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            var etiqueta = nivel == 0 ? "OBLIGATORIO" : (nivel == 1 ? "Recomendado (para actualizar precios)" : "Opcional");
            cell.GetComment().AddText($"{etiqueta}. {ayuda}");

            // Fila 2: ejemplo (arranca con EJEMPLO en el codigo -> el importador la ignora).
            var ej = ws.Cell(2, i + 1);
            ej.Value = i == 0 ? "EJEMPLO-9381" : ejemplo;
            ej.Style.Font.Italic = true;
            ej.Style.Font.FontColor = XLColor.FromHtml("#9ca3af");
        }

        ws.SheetView.FreezeRows(1);
        ws.Columns().AdjustToContents();
        foreach (var c in ws.Columns()) if (c.Width < 14) c.Width = 14;

        // Hoja de instrucciones (el importador solo lee la primera hoja, esta la ignora).
        var wsInfo = wb.Worksheets.Add("LEEME");
        wsInfo.Cell(1, 1).Value = "COMO USAR ESTA PLANTILLA";
        wsInfo.Cell(1, 1).Style.Font.Bold = true;
        wsInfo.Cell(1, 1).Style.Font.FontSize = 14;
        int rr = 3;
        void Linea(string t, bool bold = false)
        {
            var cc = wsInfo.Cell(rr, 1);
            cc.Value = t;
            cc.Style.Font.Bold = bold;
            rr++;
        }
        Linea("1) Completá los datos en la hoja 'OEMs', debajo de cada título. NO cambies los títulos.");
        Linea("2) Borrá la fila de EJEMPLO (la que arranca con 'EJEMPLO-9381') antes de importar.");
        Linea("   (Si te la olvidás, igual no se importa: el sistema ignora las filas que arrancan con EJEMPLO.)");
        Linea("3) Guardá el archivo y subilo con el botón 'Importar Excel'.");
        Linea("");
        Linea("COLUMNAS", true);
        foreach (var (headerName, nivel, ayuda, _) in cols)
        {
            var etiqueta = nivel == 0 ? "[OBLIGATORIO]" : (nivel == 1 ? "[Recomendado]" : "[Opcional]");
            Linea($"• {headerName}  {etiqueta}  →  {ayuda}");
        }
        Linea("");
        Linea("Los precios pueden ir con $ y puntos (ej: $ 19.500,00) o como número (19500). El sistema los entiende igual.");
        Linea("Al importar: si el código ya existe se ACTUALIZA; si no existe se CREA. Nada se elimina.");
        wsInfo.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return File(ms.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            "plantilla-oems.xlsx");
    }

    /// <summary>2026-06-11: scrapea la pagina del proveedor (URL del OEM) y guarda imagen, descripcion y ficha tecnica.</summary>
    [HttpPost("{id:int}/scrape-web")]
    public async Task<IActionResult> ScrapeWeb(int id)
    {
        var oem = await _db.CafeOems.FindAsync(id);
        if (oem is null) return NotFound(new { error = "OEM no encontrado" });
        if (string.IsNullOrWhiteSpace(oem.UrlWeb))
            return BadRequest(new { error = "Este OEM no tiene URL cargada. Carga la URL primero y reintenta." });

        var result = await _scraper.ScrapeAsync(oem.UrlWeb);
        if (!string.IsNullOrEmpty(result.Error))
            return StatusCode(502, new { error = result.Error });

        oem.ImagenUrl = result.ImagenUrl ?? oem.ImagenUrl;
        oem.DescripcionWeb = result.Descripcion ?? oem.DescripcionWeb;
        var espJson = _scraper.SerializeEspecificaciones(result.Especificaciones);
        if (!string.IsNullOrEmpty(espJson)) oem.EspecificacionesJson = espJson;
        oem.ScrapedAt = DateTime.UtcNow;
        oem.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        var n = await _db.CafeProductos.CountAsync(p => p.OemId == id);
        return Ok(new
        {
            success = true,
            oem = Map(oem, n),
            scraped = new
            {
                imagen = result.ImagenUrl,
                descripcion = result.Descripcion,
                especificaciones = result.Especificaciones
            }
        });
    }

    /// <summary>2026-06-11: estado del job masivo (poll desde la UI).</summary>
    [HttpGet("scrape-web/masivo/status")]
    public IActionResult ScrapeMasivoStatus() => Ok(_massiveState.Snapshot());

    /// <summary>2026-06-11: arranca el scraping masivo en background para todos los OEMs con URL.</summary>
    [HttpPost("scrape-web/masivo")]
    public async Task<IActionResult> ScrapeMasivoStart([FromQuery] string? proveedor = null, [FromQuery] bool soloFaltantes = true)
    {
        var ids = await _db.CafeOems
            .Where(o => o.IsActive && o.UrlWeb != null && o.UrlWeb != "")
            .Where(o => proveedor == null || o.Proveedor == proveedor)
            .Where(o => !soloFaltantes || o.ScrapedAt == null)
            .OrderBy(o => o.Id)
            .Select(o => o.Id)
            .ToListAsync();

        if (ids.Count == 0)
            return BadRequest(new { error = "No hay OEMs para procesar (filtros: tiene URL, " + (soloFaltantes ? "y aun no fueron scrapeados" : "todos") + ")" });

        if (!_massiveState.TryStart(ids.Count))
            return Conflict(new { error = "Ya hay un job de scraping masivo corriendo. Esperá que termine." });

        var scopeFactory = _scopeFactory;
        var state = _massiveState;
        _ = Task.Run(async () =>
        {
            try
            {
                foreach (var id in ids)
                {
                    try
                    {
                        using var scope = scopeFactory.CreateScope();
                        var db2 = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                        var scraper2 = scope.ServiceProvider.GetRequiredService<OemWebScrapingService>();
                        var oem = await db2.CafeOems.FindAsync(id);
                        if (oem is null || string.IsNullOrWhiteSpace(oem.UrlWeb))
                        {
                            state.Tick(oem?.Codigo ?? id.ToString(), false, "OEM no encontrado o sin URL");
                            continue;
                        }
                        var result = await scraper2.ScrapeAsync(oem.UrlWeb);
                        if (!string.IsNullOrEmpty(result.Error))
                        {
                            state.Tick(oem.Codigo, false, result.Error);
                        }
                        else
                        {
                            oem.ImagenUrl = result.ImagenUrl ?? oem.ImagenUrl;
                            oem.DescripcionWeb = result.Descripcion ?? oem.DescripcionWeb;
                            var espJson = scraper2.SerializeEspecificaciones(result.Especificaciones);
                            if (!string.IsNullOrEmpty(espJson)) oem.EspecificacionesJson = espJson;
                            oem.ScrapedAt = DateTime.UtcNow;
                            oem.UpdatedAt = DateTime.UtcNow;
                            await db2.SaveChangesAsync();
                            state.Tick(oem.Codigo, true);
                        }
                        // pausa para no sobrecargar al proveedor
                        await Task.Delay(800);
                    }
                    catch (Exception ex)
                    {
                        state.Tick(id.ToString(), false, ex.Message);
                    }
                }
            }
            finally
            {
                state.Finish();
            }
        });

        return Accepted(new { started = true, total = ids.Count });
    }

    /// <summary>Resuelve marca: si viene MarcaId valido la busca; si solo viene texto, busca o crea.</summary>
    private async Task<(int?, string?)> ResolveMarcaAsync(int? marcaId, string? marcaTexto)
    {
        if (marcaId.HasValue && marcaId.Value > 0)
        {
            var existing = await _db.CafeMarcas.FindAsync(marcaId.Value);
            if (existing is null) return (null, null);
            return (existing.Id, existing.Nombre);
        }
        if (string.IsNullOrWhiteSpace(marcaTexto)) return (null, null);
        var nombre = marcaTexto.Trim();
        var match = await _db.CafeMarcas.FirstOrDefaultAsync(m => m.Nombre == nombre);
        if (match is not null) return (match.Id, match.Nombre);
        var nuevo = new CafeMarca { Nombre = nombre, IsActive = true, CreatedAt = DateTime.UtcNow };
        _db.CafeMarcas.Add(nuevo);
        await _db.SaveChangesAsync();
        return (nuevo.Id, nuevo.Nombre);
    }
}
