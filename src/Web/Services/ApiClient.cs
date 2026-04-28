using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Web.Models;

namespace Web.Services;

public class ApiClient
{
    private readonly HttpClient _http;
    private readonly AuthService _authService;
    private readonly NavigationManager _navigation;
    private readonly OperatorService _operator;

    public ApiClient(HttpClient http, AuthService authService, NavigationManager navigation, OperatorService op)
    {
        _http = http;
        _authService = authService;
        _navigation = navigation;
        _operator = op;
    }

    private void EnsureOperatorHeader()
    {
        // Setea/actualiza el header X-Operator-Name con el operador actual.
        _http.DefaultRequestHeaders.Remove("X-Operator-Name");
        if (!string.IsNullOrWhiteSpace(_operator.Current))
            _http.DefaultRequestHeaders.Add("X-Operator-Name", _operator.Current);
    }

    public async Task<DashboardStats?> GetDashboardStatsAsync()
    {
        return await GetAsync<DashboardStats>("/api/dashboard/stats");
    }

    public async Task<SystemInfoDto?> GetSystemInfoAsync()
    {
        return await GetAsync<SystemInfoDto>("/api/system/info");
    }

    public async Task<HostInfoDto?> GetHostInfoAsync()
    {
        return await GetAsync<HostInfoDto>("/api/system/host-info");
    }

    public async Task<UserDto?> GetMeAsync()
    {
        return await GetAsync<UserDto>("/api/auth/me");
    }

    // --- Users ---
    public async Task<List<UserManageDto>?> GetUsersAsync()
    {
        return await GetAsync<List<UserManageDto>>("/api/users");
    }

    public async Task<UserManageDto?> CreateUserAsync(CreateUserRequest request)
    {
        return await PostAsync<UserManageDto>("/api/users", request);
    }

    public async Task<UserManageDto?> UpdateUserAsync(int id, UpdateUserRequest request)
    {
        return await PutAsync<UserManageDto>($"/api/users/{id}", request);
    }

    public async Task<bool> DeleteUserAsync(int id)
    {
        return await DeleteAsync($"/api/users/{id}");
    }

    // --- Roles ---
    public async Task<List<RoleDto>?> GetRolesAsync()
    {
        return await GetAsync<List<RoleDto>>("/api/roles");
    }

    public async Task<RoleDto?> CreateRoleAsync(CreateRoleRequest request)
    {
        return await PostAsync<RoleDto>("/api/roles", request);
    }

    public async Task<RoleDto?> UpdateRoleAsync(int id, UpdateRoleRequest request)
    {
        return await PutAsync<RoleDto>($"/api/roles/{id}", request);
    }

    public async Task<bool> DeleteRoleAsync(int id)
    {
        return await DeleteAsync($"/api/roles/{id}");
    }

    public async Task<List<MenuTreeDto>?> GetMenuTreeAsync()
    {
        return await GetAsync<List<MenuTreeDto>>("/api/roles/menu-tree");
    }

    // --- Profile ---
    public async Task<ProfileDto?> GetProfileAsync()
    {
        return await GetAsync<ProfileDto>("/api/auth/profile");
    }

    public async Task<ProfileDto?> UpdateProfileAsync(UpdateProfileRequest request)
    {
        return await PutAsync<ProfileDto>("/api/auth/profile", request);
    }

