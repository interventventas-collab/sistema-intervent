using System.Globalization;
using Api.Data;
using Api.Models;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

// Carga los excels de Contabilium a las tablas Contab_* para poder cotejar contra
// los SKU de MercadoLibre. NO toca Products ni Combos del ERP — eso pasa despues
// cuando el usuario decide vincular.
public class ContabiliumStagingService
{
    private readonly AppDbContext _db;

    public ContabiliumStagingService(AppDbContext db)
    {
        _db = db;
    }

    public class ImportSummary
    {
        public int Productos { get; set; }
        public int Combos { get; set; }
        public int ComboItems { get; set; }
        public string? ProductosFile { get; set; }
        public string? CombosFile { get; set; }
        public List<string> Warnings { get; set; } = new();
    }

    // El usuario sube los archivos a /data/files/base de datos contabilium/.
    // Buscamos por nombre que contenga "producto" y "combo".
    public async Task<ImportSummary> ImportFromDefaultFolderAsync()
    {
        var folder = "/data/files/base de datos contabilium";
        if (!Directory.Exists(folder))
            throw new InvalidOperationException($"No existe la carpeta {folder}. Subi los excels desde Archivos.");

        var files = Directory.GetFiles(folder, "*.xlsx", SearchOption.TopDirectoryOnly);
        var prodFile = files.FirstOrDefault(f =>
            Path.GetFileName(f).ToLowerInvariant().Contains("producto"));
        var combosFile = files.FirstOrDefault(f =>
            Path.GetFileName(f).ToLowerInvariant().Contains("combo"));

        var summary = new ImportSummary
        {
            ProductosFile = prodFile is not null ? Path.GetFileName(prodFile) : null,
            CombosFile = combosFile is not null ? Path.GetFileName(combosFile) : null
        };

        if (prodFile is null) summary.Warnings.Add("No se encontro un Excel de productos en la carpeta.");
        if (combosFile is null) summary.Warnings.Add("No se encontro un Excel de combos en la carpeta.");

        if (prodFile is not null)
            summary.Productos = await ImportProductosAsync(prodFile);
        if (combosFile is not null)
            (summary.Combos, summary.ComboItems) = await ImportCombosAsync(combosFile);

        return summary;
    }

