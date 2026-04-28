using System.Globalization;
using Api.Data;
using Api.DTOs;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class BulkImportService
{
    private readonly AppDbContext _db;
    private readonly SupplierService _suppliers;
    private readonly BrandService _brands;
    private readonly ClientService _clients;
    private readonly ProductService _products;
    private readonly ComboService _combos;

    public BulkImportService(
        AppDbContext db,
        SupplierService suppliers,
        BrandService brands,
        ClientService clients,
        ProductService products,
        ComboService combos)
    {
        _db = db;
        _suppliers = suppliers;
        _brands = brands;
        _clients = clients;
        _products = products;
        _combos = combos;
    }

    // ============================================================
    // SUPPLIERS
    // ============================================================

    public byte[] BuildSupplierTemplate() => BuildTemplate("Proveedores", new[]
    {
        ("nombre", "Distribuidora Ejemplo SA"),
        ("codigo", "PROV-009 (opcional, se autogenera)"),
        ("cuit", "30-12345678-9"),
        ("telefono", "+54 11 1234-5678"),
        ("email", "ventas@ejemplo.com"),
        ("direccion", "Av. Siempre Viva 123"),
        ("contacto", "Juan Perez"),
        ("notas", "Atiende de 9 a 18hs")
    });

    public async Task<BulkImportResult> ImportSuppliersAsync(Stream excelStream)
    {
        var (headers, rows) = ReadSheet(excelStream);
        var result = new BulkImportResult(rows.Count, 0, 0, new List<BulkImportError>());
        int created = 0, skipped = 0;
        var errors = new List<BulkImportError>();

        for (int i = 0; i < rows.Count; i++)
        {
            var rowNum = i + 2;
            var row = rows[i];
            try
            {
                var name = Cell(row, headers, "nombre");
                if (string.IsNullOrWhiteSpace(name)) { skipped++; continue; }

                await _suppliers.CreateAsync(new CreateSupplierRequest(
                    Code: Cell(row, headers, "codigo"),
                    Name: name!,
                    Cuit: Cell(row, headers, "cuit"),
                    Phone: Cell(row, headers, "telefono"),
                    Email: Cell(row, headers, "email"),
                    Address: Cell(row, headers, "direccion"),
                    ContactName: Cell(row, headers, "contacto"),
                    Notes: Cell(row, headers, "notas")
                ));
                created++;
            }
            catch (Exception ex) { errors.Add(new BulkImportError(rowNum, ex.Message)); }
        }
        return result with { Created = created, Skipped = skipped, Errors = errors };
    }

    // ============================================================
    // CLIENTS (mismo formato que suppliers)
    // ============================================================

    public byte[] BuildClientTemplate() => BuildTemplate("Clientes", new[]
    {
        ("nombre", "Juan Perez / Empresa SA"),
        ("codigo", "CLI-009 (opcional, se autogenera)"),
        ("cuit", "20-12345678-9 o DNI"),
        ("telefono", "+54 11 1234-5678"),
        ("direccion", "Av. Siempre Viva 123"),
        ("email", "cliente@ejemplo.com"),
        ("contacto", "Persona de contacto"),
        ("notas", "Cualquier dato adicional")
    });

    public async Task<BulkImportResult> ImportClientsAsync(Stream excelStream)
    {
        var (headers, rows) = ReadSheet(excelStream);
        int created = 0, skipped = 0;
        var errors = new List<BulkImportError>();

        for (int i = 0; i < rows.Count; i++)
        {
            var rowNum = i + 2;
            var row = rows[i];
            try
            {
                var name = Cell(row, headers, "nombre");
                if (string.IsNullOrWhiteSpace(name)) { skipped++; continue; }

                await _clients.CreateAsync(new CreateClientRequest(
                    Code: Cell(row, headers, "codigo"),
                    Name: name!,
                    Cuit: Cell(row, headers, "cuit"),
                    Phone: Cell(row, headers, "telefono"),
                    Email: Cell(row, headers, "email"),
                    Address: Cell(row, headers, "direccion"),
                    ContactName: Cell(row, headers, "contacto"),
                    Notes: Cell(row, headers, "notas")
                ));
                created++;
            }
            catch (Exception ex) { errors.Add(new BulkImportError(rowNum, ex.Message)); }
        }
        return new BulkImportResult(rows.Count, created, skipped, errors);
    }

    // ============================================================
    // BRANDS
    // ============================================================

    public byte[] BuildBrandTemplate() => BuildTemplate("Marcas", new[]
    {
        ("nombre", "Samsung"),
        ("codigo", "MAR-009 (opcional, se autogenera)"),
        ("descripcion", "Notas de la marca"),
        ("vencimiento", "N (S si los productos manejan vencimiento, N si no)")
    });

    public async Task<BulkImportResult> ImportBrandsAsync(Stream excelStream)
    {
        var (headers, rows) = ReadSheet(excelStream);
        int created = 0, skipped = 0;
        var errors = new List<BulkImportError>();

        for (int i = 0; i < rows.Count; i++)
        {
            var rowNum = i + 2;
            var row = rows[i];
            try
            {
                var name = Cell(row, headers, "nombre");
                if (string.IsNullOrWhiteSpace(name)) { skipped++; continue; }

                await _brands.CreateAsync(new CreateBrandRequest(
                    Code: Cell(row, headers, "codigo"),
                    Name: name!,
                    Description: Cell(row, headers, "descripcion"),
                    HasExpiry: ParseBool(Cell(row, headers, "vencimiento"))
                ));
                created++;
            }
            catch (Exception ex) { errors.Add(new BulkImportError(rowNum, ex.Message)); }
        }
        return new BulkImportResult(rows.Count, created, skipped, errors);
    }

    // ============================================================
    // PRODUCTS
    // ============================================================

    public byte[] BuildProductTemplate() => BuildTemplate("Productos", new[]
    {
        ("titulo", "Bandeja Blanca 10L"),
        ("nombre_para_mostrar", "Bandeja Plastica 10 Litros Blanca"),
        ("descripcion", "Bandeja plastica resistente"),
        ("marca", "Colombraro (nombre exacto de la marca cargada)"),
        ("modelo", "Modelo X"),
        ("producto_base_sku", "9311 (SKU del producto base, dejar vacio si es producto suelto)"),
        ("sku", "C9311BL"),
        ("codigo_de_barras", "7790140123456"),
        ("codigo_oem", "OEM-X"),
        ("url_imagen", "https://ejemplo.com/img.jpg"),
        ("precio_costo", "1500.50 (se ignora si tiene producto_base_sku, hereda del padre)"),
        ("precio_venta", "2999.99 (se ignora si tiene producto_base_sku, hereda del padre)"),
        ("iva", "21"),
        ("cuenta_compra", "511001"),
        ("cuenta_venta", "411001"),
        ("cuenta_mercaderia", "113001"),
        ("stock", "10"),
        ("stock_critico", "2")
    });

    // Plantilla SIN columna producto_base_sku: para cargar productos que SERAN base de otros.
    public byte[] BuildBaseProductTemplate() => BuildTemplate("Productos base", new[]
    {
        ("titulo", "Caja Plastica 10L"),
        ("nombre_para_mostrar", "Caja Plastica Apilable 10 Litros"),
        ("descripcion", "Caja plastica resistente"),
        ("marca", "Colombraro (nombre exacto de la marca cargada)"),
        ("modelo", "Modelo X"),
        ("sku", "9311"),
        ("codigo_de_barras", "7790140123456"),
        ("codigo_oem", "OEM-X"),
        ("url_imagen", "https://ejemplo.com/img.jpg"),
        ("precio_costo", "1500.50"),
        ("precio_venta", "2999.99"),
        ("iva", "21"),
        ("cuenta_compra", "511001"),
        ("cuenta_venta", "411001"),
        ("cuenta_mercaderia", "113001"),
        ("stock", "10"),
        ("stock_critico", "2")
    });

    public async Task<BulkImportResult> ImportProductsAsync(Stream excelStream, bool markAsBase = false)
    {
        var (headers, rows) = ReadSheet(excelStream);
        int created = 0, skipped = 0;
        var errors = new List<BulkImportError>();

        // Cache de marcas por nombre (lower) y productos existentes por SKU (lower)
        var brandsByName = await _db.Brands
            .ToDictionaryAsync(b => b.Name.ToLowerInvariant(), b => b.Id);

        var productIdBySku = (await _db.Products
            .Where(p => p.Sku != null)
            .Select(p => new { p.Id, p.Sku })
            .ToListAsync())
            .GroupBy(p => p.Sku!.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().Id);

        // Doble pasada: primero las filas SIN producto_base_sku (padres y sueltos),
        // despues las que SI tienen, asi cuando llega el derivado el padre ya existe.
        var withoutBase = new List<(int rowNum, List<string?> row)>();
        var withBase = new List<(int rowNum, List<string?> row)>();
        for (int i = 0; i < rows.Count; i++)
        {
            var rowNum = i + 2;
            var row = rows[i];
            var title = Cell(row, headers, "titulo");
            if (string.IsNullOrWhiteSpace(title)) { skipped++; continue; }

            var baseSku = Cell(row, headers, "producto_base_sku");
            if (string.IsNullOrWhiteSpace(baseSku)) withoutBase.Add((rowNum, row));
            else withBase.Add((rowNum, row));
        }

        async Task ProcessAsync(List<(int rowNum, List<string?> row)> batch)
        {
            foreach (var (rowNum, row) in batch)
            {
                try
                {
                    var dto = await CreateProductFromRow(row, headers, brandsByName, productIdBySku, markAsBase);
                    if (dto is not null && !string.IsNullOrEmpty(dto.Sku))
                        productIdBySku[dto.Sku.ToLowerInvariant()] = dto.Id;
                    created++;
                }
                catch (Exception ex) { errors.Add(new BulkImportError(rowNum, ex.Message)); }
            }
        }

        await ProcessAsync(withoutBase);
        await ProcessAsync(withBase);

        return new BulkImportResult(rows.Count, created, skipped, errors);
    }

    private async Task<ProductListDto?> CreateProductFromRow(
        List<string?> row, List<string> headers,
        Dictionary<string, int> brandsByName,
        Dictionary<string, int> productIdBySku,
        bool markAsBase = false)
    {
        var title = Cell(row, headers, "titulo");

        int? brandId = null;
        var brandName = Cell(row, headers, "marca");
        if (!string.IsNullOrWhiteSpace(brandName))
        {
            if (!brandsByName.TryGetValue(brandName.Trim().ToLowerInvariant(), out var bid))
                throw new InvalidOperationException($"La marca '{brandName}' no existe. Cargala primero.");
            brandId = bid;
        }

        int? baseId = null;
        var baseSku = Cell(row, headers, "producto_base_sku");
        if (!string.IsNullOrWhiteSpace(baseSku))
        {
            if (!productIdBySku.TryGetValue(baseSku.Trim().ToLowerInvariant(), out var bid))
                throw new InvalidOperationException($"El producto base con SKU '{baseSku}' no existe. Cargalo primero (en la solapa Productos base).");
            baseId = bid;
        }

        return await _products.CreateAsync(new CreateProductRequest(
            Title: title!,
            DisplayName: Cell(row, headers, "nombre_para_mostrar"),
            Description: Cell(row, headers, "descripcion"),
            Brand: brandName,
            Model: Cell(row, headers, "modelo"),
            Sku: Cell(row, headers, "sku"),
            Barcode: Cell(row, headers, "codigo_de_barras"),
            OemCode: Cell(row, headers, "codigo_oem"),
            ImageUrl: Cell(row, headers, "url_imagen"),
            Photo1: null, Photo2: null, Photo3: null,
            CostPrice: ParseDecimal(Cell(row, headers, "precio_costo")) ?? 0m,
            RetailPrice: ParseDecimal(Cell(row, headers, "precio_venta")) ?? 0m,
            VatRate: ParseDecimal(Cell(row, headers, "iva")),
            PurchaseAccount: Cell(row, headers, "cuenta_compra"),
            SaleAccount: Cell(row, headers, "cuenta_venta"),
            InventoryAccount: Cell(row, headers, "cuenta_mercaderia"),
            Stock: ParseInt(Cell(row, headers, "stock")) ?? 0,
            CriticalStock: ParseInt(Cell(row, headers, "stock_critico")) ?? 0,
            BaseProductId: baseId,
            BrandId: brandId,
            IsBase: markAsBase
        ));
    }

    // ============================================================
    // COMBOS
    // ============================================================

    public byte[] BuildComboTemplate() => BuildTemplate("Combos", new[]
    {
        ("nombre", "Pack escolar 3 unidades"),
        ("sku", "COMBO-009 (opcional, se autogenera)"),
        ("descripcion", "Lleva 3 productos a un precio especial"),
        ("modo_precio", "auto / manual / percent"),
        ("precio_o_porcentaje", "Si modo=manual: precio fijo. Si modo=percent: % (ej: -15 para descuento)"),
        ("items", "Formato: SKU:cantidad,SKU:cantidad. Ej: C8718BL:2,C1495NEG:1")
    });

    public async Task<BulkImportResult> ImportCombosAsync(Stream excelStream)
    {
        var (headers, rows) = ReadSheet(excelStream);
        int created = 0, skipped = 0;
        var errors = new List<BulkImportError>();

        var productsBySku = await _db.Products
            .Where(p => p.Sku != null)
            .ToDictionaryAsync(p => p.Sku!.ToLowerInvariant(), p => p.Id);

        for (int i = 0; i < rows.Count; i++)
        {
            var rowNum = i + 2;
            var row = rows[i];
            try
            {
                var name = Cell(row, headers, "nombre");
                if (string.IsNullOrWhiteSpace(name)) { skipped++; continue; }

                var mode = (Cell(row, headers, "modo_precio") ?? "auto").Trim().ToLowerInvariant();
                if (mode != "auto" && mode != "manual" && mode != "percent")
                    throw new InvalidOperationException($"Modo de precio invalido: '{mode}'. Aceptados: auto, manual, percent.");

                decimal? manual = null, percent = null;
                var pricePart = Cell(row, headers, "precio_o_porcentaje");
                if (!string.IsNullOrWhiteSpace(pricePart))
                {
                    if (mode == "manual") manual = ParseDecimal(pricePart);
                    else if (mode == "percent") percent = ParseDecimal(pricePart);
                }

                var itemsRaw = Cell(row, headers, "items");
                if (string.IsNullOrWhiteSpace(itemsRaw))
                    throw new InvalidOperationException("Tenés que indicar los items del combo (SKU:cantidad,SKU:cantidad).");

                var items = new List<ComboItemRequest>();
                foreach (var part in itemsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    var bits = part.Split(':');
                    if (bits.Length != 2) throw new InvalidOperationException($"Item con formato invalido: '{part}'. Usa SKU:cantidad.");
                    var sku = bits[0].Trim();
                    var qty = ParseInt(bits[1]) ?? 1;
                    if (!productsBySku.TryGetValue(sku.ToLowerInvariant(), out var pid))
                        throw new InvalidOperationException($"No existe un producto con SKU '{sku}'.");
                    items.Add(new ComboItemRequest(pid, Math.Max(1, qty)));
                }

                await _combos.CreateAsync(new CreateComboRequest(
                    Name: name!,
                    Sku: Cell(row, headers, "sku"),
                    Description: Cell(row, headers, "descripcion"),
                    Photo: null,
                    PriceMode: mode,
                    ManualPrice: manual,
                    PercentAdjustment: percent,
                    Items: items
                ));
                created++;
            }
            catch (Exception ex) { errors.Add(new BulkImportError(rowNum, ex.Message)); }
        }
        return new BulkImportResult(rows.Count, created, skipped, errors);
    }

    // ============================================================
    // HELPERS
    // ============================================================

    private static byte[] BuildTemplate(string sheetName, IEnumerable<(string header, string sample)> columns)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add(sheetName);
        int col = 1;
        foreach (var (header, sample) in columns)
        {
            ws.Cell(1, col).Value = header;
            ws.Cell(1, col).Style.Font.Bold = true;
            ws.Cell(1, col).Style.Fill.BackgroundColor = XLColor.LightGray;
            ws.Cell(2, col).Value = sample;
            ws.Cell(2, col).Style.Font.Italic = true;
            ws.Cell(2, col).Style.Font.FontColor = XLColor.Gray;
            col++;
        }
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static (List<string> headers, List<List<string?>> rows) ReadSheet(Stream stream)
    {
        using var wb = new XLWorkbook(stream);
        var ws = wb.Worksheets.First();
        var range = ws.RangeUsed();
        if (range is null) return (new(), new());

        var headerRow = range.FirstRow();
        var headers = headerRow.Cells().Select(c => (c.GetString() ?? "").Trim().ToLowerInvariant()).ToList();

        var rows = new List<List<string?>>();
        // Saltar fila de ejemplo: si la fila 2 contiene un placeholder con "(opcional)" o similar lo dejamos pasar al usuario.
        // Mejor: solo leer filas a partir de la 2 si tienen contenido.
        for (int r = 2; r <= range.LastRow().RowNumber(); r++)
        {
            var dataRow = ws.Row(r);
            var cells = new List<string?>();
            bool anyValue = false;
            for (int c = 1; c <= headers.Count; c++)
            {
                var cell = dataRow.Cell(c);
                var val = cell.IsEmpty() ? null : cell.GetString().Trim();
                if (!string.IsNullOrWhiteSpace(val)) anyValue = true;
                cells.Add(string.IsNullOrWhiteSpace(val) ? null : val);
            }
            if (anyValue) rows.Add(cells);
        }
        return (headers, rows);
    }

    private static string? Cell(List<string?> row, List<string> headers, string name)
    {
        var idx = headers.IndexOf(name);
        if (idx < 0 || idx >= row.Count) return null;
        return row[idx];
    }

    private static decimal? ParseDecimal(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var clean = s.Trim().Replace(" ", "");
        // Soportar coma o punto como decimal
        if (clean.Contains(',') && !clean.Contains('.')) clean = clean.Replace(',', '.');
        else if (clean.Contains(',') && clean.Contains('.'))
        {
            // Asume formato AR: 1.234,56 → 1234.56
            clean = clean.Replace(".", "").Replace(',', '.');
        }
        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static int? ParseInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return int.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static bool? ParseBool(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var v = s.Trim().ToLowerInvariant();
        return v switch
        {
            "s" or "si" or "sí" or "yes" or "y" or "true" or "1" or "x" => true,
            "n" or "no" or "false" or "0" => false,
            _ => null
        };
    }
}
