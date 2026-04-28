using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class SaleService
{
    private readonly AppDbContext _db;

    public SaleService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<SaleDto>> GetAllAsync()
    {
        var sales = await _db.Sales
            .Include(s => s.Client)
            .Include(s => s.Items)
            .OrderByDescending(s => s.Date)
            .ThenByDescending(s => s.Id)
            .ToListAsync();

        return sales.Select(BuildDto).ToList();
    }

    public async Task<SaleDto?> GetByIdAsync(int id)
    {
        var s = await _db.Sales
            .Include(x => x.Client)
            .Include(x => x.Items)
            .FirstOrDefaultAsync(x => x.Id == id);
        return s is null ? null : BuildDto(s);
    }

    public async Task<SaleDto> CreateAsync(CreateSaleRequest request)
    {
        if (request.Items is null || request.Items.Count == 0)
            throw new InvalidOperationException("La venta tiene que tener al menos un producto.");

        // Resolver cliente (opcional)
        Client? client = null;
        if (request.ClientId.HasValue)
        {
            client = await _db.Clients.FindAsync(request.ClientId.Value)
                ?? throw new InvalidOperationException("Cliente no encontrado.");
        }

        // Calcular items y totales
        var items = new List<SaleItem>();
        decimal subtotal = 0m;
        foreach (var i in request.Items)
        {
            if (i.Quantity <= 0)
                throw new InvalidOperationException("La cantidad de cada item debe ser mayor a 0.");

            string? code = i.Code;
            string description = i.Description;
            decimal unit = i.UnitPrice;
            decimal? vat = i.VatRate;

            if (i.ProductId.HasValue)
            {
                var product = await _db.Products.FindAsync(i.ProductId.Value)
                    ?? throw new InvalidOperationException($"Producto {i.ProductId} no encontrado.");
                code ??= product.Sku;
                if (string.IsNullOrWhiteSpace(description)) description = product.DisplayName ?? product.Title;
                if (unit <= 0) unit = product.RetailPrice;
                vat ??= product.VatRate;
            }

            var bruto = i.Quantity * unit;
            var bonif = bruto * (i.BonifPercent / 100m);
            var lineTotal = Math.Round(bruto - bonif, 2);

            items.Add(new SaleItem
            {
                ProductId = i.ProductId,
                Code = code,
                Description = description,
                Quantity = i.Quantity,
                UnitPrice = unit,
                VatRate = vat,
                BonifPercent = i.BonifPercent,
                LineTotal = lineTotal
            });
            subtotal += lineTotal;
        }

        var discount = Math.Max(0m, request.Discount);
        var total = Math.Max(0m, subtotal - discount);

        var sale = new Sale
        {
            Number = await GenerateNumberAsync(),
            Date = (request.Date ?? DateTime.UtcNow).Date,
            DueDate = request.DueDate?.Date,
            ClientId = client?.Id,
            ClientNameSnapshot = request.ClientNameOverride ?? client?.Name,
            ClientAddressSnapshot = client?.Address,
            ClientCityLocationSnapshot = null,
            ClientCuitSnapshot = client?.Cuit,
            PaymentCondition = string.IsNullOrWhiteSpace(request.PaymentCondition) ? "Efectivo" : request.PaymentCondition,
            IvaCondition = string.IsNullOrWhiteSpace(request.IvaCondition) ? "Consumidor final" : request.IvaCondition,
            Subtotal = Math.Round(subtotal, 2),
            Discount = Math.Round(discount, 2),
            Total = Math.Round(total, 2),
            AmountInWords = NumberToWordsEs.AmountToPesos(total),
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes,
            WeekDays = string.IsNullOrWhiteSpace(request.WeekDays) ? null : request.WeekDays,
            IsPaid = request.IsPaid ?? false,
            CompanyNameSnapshot = string.IsNullOrWhiteSpace(request.CompanyNameOverride) ? null : request.CompanyNameOverride.Trim(),
            CreatedAt = DateTime.UtcNow,
            Items = items
        };

        _db.Sales.Add(sale);
        await _db.SaveChangesAsync();

        return (await GetByIdAsync(sale.Id))!;
    }

    public async Task<SaleDto?> UpdateFlagsAsync(int id, UpdateSaleFlagsRequest request)
    {
        var sale = await _db.Sales.FindAsync(id);
        if (sale is null) return null;
        if (request.WeekDays is not null)
            sale.WeekDays = string.IsNullOrWhiteSpace(request.WeekDays) ? null : request.WeekDays;
        if (request.IsPaid.HasValue) sale.IsPaid = request.IsPaid.Value;
        sale.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetByIdAsync(id);
    }

    public async Task<SaleDto?> CancelAsync(int id)
    {
        var sale = await _db.Sales.FindAsync(id);
        if (sale is null) return null;
        if (!sale.IsCancelled)
        {
            sale.IsCancelled = true;
            sale.CancelledAt = DateTime.UtcNow;
            sale.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
        return await GetByIdAsync(id);
    }

    public async Task<CompanyInfoDto> GetCompanyInfoAsync()
    {
        var keys = new[] {
            "company.name", "company.cuit", "company.address", "company.phone",
            "company.email", "company.web", "company.iva_condition",
            "company.iibb", "company.activity_start", "sales.point_of_sale"
        };
        var settings = await _db.AppSettings
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);

        return new CompanyInfoDto(
            settings.GetValueOrDefault("company.name", ""),
            settings.GetValueOrDefault("company.cuit", ""),
            settings.GetValueOrDefault("company.address", ""),
            settings.GetValueOrDefault("company.phone", ""),
            settings.GetValueOrDefault("company.email", ""),
            settings.GetValueOrDefault("company.web", ""),
            settings.GetValueOrDefault("company.iva_condition", ""),
            settings.GetValueOrDefault("company.iibb", ""),
            settings.GetValueOrDefault("company.activity_start", ""),
            settings.GetValueOrDefault("sales.point_of_sale", "0001")
        );
    }

    public async Task<CompanyInfoDto> UpdateCompanyInfoAsync(CompanyInfoDto dto)
    {
        var pairs = new Dictionary<string, string>
        {
            ["company.name"] = dto.Name ?? "",
            ["company.cuit"] = dto.Cuit ?? "",
            ["company.address"] = dto.Address ?? "",
            ["company.phone"] = dto.Phone ?? "",
            ["company.email"] = dto.Email ?? "",
            ["company.web"] = dto.Web ?? "",
            ["company.iva_condition"] = dto.IvaCondition ?? "",
            ["company.iibb"] = dto.Iibb ?? "",
            ["company.activity_start"] = dto.ActivityStart ?? "",
            ["sales.point_of_sale"] = dto.PointOfSale ?? "0001"
        };

        foreach (var (key, value) in pairs)
        {
            var existing = await _db.AppSettings.FindAsync(key);
            if (existing is null)
            {
                _db.AppSettings.Add(new Models.AppSetting { Key = key, Value = value, UpdatedAt = DateTime.UtcNow });
            }
            else
            {
                existing.Value = value;
                existing.UpdatedAt = DateTime.UtcNow;
            }
        }
        await _db.SaveChangesAsync();
        return await GetCompanyInfoAsync();
    }

    // === Helpers ===

    private async Task<string> GenerateNumberAsync()
    {
        var pos = (await _db.AppSettings.FindAsync("sales.point_of_sale"))?.Value ?? "0001";
        // Tomar el ultimo numero usado para ese punto de venta
        var lastForPos = await _db.Sales
            .Where(s => s.Number.StartsWith(pos + "-"))
            .OrderByDescending(s => s.Id)
            .Select(s => s.Number)
            .FirstOrDefaultAsync();

        int next = 1;
        if (lastForPos is not null)
        {
            var idx = lastForPos.IndexOf('-');
            if (idx > 0 && int.TryParse(lastForPos[(idx + 1)..], out var prev))
                next = prev + 1;
        }
        return $"{pos}-{next:D8}";
    }

    private static SaleDto BuildDto(Sale s) => new SaleDto(
        s.Id, s.Number, s.Date, s.DueDate, s.PeriodFrom, s.PeriodTo,
        s.ClientId, s.Client?.Code, s.ClientNameSnapshot, s.ClientAddressSnapshot,
        s.ClientCityLocationSnapshot, s.ClientCuitSnapshot,
        s.PaymentCondition, s.IvaCondition,
        s.Subtotal, s.Discount, s.Total, s.AmountInWords, s.Notes,
        s.IsCancelled, s.CancelledAt, s.WeekDays, s.IsPaid, s.CompanyNameSnapshot, s.CreatedAt, s.UpdatedAt,
        s.Items.OrderBy(i => i.Id).Select(i => new SaleItemDto(
            i.Id, i.ProductId, i.Code, i.Description,
            i.Quantity, i.UnitPrice, i.VatRate, i.BonifPercent, i.LineTotal
        )).ToList()
    );
}