    private async Task<int> ImportProductosAsync(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var range = ws.RangeUsed();
        if (range is null) return 0;

        var headerRow = range.FirstRow();
        var headers = headerRow.Cells().Select(c => (c.GetString() ?? "").Trim()).ToList();

        int idx(string name) => headers.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));

        int iSku = idx("SKU"), iSkuPadre = idx("SKU Padre"), iTipo = idx("Tipo"),
            iNombre = idx("Nombre"), iAtt1 = idx("Atributo 1"), iVarAtt1 = idx("Variante De Atributo 1"),
            iAtt2 = idx("Atributo 2"), iVarAtt2 = idx("Variante De Atributo 2"),
            iBarcode = idx("Codigo Barras"), iOem = idx("Codigo Oem"),
            iEstado = idx("Estado"), iCosto = idx("Costo Interno"), iPrecio = idx("Precio"),
            iIva = idx("Iva"), iPF = idx("Precio Final"), iStock = idx("Stock"),
            iRubro = idx("Rubro"), iSubRubro = idx("Sub Rubro"), iProv = idx("Proveedor"),
            iDesc = idx("Descripcion");

        // Borrar lo previo: este loader siempre re-ingesta entero, asi siempre es
        // un snapshot fresco del ultimo Excel exportado de Contabilium.
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM Contab_Productos");

        int count = 0;
        var batch = new List<ContabProducto>();
        for (int r = 2; r <= range.LastRow().RowNumber(); r++)
        {
            var row = ws.Row(r);
            string? S(int i) { if (i < 0) return null; var v = row.Cell(i + 1).GetString().Trim(); return string.IsNullOrEmpty(v) ? null : v; }

            var sku = S(iSku);
            if (string.IsNullOrEmpty(sku)) continue;

            batch.Add(new ContabProducto
            {
                Sku = sku,
                SkuPadre = S(iSkuPadre),
                Tipo = S(iTipo),
                Nombre = S(iNombre),
                Atributo1 = S(iAtt1),
                VarianteAtributo1 = S(iVarAtt1),
                Atributo2 = S(iAtt2),
                VarianteAtributo2 = S(iVarAtt2),
                CodigoBarras = S(iBarcode),
                CodigoOem = S(iOem),
                Estado = S(iEstado),
                CostoInterno = ParseDecimal(S(iCosto)),
                Precio = ParseDecimal(S(iPrecio)),
                Iva = ParseDecimal(S(iIva)),
                PrecioFinal = ParseDecimal(S(iPF)),
                Stock = ParseDecimal(S(iStock)),
                Rubro = S(iRubro),
                SubRubro = S(iSubRubro),
                Proveedor = S(iProv),
                Descripcion = S(iDesc),
                ImportedAt = DateTime.UtcNow
            });
            count++;
            if (batch.Count >= 500)
            {
                _db.ContabProductos.AddRange(batch);
                await _db.SaveChangesAsync();
                _db.ChangeTracker.Clear();
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            _db.ContabProductos.AddRange(batch);
            await _db.SaveChangesAsync();
            _db.ChangeTracker.Clear();
        }
        return count;
    }

    private async Task<(int combos, int items)> ImportCombosAsync(string path)
    {
        using var wb = new XLWorkbook(path);
        var ws = wb.Worksheets.First();
        var range = ws.RangeUsed();
        if (range is null) return (0, 0);

        var headers = range.FirstRow().Cells().Select(c => (c.GetString() ?? "").Trim()).ToList();
        int idx(string n) => headers.FindIndex(h => h.Equals(n, StringComparison.OrdinalIgnoreCase));

        int iSkuC = idx("SKU Combo"), iEstado = idx("Estado"), iNombre = idx("Nombre"),
            iDesc = idx("Descripcion"), iCosto = idx("Costo Interno Combo"),
            iRent = idx("Rentabilidad Combo"), iPU = idx("Precio Unitario Combo"),
            iIva = idx("Iva Combo"), iPF = idx("Precio Final Combo"),
            iAuto = idx("Precio Automatico"),
            iCantidad = idx("Cantidad"), iCodigo = idx("Codigo"), iItem = idx("Item"),
            iCostoComp = idx("Costo Interno"), iPrecioComp = idx("Precio Unitario");

        await _db.Database.ExecuteSqlRawAsync("DELETE FROM Contab_ComboItems");
        await _db.Database.ExecuteSqlRawAsync("DELETE FROM Contab_Combos");

        var combos = new Dictionary<string, ContabCombo>(StringComparer.OrdinalIgnoreCase);
        var items = new List<ContabComboItem>();

        for (int r = 2; r <= range.LastRow().RowNumber(); r++)
        {
            var row = ws.Row(r);
            string? S(int i) { if (i < 0) return null; var v = row.Cell(i + 1).GetString().Trim(); return string.IsNullOrEmpty(v) ? null : v; }

            var sku = S(iSkuC);
            if (string.IsNullOrEmpty(sku)) continue;

            // Cabecera: tomamos los datos del combo de la primera fila que veamos (todas las filas
            // del mismo combo repiten la cabecera, por como exporta Contabilium).
            if (!combos.ContainsKey(sku))
            {
                combos[sku] = new ContabCombo
                {
                    SkuCombo = sku,
                    Nombre = S(iNombre),
                    Descripcion = S(iDesc),
                    Estado = S(iEstado),
                    CostoInterno = ParseDecimal(S(iCosto)),
                    Rentabilidad = ParseDecimal(S(iRent)),
                    PrecioUnitario = ParseDecimal(S(iPU)),
                    Iva = ParseDecimal(S(iIva)),
                    PrecioFinal = ParseDecimal(S(iPF)),
                    PrecioAutomatico = ParseBool(S(iAuto)),
                    ImportedAt = DateTime.UtcNow
                };
            }

            var skuComp = S(iCodigo);
            if (!string.IsNullOrEmpty(skuComp))
            {
                items.Add(new ContabComboItem
                {
                    SkuCombo = sku,
                    SkuComponente = skuComp,
                    NombreComponente = S(iItem),
                    Cantidad = ParseDecimal(S(iCantidad)) ?? 1m,
                    CostoInternoComponente = ParseDecimal(S(iCostoComp)),
                    PrecioComponente = ParseDecimal(S(iPrecioComp)),
                    ImportedAt = DateTime.UtcNow
                });
            }
        }

        // Insertar combos por lotes.
        int comboCount = 0;
        var combosBatch = new List<ContabCombo>();
        foreach (var c in combos.Values)
        {
            combosBatch.Add(c);
            comboCount++;
            if (combosBatch.Count >= 500)
            {
                _db.ContabCombos.AddRange(combosBatch);
                await _db.SaveChangesAsync();
                _db.ChangeTracker.Clear();
                combosBatch.Clear();
            }
        }
        if (combosBatch.Count > 0)
        {
            _db.ContabCombos.AddRange(combosBatch);
            await _db.SaveChangesAsync();
            _db.ChangeTracker.Clear();
        }

        // Insertar componentes por lotes.
        int itemCount = 0;
        var itemsBatch = new List<ContabComboItem>();
        foreach (var it in items)
        {
            itemsBatch.Add(it);
            itemCount++;
            if (itemsBatch.Count >= 1000)
            {
                _db.ContabComboItems.AddRange(itemsBatch);
                await _db.SaveChangesAsync();
                _db.ChangeTracker.Clear();
                itemsBatch.Clear();
            }
        }
        if (itemsBatch.Count > 0)
        {
            _db.ContabComboItems.AddRange(itemsBatch);
            await _db.SaveChangesAsync();
            _db.ChangeTracker.Clear();
        }

        return (comboCount, itemCount);
    }

    private static decimal? ParseDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var clean = s.Trim().Replace(" ", "");
        if (clean.Contains(',') && !clean.Contains('.')) clean = clean.Replace(',', '.');
        else if (clean.Contains(',') && clean.Contains('.'))
            clean = clean.Replace(".", "").Replace(',', '.');
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool? ParseBool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return s.Trim().ToLowerInvariant() switch
        {
            "si" or "sí" or "yes" or "true" or "1" => true,
            "no" or "false" or "0" => false,
            _ => null
        };
    }
}
