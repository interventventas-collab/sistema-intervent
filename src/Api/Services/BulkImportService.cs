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
        int created = 0, skipped = 0;
        var errors = new List<BulkImportError>();
        var warnings = new List<BulkImportWarning>();

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
        return new BulkImportResult(rows.Count, created, 0, skipped, errors, warnings);
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
                    Notes: Cell(row, headers, "notas"),
                    CustomerTierId: null
                ));
                created++;
            }
            catch (Exception ex) { errors.Add(new BulkImportError(rowNum, ex.Message)); }
        }
        return new BulkImportResult(rows.Count, created, 0, skipped, errors, new List<BulkImportWarning>());
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
                    HasExpiry: ParseBool(Cell(row, headers, "vencimiento")),
                    Companies: Cell(row, headers, "empresas")
                ));
                created++;
            }
            catch (Exception ex) { errors.Add(new BulkImportError(rowNum, ex.Message)); }
        }
        return new BulkImportResult(rows.Count, created, 0, skipped, errors, new List<BulkImportWarning>());
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
        ("tipo", "independiente (opciones: padre / hijo / independiente — vacio = independiente)"),
        ("producto_base_sku", "9311 (solo si tipo=hijo: SKU del padre. Si tipo=padre o independiente, dejar vacio)"),
        ("sku", "C9311BL"),
        ("codigo_de_barras", "7790140123456"),
        ("codigo_oem", "OEM-X"),
        ("url_imagen", "https://ejemplo.com/img.jpg"),
        ("precio_costo", "1500.50 (SIN IVA. Se ignora si tipo=hijo, hereda del padre)"),
        ("precio_venta", "2478.51 (SIN IVA. Si llenas precio_venta_con_iva, ignora esta)"),
        ("precio_venta_con_iva", "2999.99 (precio final CON IVA tal como te lo manda el proveedor. Si esta llena, prevalece sobre precio_venta)"),
        ("iva", "21"),
        ("cuenta_compra", "511001"),
        ("cuenta_venta", "411001"),
        ("cuenta_mercaderia", "113001"),
        ("stock", "10 (se ignora si tipo=padre — el stock del padre es la suma de los hijos)"),
        ("stock_critico", "2"),
        ("uxb", "12 (unidades por bulto, opcional)"),
        ("activo", "si (opciones: si / no — vacio = si)")
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
        ("precio_costo", "1500.50 (SIN IVA)"),
        ("precio_venta", "2478.51 (SIN IVA. Si llenas precio_venta_con_iva, ignora esta)"),
        ("precio_venta_con_iva", "2999.99 (precio final CON IVA tal como te lo manda el proveedor)"),
        ("iva", "21"),
        ("cuenta_compra", "511001"),
        ("cuenta_venta", "411001"),
        ("cuenta_mercaderia", "113001"),
        ("stock_critico", "2"),
        ("uxb", "12 (unidades por bulto, opcional)"),
        ("activo", "si (opciones: si / no — vacio = si)")
    });

    public async Task<BulkImportResult> ImportProductsAsync(Stream excelStream, bool markAsBase = false)
    {
        var (headers, rows) = ReadSheet(excelStream);
        int created = 0, updated = 0, skipped = 0;
        var errors = new List<BulkImportError>();
        var warnings = new List<BulkImportWarning>();

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
            var sku = Cell(row, headers, "sku");

            // Saltar SOLO si la fila esta totalmente vacia (sin SKU y sin titulo).
            // Si tiene SKU, procesarla aunque no tenga titulo: si el SKU ya existe en la DB,
            // se hace update parcial respetando los datos existentes.
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(sku))
            {
                skipped++; continue;
            }

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
                    var result = await CreateProductFromRow(row, headers, brandsByName, productIdBySku, markAsBase);
                    if (result is null) continue;
                    var dto = result.Product;
                    if (!string.IsNullOrEmpty(dto.Sku))
                        productIdBySku[dto.Sku.ToLowerInvariant()] = dto.Id;
                    if (result.Action == "updated") updated++;
                    else created++;
                    if (!string.IsNullOrEmpty(result.PriceWarning))
                        warnings.Add(new BulkImportWarning(rowNum, $"{dto.Sku ?? dto.Title}: {result.PriceWarning}"));
                }
                catch (Exception ex) { errors.Add(new BulkImportError(rowNum, ex.Message)); }
            }
        }

        await ProcessAsync(withoutBase);
        await ProcessAsync(withBase);

        return new BulkImportResult(rows.Count, created, updated, skipped, errors, warnings);
    }

    // ============================================================
    // IMPORT POR OEM (codigo del proveedor)
    // ============================================================
    // A diferencia de ImportProductsAsync que matchea por SKU, este metodo busca por
    // OemCode. Util para listas de precios del proveedor que vienen con SU codigo
    // (ej: '8733', '9335') y no con el SKU interno tuyo (ej: 'C8733BL').
    // Si el OEM existe en uno o mas productos -> los actualiza a TODOS.
    // Si no existe y createIfMissing=true -> crea uno nuevo con Sku=OEM, OemCode=OEM.

    public byte[] BuildProductsByOemTemplate() => BuildTemplate("Productos por OEM", new[]
    {
        ("codigo_oem", "8733 (codigo del proveedor — clave de matching)"),
        ("sku_interno", "C8733BL (opcional — si lo llenas, ese sera el SKU del producto. Vacio = usa el OEM)"),
        ("titulo", "Cajonera en Torre x 3 Grande Blanca"),
        ("marca", "Colombraro (nombre exacto)"),
        ("precio_costo", "35501.25 (SIN IVA)"),
        ("precio_venta", "65719.01 (SIN IVA, opcional)"),
        ("precio_venta_con_iva", "79520.00 (CON IVA, opcional — si lo llenas, ignora precio_venta)"),
        ("iva", "21"),
        ("codigo_de_barras", "7790733087333 (opcional)"),
        ("stock_critico", "2 (opcional)")
    });

    public async Task<BulkImportResult> ImportProductsByOemAsync(
        Stream excelStream,
        bool createIfMissing = true,
        bool loadAsInactive = false)
    {
        var (headers, rows) = ReadSheet(excelStream);
        int created = 0, updated = 0, skipped = 0;
        var errors = new List<BulkImportError>();
        var warnings = new List<BulkImportWarning>();

        var brandsByName = await _db.Brands
            .ToDictionaryAsync(b => b.Name.ToLowerInvariant(), b => b.Id);

        // Indice de productos por OemCode (un OEM puede mapear a varios productos = sus colores/variantes)
        var productsByOem = (await _db.Products
            .Where(p => p.OemCode != null && p.OemCode != "")
            .Select(p => new { p.Id, p.OemCode })
            .ToListAsync())
            .GroupBy(p => p.OemCode!.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.Select(x => x.Id).ToList());

        // Indice de productos por Sku (clave primaria de identidad)
        var productIdBySku = (await _db.Products
            .Where(p => p.Sku != null && p.Sku != "")
            .Select(p => new { p.Id, p.Sku })
            .ToListAsync())
            .GroupBy(p => p.Sku!.ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First().Id);

        for (int i = 0; i < rows.Count; i++)
        {
            var rowNum = i + 2;
            var row = rows[i];
            try
            {
                var oem = Nz(Cell(row, headers, "codigo_oem"));
                if (oem is null)
                {
                    skipped++;
                    continue;
                }

                var title = Nz(Cell(row, headers, "titulo"));
                var brandName = Nz(Cell(row, headers, "marca"));
                int? brandId = null;
                if (brandName is not null)
                {
                    if (!brandsByName.TryGetValue(brandName.ToLowerInvariant(), out var bid))
                        throw new InvalidOperationException($"La marca '{brandName}' no existe. Cargala primero.");
                    brandId = bid;
                }

                var vatRate = ParseDecimal(Cell(row, headers, "iva"));
                var costExcel = ParseDecimal(Cell(row, headers, "precio_costo"));
                var pvpConIva = ParseDecimal(Cell(row, headers, "precio_venta_con_iva"));
                var pvpSinIva = ParseDecimal(Cell(row, headers, "precio_venta"));

                decimal? retailFromExcel = null;
                if (pvpConIva.HasValue)
                {
                    var rate = vatRate ?? 0m;
                    retailFromExcel = rate > 0m
                        ? Math.Round(pvpConIva.Value / (1m + rate / 100m), 2, MidpointRounding.AwayFromZero)
                        : pvpConIva.Value;
                }
                else if (pvpSinIva.HasValue)
                {
                    retailFromExcel = pvpSinIva.Value;
                }

                var stockCritico = ParseInt(Cell(row, headers, "stock_critico"));
                var barcode = Nz(Cell(row, headers, "codigo_de_barras"));
                // SKU interno: prioridad 'sku_interno' > 'sku' > el OEM mismo.
                var skuExplicito = Nz(Cell(row, headers, "sku_interno")) ?? Nz(Cell(row, headers, "sku"));
                var skuInterno = skuExplicito ?? oem;

                // === LOGICA DE MATCHING ===
                // Prioridad 1: si la fila trae SKU interno explicito, matchear por SKU exacto.
                //   - Match -> UPDATE solo ese producto
                //   - No match -> CREATE producto nuevo (cada SKU explicito = producto distinto)
                // Prioridad 2: si NO trae SKU explicito, matchear por OEM (legacy: actualizar todos los del OEM).
                List<int> existingIds;
                if (skuExplicito is not null)
                {
                    existingIds = productIdBySku.TryGetValue(skuExplicito.ToLowerInvariant(), out var eid)
                        ? new List<int> { eid }
                        : new List<int>();
                }
                else
                {
                    existingIds = productsByOem.GetValueOrDefault(oem.ToLowerInvariant(), new List<int>());
                }

                if (existingIds.Count > 0)
                {
                    // Update — actualiza el/los productos encontrados (por SKU si vino explicito,
                    // o por OEM si no). No tocamos SKU, stock ni IsActive.
                    foreach (var pid in existingIds)
                    {
                        await _products.UpdateAsync(pid, new UpdateProductRequest(
                            Title: title,
                            DisplayName: null,
                            Description: null,
                            Brand: brandName,
                            Model: null,
                            Sku: null,
                            Barcode: barcode,
                            OemCode: oem,
                            ImageUrl: null, Photo1: null, Photo2: null, Photo3: null,
                            CostPrice: costExcel,
                            RetailPrice: retailFromExcel,
                            VatRate: vatRate,
                            PurchaseAccount: null, SaleAccount: null, InventoryAccount: null,
                            Stock: null,
                            CriticalStock: stockCritico,
                            StockUnit: null,
                            IsActive: null,
                            BaseProductId: null, ClearBaseProduct: null,
                            BrandId: brandId, ClearBrand: null,
                            IsBase: null, IsService: null,
                            UnitsPerPack: null, ClearUnitsPerPack: null,
                            Fraction: null, MarkupAmount: null
                        ));
                        updated++;
                    }
                    if (skuExplicito is null && existingIds.Count > 1)
                    {
                        warnings.Add(new BulkImportWarning(rowNum,
                            $"OEM '{oem}' actualizo {existingIds.Count} productos (variantes que comparten el mismo OEM)."));
                    }
                }
                else if (createIfMissing)
                {
                    if (string.IsNullOrWhiteSpace(title))
                        throw new InvalidOperationException(
                            $"OEM '{oem}' no existe en la base y la fila no tiene 'titulo'. " +
                            "Para crear un producto nuevo necesitas titulo + codigo_oem.");

                    var result = await _products.CreateOrUpdateAsync(new CreateProductRequest(
                        Title: title!,
                        DisplayName: null, Description: null,
                        Brand: brandName, Model: null,
                        Sku: skuInterno,    // si vino 'sku_interno' lo usa; sino el OEM
                        Barcode: barcode,
                        OemCode: oem,
                        ImageUrl: null, Photo1: null, Photo2: null, Photo3: null,
                        CostPrice: costExcel ?? 0m,
                        RetailPrice: retailFromExcel ?? 0m,
                        VatRate: vatRate,
                        PurchaseAccount: null, SaleAccount: null, InventoryAccount: null,
                        Stock: 0,                  // siempre 0 al crear; el stock entra por "Modificacion de stock"
                        CriticalStock: stockCritico ?? 0,
                        StockUnit: "unidad",
                        BaseProductId: null,
                        BrandId: brandId,
                        IsBase: false,
                        IsService: false,
                        UnitsPerPack: null,
                        Fraction: null,
                        MarkupAmount: null
                    ));
                    if (result is not null)
                    {
                        // Si pidieron carga inactiva, marcamos IsActive=false en un segundo paso
                        // (CreateOrUpdateAsync siempre crea Active=true).
                        if (loadAsInactive)
                        {
                            await _products.UpdateAsync(result.Product.Id, new UpdateProductRequest(
                                Title: null, DisplayName: null, Description: null,
                                Brand: null, Model: null, Sku: null, Barcode: null, OemCode: null,
                                ImageUrl: null, Photo1: null, Photo2: null, Photo3: null,
                                CostPrice: null, RetailPrice: null, VatRate: null,
                                PurchaseAccount: null, SaleAccount: null, InventoryAccount: null,
                                Stock: null, CriticalStock: null, StockUnit: null,
                                IsActive: false,
                                BaseProductId: null, ClearBaseProduct: null,
                                BrandId: null, ClearBrand: null,
                                IsBase: null, IsService: null,
                                UnitsPerPack: null, ClearUnitsPerPack: null,
                                Fraction: null, MarkupAmount: null
                            ));
                        }
                        created++;
                        if (!productsByOem.ContainsKey(oem.ToLowerInvariant()))
                            productsByOem[oem.ToLowerInvariant()] = new List<int>();
                        productsByOem[oem.ToLowerInvariant()].Add(result.Product.Id);
                        // Indexar tambien por SKU para que filas posteriores con el mismo SKU
                        // se traten como UPDATE (no como CREATE de un duplicado).
                        if (!string.IsNullOrEmpty(result.Product.Sku))
                            productIdBySku[result.Product.Sku.ToLowerInvariant()] = result.Product.Id;
                    }
                }
                else
                {
                    skipped++;
                    warnings.Add(new BulkImportWarning(rowNum,
                        $"OEM '{oem}' no existe y la opcion 'crear si falta' esta apagada — fila ignorada."));
                }
            }
            catch (Exception ex) { errors.Add(new BulkImportError(rowNum, ex.Message)); }
        }

        // Auto-relink solo cuando NO es carga inactiva. Si los productos vienen inactivos,
        // los SKUs aun no son los definitivos — re-vincular ahora puede asociar mal.
        if (!loadAsInactive)
        {
            try
            {
                var report = await RelinkOrphanMeliItemsExactAsync();
                var total = report.LinkedBySku + report.LinkedByOem;
                if (total > 0)
                    warnings.Add(new BulkImportWarning(0,
                        $"Re-vinculadas {total} publicaciones de ML ({report.LinkedBySku} por SKU exacto, {report.LinkedByOem} por OEM exacto). Quedaron {report.RemainingOrphans} huerfanas."));
            }
            catch { /* silencioso */ }
        }

        return new BulkImportResult(rows.Count, created, updated, skipped, errors, warnings);
    }

    /// <summary>
    /// Re-vincula publicaciones ML huerfanas (ProductId IS NULL) usando matcheo exacto:
    /// 1) Pasada A: Product.Sku == MeliItem.Sku (case-insensitive, trim) — solo si UN unico producto matchea.
    /// 2) Pasada B: Product.OemCode == MeliItem.Sku — idem (sobre las que quedaron huerfanas).
    /// Combos como 'C9335GRX2' no matchean a 'C9335GR' porque exigimos igualdad exacta.
    /// Es seguro llamar varias veces (idempotente).
    /// </summary>
    public class RelinkReport
    {
        public int LinkedBySku { get; set; }
        public int LinkedByOem { get; set; }
        public int RemainingOrphans { get; set; }
    }

    public async Task<RelinkReport> RelinkOrphanMeliItemsExactAsync()
    {
        // Pasada A: match exacto por SKU
        var sqlBySku = @"
        UPDATE mi
           SET ProductId = p.Id
          FROM MeliItems mi
          JOIN Products p ON LOWER(LTRIM(RTRIM(p.Sku))) = LOWER(LTRIM(RTRIM(mi.Sku)))
         WHERE mi.ProductId IS NULL
           AND mi.Sku IS NOT NULL AND LTRIM(RTRIM(mi.Sku)) <> ''
           AND p.Sku IS NOT NULL AND LTRIM(RTRIM(p.Sku)) <> ''
           AND (SELECT COUNT(*) FROM Products px
                 WHERE LOWER(LTRIM(RTRIM(px.Sku))) = LOWER(LTRIM(RTRIM(mi.Sku)))) = 1;
        SELECT @@ROWCOUNT AS Value;";
        var bySku = await _db.Database.SqlQueryRaw<int>(sqlBySku).FirstOrDefaultAsync();

        // Pasada B: match exacto por OEM
        var sqlByOem = @"
        UPDATE mi
           SET ProductId = p.Id
          FROM MeliItems mi
          JOIN Products p ON LOWER(LTRIM(RTRIM(p.OemCode))) = LOWER(LTRIM(RTRIM(mi.Sku)))
         WHERE mi.ProductId IS NULL
           AND mi.Sku IS NOT NULL AND LTRIM(RTRIM(mi.Sku)) <> ''
           AND p.OemCode IS NOT NULL AND LTRIM(RTRIM(p.OemCode)) <> ''
           AND (SELECT COUNT(*) FROM Products px
                 WHERE LOWER(LTRIM(RTRIM(px.OemCode))) = LOWER(LTRIM(RTRIM(mi.Sku)))) = 1;
        SELECT @@ROWCOUNT AS Value;";
        var byOem = await _db.Database.SqlQueryRaw<int>(sqlByOem).FirstOrDefaultAsync();

        var orphansLeft = await _db.MeliItems.CountAsync(m => m.ProductId == null);

        return new RelinkReport { LinkedBySku = bySku, LinkedByOem = byOem, RemainingOrphans = orphansLeft };
    }

    private async Task<ProductUpsertResult?> CreateProductFromRow(
        List<string?> row, List<string> headers,
        Dictionary<string, int> brandsByName,
        Dictionary<string, int> productIdBySku,
        bool markAsBase = false)
    {
        var title = Nz(Cell(row, headers, "titulo"));

        // VALIDACION OBLIGATORIA: el SKU es la clave para no duplicar al re-importar.
        // Sin SKU el sistema no puede saber si un producto ya existe -> crearia duplicados
        // fantasma. Rechazamos la fila con error claro.
        var sku = Nz(Cell(row, headers, "sku"));
        if (sku is null)
            throw new InvalidOperationException(
                $"El SKU es obligatorio. La fila '{title ?? "(sin titulo)"}' no tiene SKU. " +
                "Si volves a subir el mismo Excel, los productos sin SKU se crean duplicados.");

        // Cargar el producto existente (si lo hay) para usarlo como FALLBACK cuando
        // una celda del Excel viene vacia. Asi no pisamos datos buenos con vacios.
        ProductListDto? existing = null;
        var existingId = productIdBySku.GetValueOrDefault(sku.ToLowerInvariant());
        if (existingId > 0) existing = await _products.GetByIdAsync(existingId);

        // Para CREAR un producto nuevo (no existe el SKU todavia) hace falta titulo.
        // Para ACTUALIZAR uno existente, el titulo se hereda del producto previo si viene vacio.
        if (existing is null && string.IsNullOrWhiteSpace(title))
            throw new InvalidOperationException(
                $"El SKU '{sku}' no existe todavia y la fila no tiene titulo. " +
                "Para crear un producto nuevo necesitas al menos titulo + SKU.");

        int? brandId = existing?.BrandId;
        var brandName = Nz(Cell(row, headers, "marca"));
        if (brandName is not null)
        {
            if (!brandsByName.TryGetValue(brandName.ToLowerInvariant(), out var bid))
                throw new InvalidOperationException($"La marca '{brandName}' no existe. Cargala primero.");
            brandId = bid;
        }

        var baseSku = Nz(Cell(row, headers, "producto_base_sku"));
        var tipoRaw = (Cell(row, headers, "tipo") ?? "").Trim().ToLowerInvariant();

        // Resolver tipo del producto.
        bool isPadre, isHijo;
        if (tipoRaw is "padre" or "padres")
        {
            isPadre = true; isHijo = false;
        }
        else if (tipoRaw is "hijo" or "hijos")
        {
            isPadre = false; isHijo = true;
            if (baseSku is null)
                throw new InvalidOperationException("Producto marcado como 'hijo' pero la columna 'producto_base_sku' esta vacia. Indicá el SKU del padre.");
        }
        else if (tipoRaw is "independiente" or "indep" or "")
        {
            if (tipoRaw == "")
            {
                // Vacio: si existe, respetar lo que ya era (no cambiar tipo).
                // Si es nuevo: usar markAsBase + producto_base_sku como antes.
                if (existing is not null)
                {
                    isPadre = existing.IsBase || existing.DerivedCount > 0;
                    isHijo = existing.BaseProductId.HasValue;
                }
                else
                {
                    isPadre = markAsBase;
                    isHijo = !markAsBase && baseSku is not null;
                }
            }
            else
            {
                isPadre = false; isHijo = false;
                baseSku = null;
            }
        }
        else
        {
            throw new InvalidOperationException($"Valor invalido en columna 'tipo': '{tipoRaw}'. Opciones validas: padre / hijo / independiente.");
        }

        int? baseId = existing?.BaseProductId;
        if (isHijo && baseSku is not null)
        {
            if (!productIdBySku.TryGetValue(baseSku.ToLowerInvariant(), out var bid))
                throw new InvalidOperationException($"El producto base con SKU '{baseSku}' no existe. Cargalo primero (como 'tipo=padre').");
            baseId = bid;
        }
        else if (!isHijo)
        {
            baseId = null; // independiente o padre: sin padre
        }

        // === Precios e IVA ===
        // IVA: Excel manda → existente → null.
        var vatRate = ParseDecimal(Cell(row, headers, "iva")) ?? existing?.VatRate;

        // Costo: el costo siempre es SIN IVA.
        var costExcel = ParseDecimal(Cell(row, headers, "precio_costo"));
        var costPrice = costExcel ?? existing?.CostPrice ?? 0m;

        // PVP: dos columnas alternativas. Si llenas precio_venta_con_iva, descontamos
        // el IVA y guardamos el equivalente sin IVA. Si llenas precio_venta, se usa
        // tal cual (sin IVA). Si las dos vienen, prevalece precio_venta_con_iva.
        var pvpConIva = ParseDecimal(Cell(row, headers, "precio_venta_con_iva"));
        var pvpSinIvaCell = ParseDecimal(Cell(row, headers, "precio_venta"));
        decimal retailPrice;
        if (pvpConIva.HasValue)
        {
            var rate = vatRate ?? 0m;
            retailPrice = rate > 0m
                ? Math.Round(pvpConIva.Value / (1m + rate / 100m), 2, MidpointRounding.AwayFromZero)
                : pvpConIva.Value;
        }
        else if (pvpSinIvaCell.HasValue)
        {
            retailPrice = pvpSinIvaCell.Value;
        }
        else
        {
            retailPrice = existing?.RetailPrice ?? 0m;
        }

        // Stock y stock critico: empty -> existente -> 0.
        var stock = ParseInt(Cell(row, headers, "stock")) ?? existing?.Stock ?? 0;
        var stockCritico = ParseInt(Cell(row, headers, "stock_critico")) ?? existing?.CriticalStock ?? 0;
        // UxB: empty -> existente -> null.
        var uxb = ParseInt(Cell(row, headers, "uxb")) ?? existing?.UnitsPerPack;

        var result = await _products.CreateOrUpdateAsync(new CreateProductRequest(
            Title: title ?? existing?.Title ?? "",
            DisplayName: Nz(Cell(row, headers, "nombre_para_mostrar")),
            Description: Nz(Cell(row, headers, "descripcion")),
            Brand: brandName,
            Model: Nz(Cell(row, headers, "modelo")),
            Sku: sku,
            Barcode: Nz(Cell(row, headers, "codigo_de_barras")),
            OemCode: Nz(Cell(row, headers, "codigo_oem")),
            ImageUrl: Nz(Cell(row, headers, "url_imagen")),
            Photo1: null, Photo2: null, Photo3: null,
            CostPrice: costPrice,
            RetailPrice: retailPrice,
            VatRate: vatRate,
            PurchaseAccount: Nz(Cell(row, headers, "cuenta_compra")),
            SaleAccount: Nz(Cell(row, headers, "cuenta_venta")),
            InventoryAccount: Nz(Cell(row, headers, "cuenta_mercaderia")),
            Stock: stock,
            CriticalStock: stockCritico,
            StockUnit: "unidad",
            BaseProductId: baseId,
            BrandId: brandId,
            IsBase: isPadre,
            IsService: false,
            UnitsPerPack: uxb,
            Fraction: null,
            MarkupAmount: null
        ));

        // Si el Excel pidio activo=no (false), ajustar despues de crear (el create por default es activo).
        if (result is not null)
        {
            var activo = ParseBool(Cell(row, headers, "activo"));
            if (activo == false)
            {
                await _products.UpdateAsync(result.Product.Id, new UpdateProductRequest(
                    Title: null, DisplayName: null, Description: null,
                    Brand: null, Model: null, Sku: null, Barcode: null, OemCode: null,
                    ImageUrl: null, Photo1: null, Photo2: null, Photo3: null,
                    CostPrice: null, RetailPrice: null, VatRate: null,
                    PurchaseAccount: null, SaleAccount: null, InventoryAccount: null,
                    Stock: null, CriticalStock: null, StockUnit: null,
                    IsActive: false,
                    BaseProductId: null, ClearBaseProduct: null,
                    BrandId: null, ClearBrand: null,
                    IsBase: null, IsService: null,
                    UnitsPerPack: null, ClearUnitsPerPack: null,
                    Fraction: null, MarkupAmount: null
                ));
            }
        }

        return result;
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
        return new BulkImportResult(rows.Count, created, 0, skipped, errors, new List<BulkImportWarning>());
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

    /// <summary>
    /// Convierte celda vacia o solo-espacios en null. Asi, en updates, los campos vacios
    /// del Excel NO pisan los valores existentes en la DB (la API ignora null).
    /// </summary>
    private static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

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
