using System.Text.Json;
using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class SaleService
{
    private readonly AppDbContext _db;
    private readonly AuditLogService _audit;
    private readonly CustomerTierService _tiers;

    public SaleService(AppDbContext db, AuditLogService audit, CustomerTierService tiers)
    {
        _db = db;
        _audit = audit;
        _tiers = tiers;
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

        // Resolver tier de precios: el del cliente, o si no tiene, la lista default.
        var (tierId, tierAdjPct) = await ResolveTierWithPercentAsync(client?.CustomerTierId);

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
            decimal basePrice = unit;        // precio sin lista (snapshot)
            decimal itemTierAdj = 0m;        // % que aplico la lista a este item

            if (i.ProductId.HasValue)
            {
                var product = await _db.Products.FindAsync(i.ProductId.Value)
                    ?? throw new InvalidOperationException($"Producto {i.ProductId} no encontrado.");
                code ??= product.Sku;
                if (string.IsNullOrWhiteSpace(description)) description = product.DisplayName ?? product.Title;
                vat ??= product.VatRate;

                // Calcular el precio que la lista habria aplicado a este producto.
                var listPrice = Math.Round(product.RetailPrice * (1m + tierAdjPct / 100m), 2);

                if (unit <= 0)
                {
                    // Sin precio explicito: usar el de la lista (con override si existe).
                    unit = await _tiers.GetPriceForTierAsync(product.Id, tierId);
                    if (unit <= 0) unit = product.RetailPrice;
                    basePrice = product.RetailPrice;
                    itemTierAdj = tierAdjPct;
                }
                else if (Math.Abs(unit - listPrice) <= 0.01m)
                {
                    // El precio que mando el frontend coincide con el de la lista:
                    // registramos el descuento explicitamente.
                    basePrice = product.RetailPrice;
                    itemTierAdj = tierAdjPct;
                }
                else
                {
                    // El usuario lo modifico a mano. Tratarlo como "manual": sin descuento de lista.
                    basePrice = unit;
                    itemTierAdj = 0m;
                }
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
                LineTotal = lineTotal,
                BasePrice = basePrice,
                TierAdjustmentPercent = itemTierAdj
            });
            subtotal += lineTotal;
        }

        var discount = Math.Max(0m, request.Discount);
        var total = Math.Max(0m, subtotal - discount);

        // Tipo de comprobante: por defecto 'X' (cotizacion/remito interno).
        var comprobanteType = NormalizeComprobanteType(request.ComprobanteType);

        var sale = new Sale
        {
            Number = await GenerateNumberAsync(comprobanteType),
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
            ComprobanteType = comprobanteType,
            VendedorName = string.IsNullOrWhiteSpace(request.VendedorName) ? null : request.VendedorName.Trim(),
            CreatedAt = DateTime.UtcNow,
            Items = items
        };

        _db.Sales.Add(sale);
        await _db.SaveChangesAsync();

        // Descontar stock de los productos vendidos.
        await ApplyStockDiscountAsync(sale, "CreateSale");

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

    public async Task<SaleDto?> UpdateAsync(int id, UpdateSaleRequest request)
    {
        var sale = await _db.Sales.Include(s => s.Items).Include(s => s.Client).FirstOrDefaultAsync(s => s.Id == id);
        if (sale is null) return null;
        if (sale.IsCancelled) throw new InvalidOperationException("No se puede editar un comprobante anulado.");

        // Actualizar datos generales si vinieron
        if (request.Date.HasValue) sale.Date = request.Date.Value.Date;
        if (request.DueDate.HasValue) sale.DueDate = request.DueDate.Value.Date;
        if (request.PaymentCondition is not null) sale.PaymentCondition = request.PaymentCondition;
        if (request.IvaCondition is not null) sale.IvaCondition = request.IvaCondition;
        if (request.Notes is not null) sale.Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes;
        if (request.WeekDays is not null) sale.WeekDays = string.IsNullOrWhiteSpace(request.WeekDays) ? null : request.WeekDays;
        if (request.IsPaid.HasValue) sale.IsPaid = request.IsPaid.Value;
        if (request.CompanyNameOverride is not null)
            sale.CompanyNameSnapshot = string.IsNullOrWhiteSpace(request.CompanyNameOverride) ? null : request.CompanyNameOverride.Trim();
        if (request.VendedorName is not null)
            sale.VendedorName = string.IsNullOrWhiteSpace(request.VendedorName) ? null : request.VendedorName.Trim();

        // Cambiar cliente si vinieron datos
        if (request.ClientId.HasValue)
        {
            if (request.ClientId.Value == 0)
            {
                sale.ClientId = null;
                sale.ClientNameSnapshot = request.ClientNameOverride;
                sale.ClientAddressSnapshot = null;
                sale.ClientCuitSnapshot = null;
            }
            else
            {
                var client = await _db.Clients.FindAsync(request.ClientId.Value)
                    ?? throw new InvalidOperationException("Cliente no encontrado.");
                sale.ClientId = client.Id;
                sale.ClientNameSnapshot = request.ClientNameOverride ?? client.Name;
                sale.ClientAddressSnapshot = client.Address;
                sale.ClientCuitSnapshot = client.Cuit;
            }
        }

        // Reemplazar items si vinieron
        if (request.Items is not null)
        {
            if (request.Items.Count == 0)
                throw new InvalidOperationException("La venta tiene que tener al menos un item.");

            // Devolver al stock las cantidades de los items viejos antes de borrarlos,
            // para despues descontar las nuevas. Asi un cambio de cantidades queda prolijo.
            if (sale.StockDiscounted)
            {
                await ApplyStockRefundAsync(sale, "UpdateSale-RefundOld");
            }

            // Borrar items viejos
            _db.SaleItems.RemoveRange(sale.Items);
            sale.Items.Clear();

            // Tier del cliente actual de la venta + su % para snapshot por linea
            var clientTierId = sale.Client?.CustomerTierId
                ?? (sale.ClientId.HasValue ? (await _db.Clients.FindAsync(sale.ClientId.Value))?.CustomerTierId : null);
            var (tierIdForUpdate, tierAdjPctForUpdate) = await ResolveTierWithPercentAsync(clientTierId);

            decimal subtotal = 0m;
            foreach (var i in request.Items)
            {
                if (i.Quantity <= 0) throw new InvalidOperationException("La cantidad de cada item debe ser mayor a 0.");
                string? code = i.Code;
                string description = i.Description;
                decimal unit = i.UnitPrice;
                decimal? vat = i.VatRate;
                decimal basePrice = unit;
                decimal itemTierAdj = 0m;

                if (i.ProductId.HasValue)
                {
                    var product = await _db.Products.FindAsync(i.ProductId.Value)
                        ?? throw new InvalidOperationException($"Producto {i.ProductId} no encontrado.");
                    code ??= product.Sku;
                    if (string.IsNullOrWhiteSpace(description)) description = product.DisplayName ?? product.Title;
                    vat ??= product.VatRate;

                    var listPrice = Math.Round(product.RetailPrice * (1m + tierAdjPctForUpdate / 100m), 2);

                    if (unit <= 0)
                    {
                        unit = await _tiers.GetPriceForTierAsync(product.Id, tierIdForUpdate);
                        if (unit <= 0) unit = product.RetailPrice;
                        basePrice = product.RetailPrice;
                        itemTierAdj = tierAdjPctForUpdate;
                    }
                    else if (Math.Abs(unit - listPrice) <= 0.01m)
                    {
                        basePrice = product.RetailPrice;
                        itemTierAdj = tierAdjPctForUpdate;
                    }
                    else
                    {
                        basePrice = unit;
                        itemTierAdj = 0m;
                    }
                }

                var bruto = i.Quantity * unit;
                var bonif = bruto * (i.BonifPercent / 100m);
                var lineTotal = Math.Round(bruto - bonif, 2);

                sale.Items.Add(new SaleItem
                {
                    ProductId = i.ProductId, Code = code, Description = description,
                    Quantity = i.Quantity, UnitPrice = unit, VatRate = vat,
                    BonifPercent = i.BonifPercent, LineTotal = lineTotal,
                    BasePrice = basePrice, TierAdjustmentPercent = itemTierAdj
                });
                subtotal += lineTotal;
            }

            var discount = Math.Max(0m, request.Discount ?? sale.Discount);
            var total = Math.Max(0m, subtotal - discount);
            sale.Subtotal = Math.Round(subtotal, 2);
            sale.Discount = Math.Round(discount, 2);
            sale.Total = Math.Round(total, 2);
            sale.AmountInWords = NumberToWordsEs.AmountToPesos(total);
        }
        else if (request.Discount.HasValue)
        {
            // Solo cambio el descuento manteniendo items
            var discount = Math.Max(0m, request.Discount.Value);
            var total = Math.Max(0m, sale.Subtotal - discount);
            sale.Discount = Math.Round(discount, 2);
            sale.Total = Math.Round(total, 2);
            sale.AmountInWords = NumberToWordsEs.AmountToPesos(total);
        }

        sale.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Si la venta no esta anulada y se reemplazaron los items, descontar el nuevo stock.
        if (request.Items is not null && !sale.IsCancelled)
        {
            await ApplyStockDiscountAsync(sale, "UpdateSale-DiscountNew");
        }

        return await GetByIdAsync(sale.Id);
    }

    public async Task<DeleteSaleSettingsDto> GetDeleteSettingsAsync()
    {
        var keys = new[] { "sales.delete_allowed_operator", "sales.delete_password_hint" };
        var settings = await _db.AppSettings.Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value);
        return new DeleteSaleSettingsDto(
            settings.GetValueOrDefault("sales.delete_allowed_operator", "OSMAR"),
            settings.GetValueOrDefault("sales.delete_password_hint", "")
        );
    }

    public async Task<bool> DeleteAsync(int id, string operatorName, string password)
    {
        var allowedOp = (await _db.AppSettings.FindAsync("sales.delete_allowed_operator"))?.Value ?? "OSMAR";
        var expectedPassword = (await _db.AppSettings.FindAsync("sales.delete_password"))?.Value ?? "";

        if (!string.Equals(operatorName ?? "", allowedOp, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Solo {allowedOp} puede eliminar comprobantes.");
        if (string.IsNullOrEmpty(expectedPassword) || password != expectedPassword)
            throw new UnauthorizedAccessException("Clave incorrecta.");

        var sale = await _db.Sales.Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == id);
        if (sale is null) return false;

        // Si la venta tenia stock descontado, devolver al inventario antes de borrarla.
        if (sale.StockDiscounted)
        {
            await ApplyStockRefundAsync(sale, "DeleteSale");
        }

        _db.Sales.Remove(sale);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<SaleDto?> CancelAsync(int id, string? operatorName = null)
    {
        var sale = await _db.Sales.Include(s => s.Items).FirstOrDefaultAsync(s => s.Id == id);
        if (sale is null) return null;
        if (!sale.IsCancelled)
        {
            // Devolver el stock al inventario antes de marcar como anulada.
            if (sale.StockDiscounted)
            {
                await ApplyStockRefundAsync(sale, "CancelSale");
            }

            sale.IsCancelled = true;
            sale.CancelledAt = DateTime.UtcNow;
            sale.CancelledByOperator = string.IsNullOrWhiteSpace(operatorName) ? null : operatorName.Trim();
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

    /// <summary>
    /// Devuelve el id del tier que aplica a un cliente. Si el cliente no tiene
    /// uno asignado, usa el que esta marcado como default. Null si no hay listas.
    /// </summary>
    private async Task<int?> ResolveTierIdAsync(int? clientTierId)
    {
        if (clientTierId.HasValue) return clientTierId.Value;
        return await _db.CustomerTiers
            .Where(t => t.IsDefault)
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Resuelve el tier que aplica a un cliente y devuelve tambien su % de ajuste.
    /// Util para guardar el snapshot de descuento en cada item de venta.
    /// </summary>
    private async Task<(int? tierId, decimal adjustmentPercent)> ResolveTierWithPercentAsync(int? clientTierId)
    {
        var tierId = await ResolveTierIdAsync(clientTierId);
        if (!tierId.HasValue) return (null, 0m);
        var pct = await _db.CustomerTiers
            .Where(t => t.Id == tierId.Value)
            .Select(t => t.AdjustmentPercent)
            .FirstOrDefaultAsync();
        return (tierId, pct);
    }

    /// <summary>
    /// Descuenta del stock de cada producto las cantidades vendidas en la venta.
    /// Items sin ProductId (texto libre) se ignoran. Permite stock negativo.
    /// Marca Sale.StockDiscounted = true al terminar para evitar doble descuento.
    /// </summary>
    private async Task ApplyStockDiscountAsync(Sale sale, string source)
    {
        if (sale.StockDiscounted) return; // ya descontado, no hacer dos veces

        var items = sale.Items?.Where(i => i.ProductId.HasValue && i.Quantity > 0).ToList() ?? new();
        if (items.Count == 0)
        {
            sale.StockDiscounted = true;
            await _db.SaveChangesAsync();
            return;
        }

        var productIds = items.Select(i => i.ProductId!.Value).Distinct().ToList();
        // Incluir BaseProduct para resolver el caso "hijo de padre kg-mode"
        var products = await _db.Products
            .Include(p => p.BaseProduct)
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        // Sumar cantidades por producto (por si el mismo producto aparece varias veces)
        var totalsByProduct = items
            .GroupBy(i => i.ProductId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var detalles = new List<object>();
        foreach (var (productId, qty) in totalsByProduct)
        {
            if (!products.TryGetValue(productId, out var product)) continue;

            // Determinar entidad a descontar y cantidad real:
            //  - Hijo de padre kg-mode: el descuento va al PADRE en kg = qty * Fraction
            //  - Caso normal: descuento al propio producto en unidades = qty
            Product target = product;
            decimal qtyToDeduct = qty;
            string mode = "unidad";
            if (product.BaseProductId.HasValue && product.BaseProduct?.StockUnit == "kg" && product.Fraction > 0)
            {
                target = product.BaseProduct;
                qtyToDeduct = qty * product.Fraction;
                mode = "kg-padre";
            }

            var oldStock = target.Stock;
            target.Stock = target.Stock - qtyToDeduct; // permite negativo a proposito
            target.UpdatedAt = DateTime.UtcNow;
            detalles.Add(new
            {
                productId = product.Id,
                sku = product.Sku,
                title = product.Title,
                modo = mode,
                targetSku = target.Sku,
                cantidadVendida = qty,
                cantidadDescontadaDelTarget = qtyToDeduct,
                stockAnterior = oldStock,
                stockNuevo = target.Stock,
                quedoNegativo = target.Stock < 0
            });
        }

        sale.StockDiscounted = true;
        sale.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            "Sale", sale.Id.ToString(), "STOCK_DISCOUNT",
            JsonSerializer.Serialize(new { source, ventaNumero = sale.Number, items = detalles }),
            null);
    }

    /// <summary>
    /// Devuelve al stock de cada producto las cantidades de la venta.
    /// Marca Sale.StockDiscounted = false al terminar.
    /// </summary>
    private async Task ApplyStockRefundAsync(Sale sale, string source)
    {
        if (!sale.StockDiscounted) return; // nada que devolver

        var items = sale.Items?.Where(i => i.ProductId.HasValue && i.Quantity > 0).ToList() ?? new();
        if (items.Count == 0)
        {
            sale.StockDiscounted = false;
            await _db.SaveChangesAsync();
            return;
        }

        var productIds = items.Select(i => i.ProductId!.Value).Distinct().ToList();
        var products = await _db.Products
            .Include(p => p.BaseProduct)
            .Where(p => productIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id);

        var totalsByProduct = items
            .GroupBy(i => i.ProductId!.Value)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var detalles = new List<object>();
        foreach (var (productId, qty) in totalsByProduct)
        {
            if (!products.TryGetValue(productId, out var product)) continue;

            // Mismo criterio que en el descuento: si es hijo de padre kg-mode, el reembolso
            // va al padre = qty * Fraction
            Product target = product;
            decimal qtyToAdd = qty;
            if (product.BaseProductId.HasValue && product.BaseProduct?.StockUnit == "kg" && product.Fraction > 0)
            {
                target = product.BaseProduct;
                qtyToAdd = qty * product.Fraction;
            }

            var oldStock = target.Stock;
            target.Stock = target.Stock + qtyToAdd;
            target.UpdatedAt = DateTime.UtcNow;
            detalles.Add(new
            {
                productId = product.Id,
                sku = product.Sku,
                title = product.Title,
                targetSku = target.Sku,
                cantidadVendida = qty,
                cantidadDevueltaAlTarget = qtyToAdd,
                stockAnterior = oldStock,
                stockNuevo = target.Stock
            });
        }

        sale.StockDiscounted = false;
        sale.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        await _audit.LogAsync(
            "Sale", sale.Id.ToString(), "STOCK_REFUND",
            JsonSerializer.Serialize(new { source, ventaNumero = sale.Number, items = detalles }),
            null);
    }

    /// <summary>
    /// Genera el numero del comprobante. Cada tipo (X, FACTURA_A, etc.) tiene su
    /// propio Punto de Venta y por lo tanto su propia numeracion correlativa.
    /// Convencion: AppSettings 'sales.pv.{TIPO}' tiene el codigo de PV (ej '0009').
    /// Si no existe, cae al PV global 'sales.point_of_sale'.
    /// </summary>
    private async Task<string> GenerateNumberAsync(string comprobanteType)
    {
        var pos =
            (await _db.AppSettings.FindAsync($"sales.pv.{comprobanteType}"))?.Value
            ?? (await _db.AppSettings.FindAsync("sales.point_of_sale"))?.Value
            ?? "0001";

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

    /// <summary>Normaliza el tipo de comprobante. Default 'X' si viene vacio o invalido.</summary>
    private static readonly string[] ValidComprobanteTypes = new[] { "X", "FACTURA_A", "FACTURA_B", "FACTURA_C" };
    private static string NormalizeComprobanteType(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "X";
        var t = type.Trim().ToUpperInvariant();
        return ValidComprobanteTypes.Contains(t) ? t : "X";
    }

    private static SaleDto BuildDto(Sale s) => new SaleDto(
        s.Id, s.Number, s.Date, s.DueDate, s.PeriodFrom, s.PeriodTo,
        s.ClientId, s.Client?.Code, s.ClientNameSnapshot, s.ClientAddressSnapshot,
        s.ClientCityLocationSnapshot, s.ClientCuitSnapshot,
        s.PaymentCondition, s.IvaCondition,
        s.Subtotal, s.Discount, s.Total, s.AmountInWords, s.Notes,
        s.IsCancelled, s.CancelledAt, s.CancelledByOperator, s.WeekDays, s.IsPaid, s.CompanyNameSnapshot, s.CreatedAt, s.UpdatedAt,
        s.Items.OrderBy(i => i.Id).Select(i => new SaleItemDto(
            i.Id, i.ProductId, i.Code, i.Description,
            i.Quantity, i.UnitPrice, i.VatRate, i.BonifPercent, i.LineTotal,
            // Si BasePrice quedo en 0 (item viejo previo a la migracion), caer al UnitPrice.
            i.BasePrice > 0 ? i.BasePrice : i.UnitPrice,
            i.TierAdjustmentPercent
        )).ToList(),
        string.IsNullOrEmpty(s.ComprobanteType) ? "X" : s.ComprobanteType,
        s.VendedorName
    );
}
