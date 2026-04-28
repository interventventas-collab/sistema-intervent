using Api.Data;
using Api.DTOs;
using Api.Models;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class SupplierPriceListService
{
    private readonly AppDbContext _db;

    public SupplierPriceListService(AppDbContext db)
    {
        _db = db;
    }

    // ===== Listas =====

    public async Task<List<SupplierPriceListDto>> GetAllAsync()
    {
        var lists = await _db.SupplierPriceLists.Include(l => l.Supplier)
            .OrderBy(l => l.Name).ToListAsync();
        if (lists.Count == 0) return new();

        var ids = lists.Select(l => l.Id).ToList();
        var counts = await _db.SupplierPriceListItems
            .Where(i => ids.Contains(i.PriceListId))
            .GroupBy(i => i.PriceListId)
            .Select(g => new { ListId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ListId, g => g.Count);

        var linked = await _db.Products
            .Where(p => p.SupplierPriceListItemId != null)
            .Join(_db.SupplierPriceListItems, p => p.SupplierPriceListItemId, i => i.Id, (p, i) => new { p, i })
            .GroupBy(x => x.i.PriceListId)
            .Select(g => new { ListId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ListId, g => g.Count);

        return lists.Select(l => new SupplierPriceListDto(
            l.Id, l.Name, l.SupplierId, l.Supplier?.Name, l.Notes, l.LastUploadAt,
            counts.GetValueOrDefault(l.Id, 0),
            linked.GetValueOrDefault(l.Id, 0),
            l.CreatedAt, l.UpdatedAt
        )).ToList();
    }

    public async Task<SupplierPriceListDto?> GetByIdAsync(int id)
    {
        var l = await _db.SupplierPriceLists.Include(x => x.Supplier).FirstOrDefaultAsync(x => x.Id == id);
        if (l is null) return null;
        var items = await _db.SupplierPriceListItems.CountAsync(i => i.PriceListId == id);
        var linked = await _db.Products.CountAsync(p => p.SupplierPriceListItem!.PriceListId == id);
        return new SupplierPriceListDto(l.Id, l.Name, l.SupplierId, l.Supplier?.Name, l.Notes, l.LastUploadAt,
            items, linked, l.CreatedAt, l.UpdatedAt);
    }

    public async Task<SupplierPriceListDto> CreateAsync(CreateSupplierPriceListRequest r)
    {
        var l = new SupplierPriceList
        {
            Name = r.Name.Trim(),
            SupplierId = r.SupplierId,
            Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _db.SupplierPriceLists.Add(l);
        await _db.SaveChangesAsync();
        return (await GetByIdAsync(l.Id))!;
    }

    public async Task<SupplierPriceListDto?> UpdateAsync(int id, UpdateSupplierPriceListRequest r)
    {
        var l = await _db.SupplierPriceLists.FindAsync(id);
        if (l is null) return null;
        if (r.Name is not null) l.Name = r.Name.Trim();
        if (r.SupplierId.HasValue) l.SupplierId = r.SupplierId.Value == 0 ? null : r.SupplierId.Value;
        if (r.Notes is not null) l.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
        l.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetByIdAsync(l.Id);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var l = await _db.SupplierPriceLists.FindAsync(id);
        if (l is null) return false;
        _db.SupplierPriceLists.Remove(l);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Items =====

    public async Task<List<SupplierPriceListItemDto>> GetItemsAsync(int priceListId, string? search = null)
    {
        var q = _db.SupplierPriceListItems.Where(i => i.PriceListId == priceListId).AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            q = q.Where(i => i.Code.ToLower().Contains(s) || (i.Description != null && i.Description.ToLower().Contains(s)));
        }
        var items = await q.OrderBy(i => i.Code).ToListAsync();
        if (items.Count == 0) return new();

        var itemIds = items.Select(i => i.Id).ToList();
        var linkedCounts = await _db.Products
            .Where(p => p.SupplierPriceListItemId != null && itemIds.Contains(p.SupplierPriceListItemId.Value))
            .GroupBy(p => p.SupplierPriceListItemId!.Value)
            .Select(g => new { ItemId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.ItemId, g => g.Count);

        return items.Select(i => new SupplierPriceListItemDto(
            i.Id, i.PriceListId, i.Code, i.Description, i.CostPrice, i.SuggestedRetailPrice, i.Notes,
            linkedCounts.GetValueOrDefault(i.Id, 0),
            i.CreatedAt, i.UpdatedAt
        )).ToList();
    }

    public async Task<SupplierPriceListItemDto> AddItemAsync(int priceListId, CreatePriceListItemRequest r)
    {
        var list = await _db.SupplierPriceLists.FindAsync(priceListId)
            ?? throw new InvalidOperationException("Lista no encontrada.");
        var code = r.Code.Trim();
        if (await _db.SupplierPriceListItems.AnyAsync(i => i.PriceListId == priceListId && i.Code == code))
            throw new InvalidOperationException($"Ya existe un item con codigo '{code}' en esta lista.");

        var item = new SupplierPriceListItem
        {
            PriceListId = priceListId,
            Code = code,
            Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim(),
            CostPrice = r.CostPrice,
            SuggestedRetailPrice = r.SuggestedRetailPrice,
            Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim(),
            CreatedAt = DateTime.UtcNow
        };
        _db.SupplierPriceListItems.Add(item);
        await _db.SaveChangesAsync();

        return new SupplierPriceListItemDto(item.Id, item.PriceListId, item.Code, item.Description,
            item.CostPrice, item.SuggestedRetailPrice, item.Notes, 0, item.CreatedAt, item.UpdatedAt);
    }

    public async Task<SupplierPriceListItemDto?> UpdateItemAsync(int itemId, UpdatePriceListItemRequest r)
    {
        var item = await _db.SupplierPriceListItems.FindAsync(itemId);
        if (item is null) return null;

        if (r.Code is not null)
        {
            var newCode = r.Code.Trim();
            if (newCode != item.Code && await _db.SupplierPriceListItems.AnyAsync(i => i.Id != itemId && i.PriceListId == item.PriceListId && i.Code == newCode))
                throw new InvalidOperationException($"Ya existe un item con codigo '{newCode}' en esta lista.");
            item.Code = newCode;
        }
        if (r.Description is not null) item.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim();
        var costChanged = r.CostPrice.HasValue && r.CostPrice.Value != item.CostPrice;
        if (r.CostPrice.HasValue) item.CostPrice = r.CostPrice.Value;
        if (r.SuggestedRetailPrice.HasValue) item.SuggestedRetailPrice = r.SuggestedRetailPrice.Value;
        if (r.Notes is not null) item.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim();
        item.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Propagar nuevo costo a productos vinculados.
        if (costChanged) await PropagateCostToProductsAsync(itemId, item.CostPrice);

        var linked = await _db.Products.CountAsync(p => p.SupplierPriceListItemId == itemId);
        return new SupplierPriceListItemDto(item.Id, item.PriceListId, item.Code, item.Description,
            item.CostPrice, item.SuggestedRetailPrice, item.Notes, linked, item.CreatedAt, item.UpdatedAt);
    }

    public async Task<bool> DeleteItemAsync(int itemId)
    {
        var item = await _db.SupplierPriceListItems.FindAsync(itemId);
        if (item is null) return false;
        _db.SupplierPriceListItems.Remove(item);
        await _db.SaveChangesAsync();
        return true;
    }

    // ===== Importacion masiva por Excel =====
    // Columnas esperadas: codigo, descripcion, costo, pvp_sugerido, notas
    public async Task<PriceListImportResult> ImportExcelAsync(int priceListId, Stream excelStream)
    {
        var list = await _db.SupplierPriceLists.FindAsync(priceListId)
            ?? throw new InvalidOperationException("Lista no encontrada.");

        using var workbook = new XLWorkbook(excelStream);
        var sheet = workbook.Worksheet(1);
        var rows = sheet.RangeUsed()?.RowsUsed().ToList() ?? new List<IXLRangeRow>();
        if (rows.Count < 2)
            return new PriceListImportResult(0, 0, 0, 0, 0, new List<string> { "El archivo esta vacio." });

        var headers = rows[0].Cells().Select(c => (c.GetString() ?? "").Trim().ToLowerInvariant()).ToList();
        int Idx(string name) => headers.IndexOf(name);
        int iCode = Idx("codigo");
        int iDesc = Idx("descripcion");
        int iCost = Idx("costo");
        int iSug = Idx("pvp_sugerido");
        int iNotes = Idx("notas");

        if (iCode < 0 || iCost < 0)
            return new PriceListImportResult(0, 0, 0, 0, 0, new List<string> { "Faltan columnas obligatorias 'codigo' y/o 'costo'." });

        // Cargar items existentes en memoria (lookup por code)
        var existing = await _db.SupplierPriceListItems
            .Where(i => i.PriceListId == priceListId)
            .ToDictionaryAsync(i => i.Code.ToLowerInvariant(), i => i);

        int created = 0, updated = 0, skipped = 0;
        var errors = new List<string>();
        var changedItemIds = new List<(int itemId, decimal newCost)>();

        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            try
            {
                string? Cell(int idx) => idx < 0 ? null : row.Cell(idx + 1).GetString()?.Trim();

                var code = Cell(iCode);
                if (string.IsNullOrWhiteSpace(code)) { skipped++; continue; }

                var costStr = Cell(iCost);
                var cost = ParseDecimal(costStr) ?? 0m;
                var sugStr = iSug >= 0 ? Cell(iSug) : null;
                var sug = ParseDecimal(sugStr);
                var desc = iDesc >= 0 ? Cell(iDesc) : null;
                var notes = iNotes >= 0 ? Cell(iNotes) : null;

                if (existing.TryGetValue(code.ToLowerInvariant(), out var item))
                {
                    var oldCost = item.CostPrice;
                    if (!string.IsNullOrWhiteSpace(desc)) item.Description = desc;
                    item.CostPrice = cost;
                    if (sug.HasValue) item.SuggestedRetailPrice = sug;
                    if (!string.IsNullOrWhiteSpace(notes)) item.Notes = notes;
                    item.UpdatedAt = DateTime.UtcNow;
                    if (oldCost != cost) changedItemIds.Add((item.Id, cost));
                    updated++;
                }
                else
                {
                    var item2 = new SupplierPriceListItem
                    {
                        PriceListId = priceListId,
                        Code = code,
                        Description = string.IsNullOrWhiteSpace(desc) ? null : desc,
                        CostPrice = cost,
                        SuggestedRetailPrice = sug,
                        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes,
                        CreatedAt = DateTime.UtcNow
                    };
                    _db.SupplierPriceListItems.Add(item2);
                    existing[code.ToLowerInvariant()] = item2;
                    created++;
                }
            }
            catch (Exception ex)
            {
                errors.Add($"Fila {r + 1}: {ex.Message}");
            }
        }

        list.LastUploadAt = DateTime.UtcNow;
        list.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Propagar costos a productos vinculados.
        int linkedUpdated = 0;
        foreach (var (itemId, newCost) in changedItemIds)
        {
            linkedUpdated += await PropagateCostToProductsAsync(itemId, newCost);
        }

        return new PriceListImportResult(rows.Count - 1, created, updated, skipped, linkedUpdated, errors);
    }

    private async Task<int> PropagateCostToProductsAsync(int itemId, decimal newCost)
    {
        var products = await _db.Products.Where(p => p.SupplierPriceListItemId == itemId).ToListAsync();
        if (products.Count == 0) return 0;
        foreach (var p in products)
        {
            p.CostPrice = newCost;
            p.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return products.Count;
    }

    private static decimal? ParseDecimal(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var clean = raw.Trim();
        var lastComma = clean.LastIndexOf(',');
        var lastDot = clean.LastIndexOf('.');
        var normalized = lastComma > lastDot
            ? clean.Replace(".", "").Replace(",", ".")
            : clean.Replace(",", "");
        return decimal.TryParse(normalized, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}
