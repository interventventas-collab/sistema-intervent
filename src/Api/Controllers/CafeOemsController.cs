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
        o.Costo, o.PvpConIva, o.IvaPct,
        o.Barcode, o.Proveedor,
        o.IsActive, o.CreatedAt, o.UpdatedAt, o.LastImportAt,
        variantesCount);

    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] string? proveedor = null, [FromQuery] string? marca = null, [FromQuery] string? q = null)
    {
        var query = _db.CafeOems.AsQueryable();
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
        var o = await _db.CafeOems.FindAsync(id);
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

        var o = new CafeOem
        {
            Codigo = codigo,
            Descripcion = string.IsNullOrWhiteSpace(req.Descripcion) ? null : req.Descripcion.Trim(),
            Marca = string.IsNullOrWhiteSpace(req.Marca) ? null : req.Marca.Trim(),
            Costo = req.Costo,
            PvpConIva = req.PvpConIva,
            IvaPct = req.IvaPct,
            Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim(),
            Proveedor = string.IsNullOrWhiteSpace(req.Proveedor) ? null : req.Proveedor.Trim().ToUpperInvariant(),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.CafeOems.Add(o);
        await _db.SaveChangesAsync();
        return Ok(Map(o));
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
        if (req.Marca is not null) o.Marca = string.IsNullOrWhiteSpace(req.Marca) ? null : req.Marca.Trim();
        if (req.Costo.HasValue) o.Costo = req.Costo.Value;
        if (req.PvpConIva.HasValue) o.PvpConIva = req.PvpConIva.Value;
        if (req.IvaPct.HasValue) o.IvaPct = req.IvaPct.Value;
        if (req.Barcode is not null) o.Barcode = string.IsNullOrWhiteSpace(req.Barcode) ? null : req.Barcode.Trim();
        if (req.Proveedor is not null) o.Proveedor = string.IsNullOrWhiteSpace(req.Proveedor) ? null : req.Proveedor.Trim().ToUpperInvariant();
        if (req.IsActive.HasValue) o.IsActive = req.IsActive.Value;
        o.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Ok(Map(o));
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

                if (existentes.TryGetValue(codigo, out var existente))
                {
                    existente.Descripcion = titulo ?? existente.Descripcion;
                    existente.Marca = marca ?? existente.Marca;
                    existente.Costo = costo;
                    existente.PvpConIva = pvp;
                    existente.IvaPct = iva;
                    existente.Barcode = barcode ?? existente.Barcode;
                    existente.Proveedor = prov;
                    existente.UpdatedAt = ahora;
                    existente.LastImportAt = ahora;
                    actualizados++;
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
            return StatusCode(500, new CafeOemImportResultDto(creados, actualizados, omitidos, prov, errores));
        }

        return Ok(new CafeOemImportResultDto(creados, actualizados, omitidos, prov, errores));
    }
}