    public async Task<bool> ChangePasswordAsync(ChangePasswordRequest request)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PutAsJsonAsync("/api/auth/password", request);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _authService.LogoutAsync();
            _navigation.NavigateTo("/login", forceLoad: true);
            return false;
        }

        return response.IsSuccessStatusCode;
    }

    // --- MeLi Item Details (pictures + description) ---
    public async Task<MeliItemDetailsDto?> GetMeliItemDetailsAsync(string meliItemId)
    {
        return await GetAsync<MeliItemDetailsDto>($"/api/meli/items/{meliItemId}/details");
    }

        // --- Item-Product Linking ---
    public async Task<MeliItemDto?> LinkItemToProductAsync(string meliItemId, int productId)
    {
        return await PutAsync<MeliItemDto>($"/api/meli/items/{meliItemId}/link", new { productId });
    }

    public async Task<MeliItemDto?> UnlinkItemProductAsync(string meliItemId)
    {
        await SetAuthHeaderAsync();
        var response = await _http.DeleteAsync($"/api/meli/items/{meliItemId}/link");
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<MeliItemDto>();
    }

    // --- Products ---
    public async Task<List<ProductDto>?> GetProductsAsync()
    {
        return await GetAsync<List<ProductDto>>("/api/products");
    }

    public async Task<ProductDto?> CreateProductAsync(CreateProductRequest request)
    {
        return await PostAsync<ProductDto>("/api/products", request);
    }

    public async Task<ProductDto?> UpdateProductAsync(int id, UpdateProductRequest request)
    {
        return await PutAsync<ProductDto>($"/api/products/{id}", request);
    }

    public async Task<int> BulkDeleteProductsAsync(List<int> ids)
    {
        var result = await PostAsync<Dictionary<string, int>>("/api/products/bulk-delete", new { ids });
        return result?.GetValueOrDefault("deleted") ?? 0;
    }

    public async Task<int> BulkToggleProductStatusAsync(List<int> ids, bool isActive)
    {
        var result = await PutAsync<Dictionary<string, int>>("/api/products/bulk-toggle-status", new { ids, isActive });
        return result?.GetValueOrDefault("updated") ?? 0;
    }

    // --- Suppliers ---
    public async Task<List<SupplierDto>?> GetSuppliersAsync()
        => await GetAsync<List<SupplierDto>>("/api/suppliers");

    public async Task<SupplierDto?> CreateSupplierAsync(CreateSupplierRequest request)
        => await PostAsync<SupplierDto>("/api/suppliers", request);

    public async Task<SupplierDto?> UpdateSupplierAsync(int id, UpdateSupplierRequest request)
        => await PutAsync<SupplierDto>($"/api/suppliers/{id}", request);

    public async Task<bool> DeleteSupplierAsync(int id)
        => await DeleteAsync($"/api/suppliers/{id}");

    // --- Brands ---
    public async Task<List<BrandDto>?> GetBrandsAsync()
        => await GetAsync<List<BrandDto>>("/api/brands");

    public async Task<BrandDto?> CreateBrandAsync(CreateBrandRequest request)
        => await PostAsync<BrandDto>("/api/brands", request);

    public async Task<BrandDto?> UpdateBrandAsync(int id, UpdateBrandRequest request)
        => await PutAsync<BrandDto>($"/api/brands/{id}", request);

    public async Task<bool> DeleteBrandAsync(int id)
        => await DeleteAsync($"/api/brands/{id}");

    // --- Clients ---
    public async Task<List<ClientDto>?> GetClientsAsync()
        => await GetAsync<List<ClientDto>>("/api/clients");

    public async Task<ClientDto?> CreateClientAsync(CreateClientRequest request)
        => await PostAsync<ClientDto>("/api/clients", request);

    public async Task<ClientDto?> UpdateClientAsync(int id, UpdateClientRequest request)
        => await PutAsync<ClientDto>($"/api/clients/{id}", request);

    public async Task<bool> DeleteClientAsync(int id)
        => await DeleteAsync($"/api/clients/{id}");

    // --- Sales (ventas / comprobantes) ---
    public async Task<List<SaleDto>?> GetSalesAsync()
        => await GetAsync<List<SaleDto>>("/api/sales");

    public async Task<SaleDto?> GetSaleAsync(int id)
        => await GetAsync<SaleDto>($"/api/sales/{id}");

    public async Task<SaleDto?> CreateSaleAsync(CreateSaleRequest request)
        => await PostAsync<SaleDto>("/api/sales", request);

    public async Task<SaleDto?> UpdateSaleAsync(int id, UpdateSaleRequest request)
        => await PutAsync<SaleDto>($"/api/sales/{id}", request);

    public async Task<SaleDto?> CancelSaleAsync(int id)
        => await PostAsync<SaleDto>($"/api/sales/{id}/cancel", new { });

    public async Task<DeleteSaleSettingsDto?> GetSaleDeleteSettingsAsync()
        => await GetAsync<DeleteSaleSettingsDto>("/api/sales/delete-settings");

    public async Task<bool> DeleteSaleAsync(int id, string password)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync($"/api/sales/{id}/delete", new { password });
        if (response.IsSuccessStatusCode) return true;
        await ThrowIfErrorAsync(response);
        return false;
    }

    // ===== Empresas (display names editables) =====
    public async Task<List<CompanyNameDto>?> GetCompanyNamesAsync()
        => await GetAsync<List<CompanyNameDto>>("/api/companies/names");

    public async Task<List<CompanyNameDto>?> UpdateCompanyNamesAsync(string password, Dictionary<string, string> names)
        => await PostAsync<List<CompanyNameDto>>("/api/companies/names", new { password, names });

    public async Task<SaleDto?> UpdateSaleFlagsAsync(int id, UpdateSaleFlagsRequest request)
    {
        var response = await _http.PatchAsJsonAsync($"/api/sales/{id}/flags", request);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<SaleDto>();
        return null;
    }

    public async Task<CompanyInfoDto?> GetCompanyInfoAsync()
        => await GetAsync<CompanyInfoDto>("/api/sales/company-info");

    public async Task<CompanyInfoDto?> UpdateCompanyInfoAsync(CompanyInfoDto info)
        => await PutAsync<CompanyInfoDto>("/api/sales/company-info", info);

    // --- Treasury (cuentas + movimientos) ---
    public async Task<List<TreasuryAccountDto>?> GetTreasuryAccountsAsync()
        => await GetAsync<List<TreasuryAccountDto>>("/api/treasury/accounts");
    public async Task<TreasuryAccountDto?> CreateTreasuryAccountAsync(CreateTreasuryAccountRequest r)
        => await PostAsync<TreasuryAccountDto>("/api/treasury/accounts", r);
    public async Task<TreasuryAccountDto?> UpdateTreasuryAccountAsync(int id, UpdateTreasuryAccountRequest r)
        => await PutAsync<TreasuryAccountDto>($"/api/treasury/accounts/{id}", r);
    public async Task<bool> DeleteTreasuryAccountAsync(int id)
        => await DeleteAsync($"/api/treasury/accounts/{id}");
    public async Task<List<TreasuryMovementDto>?> GetTreasuryMovementsAsync(int? accountId = null)
    {
        var url = "/api/treasury/movements";
        if (accountId.HasValue) url += $"?accountId={accountId.Value}";
        return await GetAsync<List<TreasuryMovementDto>>(url);
    }
    public async Task<List<TreasuryMovementDto>?> CreateTreasuryMovementAsync(CreateTreasuryMovementRequest r)
        => await PostAsync<List<TreasuryMovementDto>>("/api/treasury/movements", r);
    public async Task<bool> DeleteTreasuryMovementAsync(int id)
        => await DeleteAsync($"/api/treasury/movements/{id}");

    // --- Empleados ---
    public async Task<List<EmployeeDto>?> GetEmployeesAsync()
        => await GetAsync<List<EmployeeDto>>("/api/employees");
    public async Task<EmployeeDto?> CreateEmployeeAsync(CreateEmployeeRequest r)
        => await PostAsync<EmployeeDto>("/api/employees", r);
    public async Task<EmployeeDto?> UpdateEmployeeAsync(int id, UpdateEmployeeRequest r)
        => await PutAsync<EmployeeDto>($"/api/employees/{id}", r);
    public async Task<bool> DeleteEmployeeAsync(int id)
        => await DeleteAsync($"/api/employees/{id}");

    // --- Liquidaciones de sueldo ---
    public async Task<List<PayrollDto>?> GetPayrollsAsync(int? employeeId = null, int? year = null, int? month = null)
    {
        var qs = new List<string>();
        if (employeeId.HasValue) qs.Add($"employeeId={employeeId.Value}");
        if (year.HasValue) qs.Add($"year={year.Value}");
        if (month.HasValue) qs.Add($"month={month.Value}");
        var url = "/api/payrolls" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<PayrollDto>>(url);
    }
    public async Task<PayrollDto?> CreatePayrollAsync(CreatePayrollRequest r)
        => await PostAsync<PayrollDto>("/api/payrolls", r);
    public async Task<PayrollDto?> UpdatePayrollAsync(int id, UpdatePayrollRequest r)
        => await PutAsync<PayrollDto>($"/api/payrolls/{id}", r);
    public async Task<bool> DeletePayrollAsync(int id)
        => await DeleteAsync($"/api/payrolls/{id}");
    public async Task<int> GeneratePayrollMonthAsync(GeneratePayrollMonthRequest r)
    {
        var result = await PostAsync<Dictionary<string, int>>("/api/payrolls/generate-month", r);
        return result?.GetValueOrDefault("created") ?? 0;
    }
    public async Task<PayrollDto?> AddPayrollPaymentAsync(int payrollId, AddPayrollPaymentRequest r)
        => await PostAsync<PayrollDto>($"/api/payrolls/{payrollId}/payments", r);
    public async Task<PayrollDto?> DeletePayrollPaymentAsync(int paymentId)
        => await DeleteWithBodyAsync<PayrollDto>($"/api/payrolls/payments/{paymentId}");

    // --- Lookup AFIP ---
    public async Task<FiscalLookupDto?> LookupCuitAsync(string cuit)
        => await GetAsync<FiscalLookupDto>($"/api/fiscal/lookup?cuit={Uri.EscapeDataString(cuit)}");

    // --- Cotizaciones ---
    public async Task<DolarBnaDto?> GetDolarBnaAsync()
        => await GetAsync<DolarBnaDto>("/api/quotes/dolar-bna");

    // --- Listas de precios de proveedores ---
    public async Task<List<SupplierPriceListDto>?> GetPriceListsAsync()
        => await GetAsync<List<SupplierPriceListDto>>("/api/price-lists");
    public async Task<SupplierPriceListDto?> CreatePriceListAsync(CreateSupplierPriceListRequest r)
        => await PostAsync<SupplierPriceListDto>("/api/price-lists", r);
    public async Task<SupplierPriceListDto?> UpdatePriceListAsync(int id, UpdateSupplierPriceListRequest r)
        => await PutAsync<SupplierPriceListDto>($"/api/price-lists/{id}", r);
    public async Task<bool> DeletePriceListAsync(int id)
        => await DeleteAsync($"/api/price-lists/{id}");
    public async Task<List<SupplierPriceListItemDto>?> GetPriceListItemsAsync(int listId, string? search = null)
    {
        var url = $"/api/price-lists/{listId}/items";
        if (!string.IsNullOrEmpty(search)) url += $"?search={Uri.EscapeDataString(search)}";
        return await GetAsync<List<SupplierPriceListItemDto>>(url);
    }
    public async Task<SupplierPriceListItemDto?> AddPriceListItemAsync(int listId, CreatePriceListItemRequest r)
        => await PostAsync<SupplierPriceListItemDto>($"/api/price-lists/{listId}/items", r);
    public async Task<SupplierPriceListItemDto?> UpdatePriceListItemAsync(int itemId, UpdatePriceListItemRequest r)
        => await PutAsync<SupplierPriceListItemDto>($"/api/price-lists/items/{itemId}", r);
    public async Task<bool> DeletePriceListItemAsync(int itemId)
        => await DeleteAsync($"/api/price-lists/items/{itemId}");

    // --- Stock batches (lotes con vencimiento) ---
    public async Task<List<StockBatchDto>?> GetStockBatchesAsync(int productId)
        => await GetAsync<List<StockBatchDto>>($"/api/products/{productId}/stock-batches");

    public async Task<StockBatchDto?> CreateStockBatchAsync(int productId, CreateStockBatchRequest request)
        => await PostAsync<StockBatchDto>($"/api/products/{productId}/stock-batches", request);

    public async Task<StockBatchDto?> UpdateStockBatchAsync(int batchId, UpdateStockBatchRequest request)
        => await PutAsync<StockBatchDto>($"/api/stock-batches/{batchId}", request);

    public async Task<bool> DeleteStockBatchAsync(int batchId)
        => await DeleteAsync($"/api/stock-batches/{batchId}");

    // --- Combos ---
    public async Task<List<ComboDto>?> GetCombosAsync()
        => await GetAsync<List<ComboDto>>("/api/combos");

    public async Task<string?> GetNextComboSkuAsync()
    {
        var res = await GetAsync<Dictionary<string, string>>("/api/combos/next-sku");
        return res?.GetValueOrDefault("sku");
    }

    public async Task<ComboDto?> CreateComboAsync(CreateComboRequest request)
        => await PostAsync<ComboDto>("/api/combos", request);

    public async Task<ComboDto?> UpdateComboAsync(int id, UpdateComboRequest request)
        => await PutAsync<ComboDto>($"/api/combos/{id}", request);

    public async Task<bool> DeleteComboAsync(int id)
        => await DeleteAsync($"/api/combos/{id}");

    public async Task<bool> DeleteProductAsync(int id)
    {
        return await DeleteAsync($"/api/products/{id}");
    }

    // --- Integrations ---
    public async Task<List<IntegrationDto>?> GetIntegrationsAsync()
    {
        return await GetAsync<List<IntegrationDto>>("/api/integrations");
    }

    public async Task<IntegrationDto?> GetIntegrationAsync(string provider)
    {
        return await GetAsync<IntegrationDto>($"/api/integrations/{provider}");
    }

    public async Task<IntegrationDto?> SaveIntegrationAsync(SaveIntegrationRequest request)
    {
        return await PostAsync<IntegrationDto>("/api/integrations", request);
    }

    public async Task<bool> DeleteIntegrationAsync(string provider)
    {
        return await DeleteAsync($"/api/integrations/{provider}");
    }

    public async Task<string?> TestEmailAsync()
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsync("/api/integrations/email-smtp/test", null);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            await _authService.LogoutAsync();
            _navigation.NavigateTo("/login", forceLoad: true);
            return null;
        }

        var body = await response.Content.ReadAsStringAsync();
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var msg))
                return msg.GetString();
            if (doc.RootElement.TryGetProperty("error", out var err))
                throw new Exception(err.GetString());
        }
        catch (System.Text.Json.JsonException) { }

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Error del servidor ({response.StatusCode})");

        return body;
    }

    // --- MercadoLibre Accounts ---
    public async Task<List<MeliAccountDto>?> GetMeliAccountsAsync()
    {
        return await GetAsync<List<MeliAccountDto>>("/api/meli/accounts");
    }

    public async Task<MeliAuthUrlResponse?> GetMeliAuthUrlAsync()
    {
        return await GetAsync<MeliAuthUrlResponse>("/api/meli/auth-url");
    }

    public async Task<MeliAccountDto?> MeliCallbackAsync(string code)
    {
        return await PostAsync<MeliAccountDto>("/api/meli/callback", new MeliCallbackRequest { Code = code });
    }


    public async Task<string?> CreateMeliTestUserAsync(string siteId)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/meli/test-user", new { siteId });
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return null;
        }
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err))
                    throw new Exception(err.GetString());
            }
            catch (System.Text.Json.JsonException) { }
            throw new Exception(body);
        }
        return body;
    }

    public async Task<MeliAccountStatsDto?> GetMeliAccountStatsAsync(int id)
    {
        return await GetAsync<MeliAccountStatsDto>($"/api/meli/accounts/{id}/stats");
    }

    public async Task<bool> DeleteMeliAccountAsync(int id)
    {
        return await DeleteAsync($"/api/meli/accounts/{id}");
    }

    // --- MercadoLibre Orders ---
    public async Task<MeliOrdersResponse?> GetMeliOrdersAsync(DateTime from, DateTime to, int? accountId = null)
    {
        var url = $"/api/meli/orders?from={from:yyyy-MM-ddTHH:mm:ss}&to={to:yyyy-MM-ddTHH:mm:ss}";
        if (accountId.HasValue)
            url += $"&accountId={accountId.Value}";
        return await GetAsync<MeliOrdersResponse>(url);
    }

    public async Task<MeliOrderSyncResult?> SyncMeliOrdersAsync(DateTime from, DateTime to)
    {
        var url = $"/api/meli/orders/sync?from={from:yyyy-MM-ddTHH:mm:ss}&to={to:yyyy-MM-ddTHH:mm:ss}";
        return await PostAsync<MeliOrderSyncResult>(url, new { });
    }

    // --- MercadoLibre Items ---
    public async Task<MeliItemsResponse?> GetMeliItemsAsync(int? accountId = null, string? status = null)
    {
        var url = "/api/meli/items";
        var queryParams = new List<string>();
        if (accountId.HasValue)
            queryParams.Add($"accountId={accountId.Value}");
        if (!string.IsNullOrEmpty(status))
            queryParams.Add($"status={status}");
        if (queryParams.Any())
            url += "?" + string.Join("&", queryParams);
        return await GetAsync<MeliItemsResponse>(url);
    }

    public async Task<MeliItemSyncResult?> SyncMeliItemsAsync(string? status = null, int? accountId = null)
    {
        var url = "/api/meli/items/sync";
        var queryParams = new List<string>();
        if (!string.IsNullOrEmpty(status)) queryParams.Add($"status={status}");
        if (accountId.HasValue) queryParams.Add($"accountId={accountId.Value}");
        if (queryParams.Any()) url += "?" + string.Join("&", queryParams);
        return await PostAsync<MeliItemSyncResult>(url, new { });
    }

    public async Task<(MeliItemSyncByIdBatchResult? Result, string? Error)> SyncMeliItemByIdAsync(string meliItemId)
    {
        var response = await _http.PostAsJsonAsync("/api/meli/items/sync-by-id", new { meliItemId });
        if (response.IsSuccessStatusCode)
        {
            var result = await response.Content.ReadFromJsonAsync<MeliItemSyncByIdBatchResult>();
            return (result, null);
        }
        try
        {
            var err = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            return (null, err is not null && err.TryGetValue("error", out var msg) ? msg : "Error al traer las publicaciones.");
        }
        catch
        {
            return (null, "Error al traer las publicaciones.");
        }
    }

    public async Task<SyncProgressResponse?> GetSyncProgressAsync(string? id = null)
    {
        var url = "/api/meli/items/sync/progress";
        if (!string.IsNullOrEmpty(id)) url += $"?id={id}";
        return await GetAsync<SyncProgressResponse>(url);
    }

    public async Task<List<ItemPromotionDto>?> GetItemPromotionsAsync(string meliItemId)
    {
        return await GetAsync<List<ItemPromotionDto>>($"/api/meli/items/{meliItemId}/promotions");
    }

    public async Task<ListingCostDto?> GetItemCostsAsync(string meliItemId)
    {
        try
        {
            return await _http.GetFromJsonAsync<ListingCostDto>($"api/meli/items/{meliItemId}/costs");
        }
        catch
        {
            return null;
        }
    }

    public async Task<MeliItemDto?> UpdateMeliItemAsync(string meliItemId, UpdateMeliItemRequest request)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PutAsJsonAsync($"/api/meli/items/{meliItemId}", request);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _authService.LogoutAsync();
            _navigation.NavigateTo("/login", forceLoad: true);
            return default;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(errorBody);
                if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    throw new Exception(errorProp.GetString());
            }
            catch (System.Text.Json.JsonException) { }
            throw new Exception($"Error del servidor ({response.StatusCode})");
        }

        return await response.Content.ReadFromJsonAsync<MeliItemDto>();
    }

    public async Task<List<OpenAiModelDto>> GetOpenAiModelsAsync()
    {
        await SetAuthHeaderAsync();
        var response = await _http.GetAsync("/api/integrations/openai/models");

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _authService.LogoutAsync();
            _navigation.NavigateTo("/login", forceLoad: true);
            return new();
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(errorBody);
                if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    throw new Exception(errorProp.GetString());
            }
            catch (System.Text.Json.JsonException) { }
            throw new Exception($"Error al obtener modelos ({response.StatusCode})");
        }

        return await response.Content.ReadFromJsonAsync<List<OpenAiModelDto>>() ?? new();
    }

    public async Task<List<ClaudeModelDto>> GetClaudeModelsAsync()
    {
        await SetAuthHeaderAsync();
        var response = await _http.GetAsync("/api/integrations/claude/models");

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _authService.LogoutAsync();
            _navigation.NavigateTo("/login", forceLoad: true);
            return new();
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(errorBody);
                if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    throw new Exception(errorProp.GetString());
            }
            catch (System.Text.Json.JsonException) { }
            throw new Exception($"Error al obtener modelos ({response.StatusCode})");
        }

        return await response.Content.ReadFromJsonAsync<List<ClaudeModelDto>>() ?? new();
    }

    public async Task<int> DeleteMeliItemsBulkAsync(List<int> ids)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/meli/items/bulk-delete", new { ids });

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _authService.LogoutAsync();
            _navigation.NavigateTo("/login", forceLoad: true);
            return 0;
        }

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Error al eliminar publicaciones ({response.StatusCode})");

        var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        return result.GetProperty("deleted").GetInt32();
    }

    public async Task<BulkCreateProductResult?> CreateProductFromItemAsync(int itemId)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsync("/api/meli/items/" + itemId + "/create-product", null);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _authService.LogoutAsync();
            _navigation.NavigateTo("/login", forceLoad: true);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync();
            throw new Exception(errorText);
        }

        return await response.Content.ReadFromJsonAsync<BulkCreateProductResult>();
    }

        public async Task<BulkCreateProductResult?> BulkCreateProductsAsync(List<int> ids)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/meli/items/bulk-create-products", new { ids });

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _authService.LogoutAsync();
            _navigation.NavigateTo("/login", forceLoad: true);
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await response.Content.ReadAsStringAsync();
            throw new Exception($"Error al crear productos ({response.StatusCode}): {errorText}");
        }

        return await response.Content.ReadFromJsonAsync<BulkCreateProductResult>();
    }


        // --- Audit Logs ---
    public async Task<AuditLogListResponse?> GetAuditLogsAsync(DateTime from, DateTime to, string? entityType = null, int page = 1)
    {
        var url = $"/api/audit-logs?from={from:yyyy-MM-ddTHH:mm:ss}&to={to:yyyy-MM-ddTHH:mm:ss}&page={page}";
        if (!string.IsNullOrEmpty(entityType))
            url += $"&entityType={entityType}";
        return await GetAsync<AuditLogListResponse>(url);
    }

    // --- MercadoLibre Order Detail ---
    public async Task<MeliOrderDetailResponse?> GetMeliOrderDetailAsync(long meliOrderId)
    {
        return await GetAsync<MeliOrderDetailResponse>($"/api/meli/orders/detail/{meliOrderId}");
    }

    public async Task<MeliOrderDetailResponse?> GetMeliPackDetailAsync(long packId)
    {
        return await GetAsync<MeliOrderDetailResponse>($"/api/meli/orders/pack-detail/{packId}");
    }

    // --- Scheduled Processes ---
    public async Task<List<ScheduledProcessDto>?> GetScheduledProcessesAsync()
    {
        return await GetAsync<List<ScheduledProcessDto>>("/api/scheduled-processes");
    }

    public async Task<ScheduledProcessDto?> UpdateProcessScheduleAsync(string code, UpdateScheduleRequest request)
    {
        return await PutAsync<ScheduledProcessDto>($"/api/scheduled-processes/{code}/schedule", request);
    }

    public async Task<RunProcessResponse?> RunProcessNowAsync(string code)
    {
        return await PostAsync<RunProcessResponse>($"/api/scheduled-processes/{code}/run", new { });
    }

    public async Task<ProcessLogListResponse?> GetProcessLogsAsync(string? code = null, int page = 1)
    {
        var url = code != null
            ? $"/api/scheduled-processes/{code}/logs?page={page}"
            : $"/api/scheduled-processes/logs?page={page}";
        return await GetAsync<ProcessLogListResponse>(url);
    }

    // --- MeLi Publish ---
    public async Task<List<CategoryPredictionDto>?> PredictCategoryAsync(string title, int accountId)
    {
        return await PostAsync<List<CategoryPredictionDto>>($"/api/meli/publish/predict-category?accountId={accountId}", new { title });
    }

    public async Task<List<CategoryAttributeDto>?> GetCategoryAttributesAsync(string categoryId)
    {
        return await GetAsync<List<CategoryAttributeDto>>($"/api/meli/publish/category-attributes/{categoryId}");
    }

    public async Task<List<SuggestedAttributeDto>?> SuggestAttributesAsync(object request)
    {
        return await PostAsync<List<SuggestedAttributeDto>>("/api/meli/publish/suggest-attributes", request);
    }

    public async Task<PublishItemResponse?> PublishItemAsync(PublishItemRequest request)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/meli/publish", request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _authService.LogoutAsync();
            _navigation.NavigateTo("/login", forceLoad: true);
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(errorBody);
                if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    return new PublishItemResponse { Error = errorProp.GetString() };
            }
            catch (System.Text.Json.JsonException) { }
            return new PublishItemResponse { Error = $"Error del servidor ({response.StatusCode})" };
        }
        return await response.Content.ReadFromJsonAsync<PublishItemResponse>();
    }

    // --- Settings ---
    public async Task<Dictionary<string, string>?> GetSettingsAsync()
    {
        return await GetAsync<Dictionary<string, string>>("/api/settings");
    }

    public async Task<bool> UpdateSettingAsync(string key, string value)
    {
        var result = await PutAsync<object>($"/api/settings/{key}", new { Value = value });
        return result != null;
    }

    public async Task<string?> UploadLogoAsync(byte[] fileBytes, string fileName, string contentType)
    {
        try
        {
            await SetAuthHeaderAsync();
            using var content = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(fileBytes);
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            content.Add(fileContent, "file", fileName);
            var response = await _http.PostAsync("/api/settings/logo", content);
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
                return result?.GetValueOrDefault("value");
            }
            return null;
        }
        catch { return null; }
    }

    public async Task<bool> DeleteLogoAsync()
    {
        try
        {
            await SetAuthHeaderAsync();
            var response = await _http.DeleteAsync("/api/settings/logo");
            return response.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // --- WhatsApp ---
    public async Task<WhatsAppStatusDto?> GetWhatsAppStatusAsync()
    {
        return await GetAsync<WhatsAppStatusDto>("/api/whatsapp/status");
    }

    public async Task<bool> StartWhatsAppLinkAsync()
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsync("/api/whatsapp/link", null);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return false;
        }
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> CheckWhatsAppLinkedAsync()
    {
        var dto = await GetAsync<WhatsAppLinkedDto>("/api/whatsapp/check-linked");
        return dto?.Linked ?? false;
    }

    public async Task<bool> UnlinkWhatsAppAsync()
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsync("/api/whatsapp/unlink", null);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return false;
        }
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> CancelWhatsAppLinkAsync()
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsync("/api/whatsapp/cancel-link", null);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return false;
        }
        return response.IsSuccessStatusCode;
    }

    public async Task<WhatsAppSendResultDto?> SendWhatsAppTestAsync(string phone)
    {
        var appName = "AI-ML";
        var ts = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
        var message = $"Prueba de integración {appName} - {ts}";
        return await PostAsync<WhatsAppSendResultDto>("/api/whatsapp/send", new { phone, message });
    }

    public async Task<List<WhatsAppSendResultDto>?> SendWhatsAppTestBulkAsync(IEnumerable<string> phones)
    {
        var appName = "AI-ML";
        var ts = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss");
        var message = $"Prueba de integración {appName} - {ts}";
        var recipients = phones.Select(p => new { phone = p, name = (string?)null, message = (string?)null }).ToList();
        return await PostAsync<List<WhatsAppSendResultDto>>("/api/whatsapp/send-bulk", new { recipients, message });
    }

    // --- Archivos ---
    public async Task<FilesListResponse?> ListFilesAsync(string path)
    {
        var q = Uri.EscapeDataString(path ?? "");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return await GetAsync<FilesListResponse>($"/api/files/list?path={q}&_={ts}");
    }

    public async Task<StorageProviderResponse?> GetFilesProviderAsync()
    {
        return await GetAsync<StorageProviderResponse>("/api/files/provider");
    }

    public async Task<bool> SetFilesProviderAsync(string provider)
    {
        var result = await PostAsync<object>("/api/files/provider", new { provider });
        return result is not null;
    }

    public async Task<bool> CreateFolderAsync(string path, string name)
    {
        var result = await PostAsync<object>("/api/files/folder", new { path, name });
        return result is not null;
    }

    public async Task<List<FileDeleteResult>?> DeleteFilesAsync(IEnumerable<string> paths)
    {
        return await PostAsync<List<FileDeleteResult>>("/api/files/delete", new { paths });
    }

    public async Task<bool> RenameFileAsync(string path, string newName)
    {
        var result = await PostAsync<object>("/api/files/rename", new { path, newName });
        return result is not null;
    }

    public async Task<List<FileDeleteResult>?> MoveFilesAsync(IEnumerable<string> paths, string targetPath)
    {
        return await PostAsync<List<FileDeleteResult>>("/api/files/move", new { paths, targetPath });
    }

    public async Task<List<FileUploadResult>?> UploadFilesAsync(string path, IEnumerable<(string name, Stream stream, long size)> files)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        foreach (var (name, stream, size) in files)
        {
            var sc = new StreamContent(stream);
            sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(sc, "files", name);
        }
        var q = Uri.EscapeDataString(path ?? "");
        var response = await _http.PostAsync($"/api/files/upload?path={q}", content);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return null;
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<FileUploadResult>>();
    }

    public string BuildFilesDownloadUrl(string path) =>
        $"/api/files/download?path={Uri.EscapeDataString(path)}";

    public async Task<(Stream stream, string fileName)?> DownloadFileAsync(string path)
    {
        await SetAuthHeaderAsync();
        var response = await _http.GetAsync(BuildFilesDownloadUrl(path), HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return null;
        }
        if (!response.IsSuccessStatusCode) return null;
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
                   ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? System.IO.Path.GetFileName(path);
        var stream = await response.Content.ReadAsStreamAsync();
        return (stream, name);
    }

    // --- Backups ---
    public async Task<List<BackupFileDto>?> ListBackupsAsync()
    {
        return await GetAsync<List<BackupFileDto>>("/api/backups");
    }

    public async Task<BackupSettingsDto?> GetBackupSettingsAsync()
    {
        return await GetAsync<BackupSettingsDto>("/api/backups/settings");
    }

    public async Task<BackupSettingsDto?> UpdateBackupSettingsAsync(UpdateBackupSettingsRequest request)
    {
        return await PutAsync<BackupSettingsDto>("/api/backups/settings", request);
    }

    public async Task<BackupFileDto?> CreateBackupAsync()
    {
        return await PostAsync<BackupFileDto>("/api/backups", new { });
    }

    public async Task<bool> DeleteBackupAsync(int id)
    {
        await SetAuthHeaderAsync();
        var response = await _http.DeleteAsync($"/api/backups/{id}");
        if (response.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return false; }
        return response.IsSuccessStatusCode;
    }

    public async Task<(bool ok, string? error)> RestoreBackupAsync(RestoreBackupRequest request)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/backups/restore", request);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return (false, "No autorizado"); }
        if (response.IsSuccessStatusCode) return (true, null);
        try
        {
            var err = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            return (false, err != null && err.TryGetValue("error", out var m) ? m : "Error al restaurar");
        }
        catch
        {
            return (false, "Error al restaurar");
        }
    }

    public async Task<BackupFileDto?> UploadBackupAsync(string fileName, Stream stream)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        var sc = new StreamContent(stream);
        sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(sc, "file", fileName);
        var response = await _http.PostAsync("/api/backups/upload", content);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<BackupFileDto>();
    }

    public string BuildBackupDownloadUrl(int id) => $"/api/backups/{id}/download";

    public async Task<(Stream stream, string fileName)?> DownloadBackupAsync(int id, string fallbackName)
    {
        await SetAuthHeaderAsync();
        var response = await _http.GetAsync(BuildBackupDownloadUrl(id), HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        if (!response.IsSuccessStatusCode) return null;
        var name = response.Content.Headers.ContentDisposition?.FileNameStar
                   ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                   ?? fallbackName;
        var stream = await response.Content.ReadAsStreamAsync();
        return (stream, name);
    }

    // --- Changelog ---
    public async Task<ChangelogResponse?> GetChangelogAsync(int page = 1, int pageSize = 15)
    {
        return await GetAsync<ChangelogResponse>($"/api/changelog?page={page}&pageSize={pageSize}");
    }

    // --- HTTP helpers ---
    private bool IsOnLoginPage()
    {
        try { return _navigation.Uri.Contains("/login", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private async Task HandleUnauthorizedAsync()
    {
        await _authService.LogoutAsync();
        if (!IsOnLoginPage())
            _navigation.NavigateTo("/login", forceLoad: true);
    }

    private async Task<T?> GetAsync<T>(string url)
    {
        await SetAuthHeaderAsync();
        var response = await _http.GetAsync(url);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return default;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    private async Task<T?> PostAsync<T>(string url, object data)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync(url, data);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return default;
        }

        await ThrowIfErrorAsync(response);
        return await response.Content.ReadFromJsonAsync<T>();
    }

    private async Task<T?> PutAsync<T>(string url, object data)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PutAsJsonAsync(url, data);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return default;
        }

        await ThrowIfErrorAsync(response);
        return await response.Content.ReadFromJsonAsync<T>();
    }

    // Si la respuesta no es exitosa, lee el body buscando { "error": "..." } y lanza HttpRequestException con ese mensaje.
    private static async Task ThrowIfErrorAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string? message = null;
        try
        {
            var dict = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            if (dict is not null && dict.TryGetValue("error", out var msg)) message = msg;
        }
        catch { /* no era JSON con la forma esperada */ }
        if (string.IsNullOrEmpty(message))
            message = $"Error {(int)response.StatusCode}: {response.ReasonPhrase}";
        throw new HttpRequestException(message);
    }

    private async Task<bool> DeleteAsync(string url)
    {
        await SetAuthHeaderAsync();
        var response = await _http.DeleteAsync(url);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return false;
        }

        return response.IsSuccessStatusCode;
    }

    private async Task<T?> DeleteWithBodyAsync<T>(string url)
    {
        await SetAuthHeaderAsync();
        var response = await _http.DeleteAsync(url);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return default;
        }
        await ThrowIfErrorAsync(response);
        return await response.Content.ReadFromJsonAsync<T>();
    }

    // El JWT viaja en una cookie httpOnly que el browser envia automaticamente
    // en cada request al mismo origen, asi que ya no hace falta setear Authorization.
    // Aprovechamos este hook para inyectar el header X-Operator-Name (operador
    // actual seleccionado en la UI) en cada request.
    private Task SetAuthHeaderAsync()
    {
        EnsureOperatorHeader();
        return Task.CompletedTask;
    }

    public async Task<BulkPublishResponse?> BulkPublishAsync(BulkPublishRequest request)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/meli/publish/bulk", request);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await _authService.LogoutAsync();
            _navigation.NavigateTo("/login", forceLoad: true);
            return null;
        }
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            try
            {
                var doc = System.Text.Json.JsonDocument.Parse(errorBody);
                if (doc.RootElement.TryGetProperty("error", out var errorProp))
                    throw new Exception(errorProp.GetString());
            }
            catch (System.Text.Json.JsonException) { }
            throw new Exception("Error del servidor (" + response.StatusCode + ")");
        }
        return await response.Content.ReadFromJsonAsync<BulkPublishResponse>();
    }

}
