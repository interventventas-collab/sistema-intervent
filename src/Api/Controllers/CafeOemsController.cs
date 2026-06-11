using Api.Data;
using Api.DTOs;
using Api.Models;
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
    public CafeOemsController(AppDbContext db) { _db = db; }

    private static CafeOemDto Map(CafeOem o, int variantesCount = 0) => new(
        o.Id, o.Codigo, o.Descripcion, o.Marca,
        o.MarcaId, o.MarcaNav?.Nombre,
        o.Costo, o.PvpConIva, o.IvaPct,
        o.Barcode, o.Proveedor, o.UxB,
        o.IsActive, o.CreatedAt, o.UpdatedAt, o.LastImportAt,
        variantesCount,
        UrlWeb: o.UrlWeb);

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
            using var stream = file.OpenReadStream();
            using var wb = new XLWorkbook(stream);
            var ws = wb.Worksheets.First();
            var range = ws.RangeUsed();
            if (range is null)
                return BadRequest(new { error = "Hoja vacia" });

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
            // 2026-06-10: detección de columna URL del producto en la web del proveedor
            var cUrlWeb = Find("web", "url", "url_web", "enlace", "link", "url_producto", "pagina_web");

            if (cCodigo is null) return BadRequest(new { error = "Falta la columna 'codigo_oem' (o 'codigo'/'oem')" });

            // Cache existentes por codigo
            var existentes = await _db.CafeOems.ToDictionaryAsync(x => x.Codigo, x => x);

            int firstDataRow = header.RowNumber() + 1;
            int lastRow = range.LastRow().RowNumber();

            for (int r = firstDataRow; r <= lastRow; r++)
            {
                var row = ws.Row(r);
                var codigo = (row.Cell(cCodigo.Value).GetString() ?? "").Trim();
                if (string.IsNullOrWhiteSpace(codigo)) { omitidos++; continue; }

                string? Get(int? col) => col is null ? null : (row.Cell(col.Value).IsEmpty() ? null : row.Cell(col.Value).GetString().Trim());
                decimal? GetNum(int? col)
                {
                    if (col is null) return null;
                    var cell = row.Cell(col.Value);
                    if (cell.IsEmpty()) return null;
                    if (cell.DataType == XLDataType.Number) return (decimal)cell.GetDouble();
                    var s = cell.GetString().Trim().Replace(".", "").Replace(",", ".");
                    return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : null;
                }

                var titulo = Get(cTitulo);
                var marca = Get(cMarca);
                var costo = GetNum(cCosto) ?? 0m;
                var pvp = GetNum(cPvp);
                var iva = GetNum(cIva);
                var barcode = Get(cBarcode);
                var uxbDec = GetNum(cUxB);
                int? uxb = uxbDec.HasValue ? (int)uxbDec.Value : null;
                var urlWeb = Get(cUrlWeb);  // 2026-06-10

                if (existentes.TryGetValue(codigo, out var existente))
                {
                    existente.Descripcion = titulo ?? existente.Descripcion;
                    existente.Marca = marca ?? existente.Marca;
                    existente.Costo = costo;
                    existente.PvpConIva = pvp;
                    existente.IvaPct = iva;
                    existente.Barcode = barcode ?? existente.Barcode;
                    existente.UxB = uxb ?? existente.UxB;
                    existente.UrlWeb = urlWeb ?? existente.UrlWeb; // 2026-06-10
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
                        Codigo = codigo,
                        Descripcion = titulo,
                        Marca = marca,
                        Costo = costo,
                        PvpConIva = pvp,
                        IvaPct = iva,
                        Barcode = barcode,
                        UxB = uxb,
                        UrlWeb = urlWeb,  // 2026-06-10
                        Proveedor = prov,
                        IsActive = true,
                        CreatedAt = ahora,
                        LastImportAt = ahora
                    };
                    _db.CafeOems.Add(nuevo);
                    existentes[codigo] = nuevo;  // por si aparece el mismo codigo dos veces en el excel
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
