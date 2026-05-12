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

    public async Task<bool> ResetUserPasswordAsync(int id, string newPassword)
    {
        var r = await PostAsync<object>($"/api/users/{id}/reset-password", new { newPassword });
        return r is not null;
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

    public async Task<MeliItemDto?> LinkItemToComboAsync(string meliItemId, int comboId)
    {
        return await PutAsync<MeliItemDto>($"/api/meli/items/{meliItemId}/link-combo", new { comboId });
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

    /// <summary>Crea una variedad de cafe (padre + 3 hijos 1kg/500g/250g) en una sola llamada.</summary>
    public async Task<CreateCoffeeVarietyResponse?> CreateCoffeeVarietyAsync(CreateCoffeeVarietyRequest request)
    {
        return await PostAsync<CreateCoffeeVarietyResponse>("/api/products/coffee-variety", request);
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

    // --- Alquileres: Equipos ---
    public async Task<List<AlqEquipoDto>?> GetAlqEquiposAsync()
        => await GetAsync<List<AlqEquipoDto>>("/api/alquileres/equipos");

    public async Task<AlqEquipoDto?> CreateAlqEquipoAsync(CreateAlqEquipoRequest request)
        => await PostAsync<AlqEquipoDto>("/api/alquileres/equipos", request);

    public async Task<AlqEquipoDto?> UpdateAlqEquipoAsync(int id, UpdateAlqEquipoRequest request)
        => await PutAsync<AlqEquipoDto>($"/api/alquileres/equipos/{id}", request);

    public async Task<bool> DeleteAlqEquipoAsync(int id)
        => await DeleteAsync($"/api/alquileres/equipos/{id}");

    // --- Alquileres: Clientes ---
    public async Task<List<AlqClienteDto>?> GetAlqClientesAsync()
        => await GetAsync<List<AlqClienteDto>>("/api/alquileres/clientes");

    public async Task<AlqClienteDto?> CreateAlqClienteAsync(CreateAlqClienteRequest request)
        => await PostAsync<AlqClienteDto>("/api/alquileres/clientes", request);

    public async Task<AlqClienteDto?> UpdateAlqClienteAsync(int id, UpdateAlqClienteRequest request)
        => await PutAsync<AlqClienteDto>($"/api/alquileres/clientes/{id}", request);

    public async Task<bool> DeleteAlqClienteAsync(int id)
        => await DeleteAsync($"/api/alquileres/clientes/{id}");

    // --- Alquileres: Reservas ---
    public async Task<List<AlqReservaDto>?> GetAlqReservasAsync(string? estado = null, DateTime? from = null, DateTime? to = null)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(estado)) qs.Add($"estado={Uri.EscapeDataString(estado)}");
        if (from.HasValue) qs.Add($"from={from.Value:yyyy-MM-dd}");
        if (to.HasValue) qs.Add($"to={to.Value:yyyy-MM-dd}");
        var url = "/api/alquileres/reservas" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<AlqReservaDto>>(url);
    }

    public async Task<AlqReservaDto?> GetAlqReservaAsync(int id)
        => await GetAsync<AlqReservaDto>($"/api/alquileres/reservas/{id}");

    public async Task<AlqReservaDto?> CreateAlqReservaAsync(CreateAlqReservaRequest request)
        => await PostAsync<AlqReservaDto>("/api/alquileres/reservas", request);

    public async Task<AlqReservaDto?> UpdateAlqReservaAsync(int id, UpdateAlqReservaRequest request)
        => await PutAsync<AlqReservaDto>($"/api/alquileres/reservas/{id}", request);

    public async Task<bool> DeleteAlqReservaAsync(int id)
        => await DeleteAsync($"/api/alquileres/reservas/{id}");

    public async Task<List<AlqDisponibilidadDto>?> GetAlqDisponibilidadAsync(DateTime fechaEntrega, DateTime fechaRetiro, int? excluirReservaId = null)
    {
        var url = $"/api/alquileres/reservas/disponibilidad?fechaEntrega={fechaEntrega:yyyy-MM-dd}&fechaRetiro={fechaRetiro:yyyy-MM-dd}";
        if (excluirReservaId.HasValue) url += $"&excluirReservaId={excluirReservaId.Value}";
        return await GetAsync<List<AlqDisponibilidadDto>>(url);
    }

    public async Task<string> GetAlqCondicionesAsync()
    {
        var resp = await GetAsync<AlqCondicionesDto>("/api/alquileres/reservas/condiciones");
        return resp?.Texto ?? "";
    }

    public async Task<bool> SetAlqCondicionesAsync(string texto)
    {
        var resp = await PutAsync<AlqCondicionesDto>("/api/alquileres/reservas/condiciones", new { texto });
        return resp is not null;
    }

    // --- Nominas: Empleados ---
    public async Task<List<NomEmpleadoDto>?> GetNomEmpleadosAsync()
        => await GetAsync<List<NomEmpleadoDto>>("/api/nominas/empleados");

    public async Task<NomEmpleadoDto?> CreateNomEmpleadoAsync(CreateNomEmpleadoRequest request)
        => await PostAsync<NomEmpleadoDto>("/api/nominas/empleados", request);

    public async Task<NomEmpleadoDto?> UpdateNomEmpleadoAsync(int id, UpdateNomEmpleadoRequest request)
        => await PutAsync<NomEmpleadoDto>($"/api/nominas/empleados/{id}", request);

    public async Task<bool> DeleteNomEmpleadoAsync(int id)
        => await DeleteAsync($"/api/nominas/empleados/{id}");

    // --- Nominas: Liquidaciones ---
    public async Task<List<NomLiquidacionDto>?> GetNomLiquidacionesAsync(int? anio = null, int? mes = null, string? estado = null)
    {
        var qs = new List<string>();
        if (anio.HasValue) qs.Add($"anio={anio.Value}");
        if (mes.HasValue) qs.Add($"mes={mes.Value}");
        if (!string.IsNullOrWhiteSpace(estado)) qs.Add($"estado={Uri.EscapeDataString(estado)}");
        var url = "/api/nominas/liquidaciones" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<NomLiquidacionDto>>(url);
    }

    public async Task<NomLiquidacionDto?> GetNomLiquidacionAsync(int id)
        => await GetAsync<NomLiquidacionDto>($"/api/nominas/liquidaciones/{id}");

    public async Task<NomLiquidacionDto?> CreateNomLiquidacionAsync(CreateNomLiquidacionRequest request)
        => await PostAsync<NomLiquidacionDto>("/api/nominas/liquidaciones", request);

    public async Task<NomLiquidacionDto?> UpdateNomLiquidacionAsync(int id, UpdateNomLiquidacionRequest request)
        => await PutAsync<NomLiquidacionDto>($"/api/nominas/liquidaciones/{id}", request);

    public async Task<bool> DeleteNomLiquidacionAsync(int id)
        => await DeleteAsync($"/api/nominas/liquidaciones/{id}");

    public async Task<NomLiquidacionDto?> AddNomPagoAsync(CreateNomPagoRequest request)
        => await PostAsync<NomLiquidacionDto>("/api/nominas/pagos", request);

    public async Task<bool> DeleteNomPagoAsync(int id)
        => await DeleteAsync($"/api/nominas/pagos/{id}");

    public async Task<NomResumenMensualDto?> GetNomResumenAsync(int anio, int mes)
        => await GetAsync<NomResumenMensualDto>($"/api/nominas/resumen?anio={anio}&mes={mes}");

    // ════════════════════════════════════════════════════════════
    // BÓVEDA DE CONTRASEÑAS
    // ════════════════════════════════════════════════════════════
    // El token de la bóveda viaja en el header X-Vault-Token (NO la cookie de auth).
    // Si el server contesta 401 en estos endpoints significa "bóveda bloqueada"
    // (clave maestra incorrecta o auto-lock), no que el JWT de login expiró.
    public class VaultLockedException : Exception { public VaultLockedException(string msg) : base(msg) { } }

    private async Task<T?> VaultRequestAsync<T>(HttpMethod method, string url, object? body, string? vaultToken)
    {
        await SetAuthHeaderAsync();
        using var req = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(vaultToken)) req.Headers.Add("X-Vault-Token", vaultToken);
        if (body is not null)
        {
            req.Content = System.Net.Http.Json.JsonContent.Create(body);
        }
        var response = await _http.SendAsync(req);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            // Si el JWT de login expiró el server contestaría 401 antes de llegar al [Authorize] del vault.
            // Distinguimos: el body típicamente viene { "error": "Bóveda bloqueada" }.
            string? body2 = null;
            try { body2 = await response.Content.ReadAsStringAsync(); } catch { }
            if (body2 is not null && body2.Contains("Bóveda", StringComparison.OrdinalIgnoreCase))
                throw new VaultLockedException("Bóveda bloqueada");
            await HandleUnauthorizedAsync();
            return default;
        }
        await ThrowIfErrorAsync(response);
        if (response.Content.Headers.ContentLength == 0) return default;
        return await response.Content.ReadFromJsonAsync<T>();
    }

    public Task<VaultStatusDto?> GetVaultStatusAsync(string? token = null)
        => VaultRequestAsync<VaultStatusDto>(HttpMethod.Get, "/api/vault/status", null, token);

    public Task<VaultStatusDto?> SetupVaultAsync(VaultSetupRequest request)
        => VaultRequestAsync<VaultStatusDto>(HttpMethod.Post, "/api/vault/setup", request, null);

    public Task<VaultUnlockResponse?> UnlockVaultAsync(string password)
        => VaultRequestAsync<VaultUnlockResponse>(HttpMethod.Post, "/api/vault/unlock", new { password }, null);

    public Task LockVaultAsync(string token)
        => VaultRequestAsync<object>(HttpMethod.Post, "/api/vault/lock", null, token);

    public Task<List<VaultEntryDto>?> ListVaultEntriesAsync(string token)
        => VaultRequestAsync<List<VaultEntryDto>>(HttpMethod.Get, "/api/vault/entries", null, token);

    public Task<VaultEntryDto?> CreateVaultEntryAsync(string token, VaultUpsertEntryRequest request)
        => VaultRequestAsync<VaultEntryDto>(HttpMethod.Post, "/api/vault/entries", request, token);

    public Task<VaultEntryDto?> UpdateVaultEntryAsync(string token, int id, VaultUpsertEntryRequest request)
        => VaultRequestAsync<VaultEntryDto>(HttpMethod.Put, $"/api/vault/entries/{id}", request, token);

    public async Task<bool> DeleteVaultEntryAsync(string token, int id)
    {
        try
        {
            await VaultRequestAsync<object>(HttpMethod.Delete, $"/api/vault/entries/{id}", null, token);
            return true;
        }
        catch { return false; }
    }

    public Task ChangeVaultMasterAsync(string token, VaultChangeMasterRequest request)
        => VaultRequestAsync<object>(HttpMethod.Post, "/api/vault/change-master", request, token);

    public Task UpdateVaultSettingsAsync(string token, VaultUpdateSettingsRequest request)
        => VaultRequestAsync<object>(HttpMethod.Put, "/api/vault/settings", request, token);

    public Task<VaultGenerateResponse?> GenerateVaultPasswordAsync(string token, VaultGenerateRequest request)
        => VaultRequestAsync<VaultGenerateResponse>(HttpMethod.Post, "/api/vault/generate", request, token);

    // --- Postits ---
    public async Task<List<PostitDto>?> GetPostitsAsync(string? scope = null)
    {
        var url = "/api/postits";
        if (!string.IsNullOrWhiteSpace(scope)) url += $"?scope={Uri.EscapeDataString(scope)}";
        return await GetAsync<List<PostitDto>>(url);
    }

    public async Task<PostitDto?> CreatePostitAsync(CreatePostitRequest request)
        => await PostAsync<PostitDto>("/api/postits", request);

    public async Task<PostitDto?> UpdatePostitAsync(int id, UpdatePostitRequest request)
        => await PutAsync<PostitDto>($"/api/postits/{id}", request);

    public async Task<bool> DeletePostitAsync(int id)
        => await DeleteAsync($"/api/postits/{id}");

    // --- Asistente ---
    public async Task<AssistantChatResponse?> AssistantChatAsync(List<AssistantChatMessage> messages)
        => await PostAsync<AssistantChatResponse>("/api/assistant/chat", new AssistantChatRequest { Messages = messages });

    // --- Cafe: Clientes ---
    public async Task<List<CafeClienteDto>?> GetCafeClientesAsync()
        => await GetAsync<List<CafeClienteDto>>("/api/cafe/clientes");

    public async Task<CafeClienteDto?> CreateCafeClienteAsync(CreateCafeClienteRequest request)
        => await PostAsync<CafeClienteDto>("/api/cafe/clientes", request);

    public async Task<CafeClienteDto?> UpdateCafeClienteAsync(int id, UpdateCafeClienteRequest request)
        => await PutAsync<CafeClienteDto>($"/api/cafe/clientes/{id}", request);

    public async Task<bool> DeleteCafeClienteAsync(int id)
        => await DeleteAsync($"/api/cafe/clientes/{id}");

    // --- Cafe: Productos ---
    public async Task<List<CafeProductoDto>?> GetCafeProductosAsync(string? categoria = null)
    {
        var url = "/api/cafe/productos";
        if (!string.IsNullOrWhiteSpace(categoria)) url += $"?categoria={Uri.EscapeDataString(categoria)}";
        return await GetAsync<List<CafeProductoDto>>(url);
    }

    public async Task<CafeProductoDto?> CreateCafeProductoAsync(CreateCafeProductoRequest request)
        => await PostAsync<CafeProductoDto>("/api/cafe/productos", request);

    public async Task<CafeProductoDto?> UpdateCafeProductoAsync(int id, UpdateCafeProductoRequest request)
        => await PutAsync<CafeProductoDto>($"/api/cafe/productos/{id}", request);

    public async Task<bool> DeleteCafeProductoAsync(int id)
        => await DeleteAsync($"/api/cafe/productos/{id}");

    public async Task<List<CafeHistorialPrecioDto>?> GetCafeProductoHistorialAsync(int id)
        => await GetAsync<List<CafeHistorialPrecioDto>>($"/api/cafe/productos/{id}/historial-precios");

    // --- Cafe: Settings ---
    public async Task<CafeSettingDto?> GetCafeSettingsAsync()
        => await GetAsync<CafeSettingDto>("/api/cafe/settings");

    public async Task<CafeSettingDto?> UpdateCafeSettingsAsync(UpdateCafeSettingRequest request)
        => await PutAsync<CafeSettingDto>("/api/cafe/settings", request);

    // --- Cafe: Ventas ---
    public async Task<List<CafeVentaDto>?> GetCafeVentasAsync(DateTime? from = null, DateTime? to = null)
    {
        var qs = new List<string>();
        if (from.HasValue) qs.Add($"from={from.Value:yyyy-MM-dd}");
        if (to.HasValue) qs.Add($"to={to.Value:yyyy-MM-dd}");
        var url = "/api/cafe/ventas" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<CafeVentaDto>>(url);
    }

    public async Task<CafeVentaDto?> GetCafeVentaAsync(int id)
        => await GetAsync<CafeVentaDto>($"/api/cafe/ventas/{id}");

    /// <summary>Devuelve los bytes del PDF de una venta Café (cotización/proforma) o null si fallo.</summary>
    public async Task<(byte[]? bytes, string? error)> GetCafeVentaPdfAsync(int id)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync($"/api/cafe/ventas/{id}/pdf");
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (null, "Sesión expirada");
        }
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadAsByteArrayAsync(), null);
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return (null, err.GetString());
        }
        catch { }
        return (null, body);
    }

    public async Task<CafeCotizadoDto?> CotizarCafeAsync(CafeCotizarRequest request)
        => await PostAsync<CafeCotizadoDto>("/api/cafe/ventas/cotizar", request);

    public async Task<CafeVentaDto?> CreateCafeVentaAsync(CreateCafeVentaRequest request)
        => await PostAsync<CafeVentaDto>("/api/cafe/ventas", request);

    public async Task<CafeVentaDto?> AnularCafeVentaAsync(int id)
        => await PostAsync<CafeVentaDto>($"/api/cafe/ventas/{id}/anular", new { });

    public async Task<CafeVentaDto?> RetryArcaCafeVentaAsync(int id)
        => await PostAsync<CafeVentaDto>($"/api/cafe/ventas/{id}/retry-arca", new { });

    /// <summary>Pide al backend un payload pre-armado para duplicar el comprobante.
    /// NO crea la venta — solo devuelve los datos para llenar el modal de Nueva Venta.</summary>
    public async Task<DuplicarVentaPayloadDto?> DuplicarCafeVentaAsync(int id)
        => await PostAsync<DuplicarVentaPayloadDto>($"/api/cafe/ventas/{id}/duplicar", new { });

    /// <summary>Convierte una proforma (X o PRO) en factura real (FA/FB/FC) con CAE de ARCA.
    /// Crea una venta NUEVA, vincula a la original. Devuelve la nueva venta con su CAE.</summary>
    public async Task<CafeVentaDto?> ConvertirCafeVentaAFacturaAsync(int id, ConvertirAFacturaRequest req)
        => await PostAsync<CafeVentaDto>($"/api/cafe/ventas/{id}/convertir-a-factura", req);

    /// <summary>Genera un PDF de PREVIEW del comprobante con los datos del modal sin guardar
    /// nada en la base. Devuelve los bytes para abrir en una pestaña nueva.</summary>
    public async Task<(byte[]? bytes, string? error)> PreviewCafeVentaPdfAsync(CreateCafeVentaRequest request)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("/api/cafe/ventas/preview-pdf", request);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return (null, "Sesión expirada"); }
        if (resp.IsSuccessStatusCode) return (await resp.Content.ReadAsByteArrayAsync(), null);
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return (null, err.GetString());
        }
        catch { }
        return (null, body);
    }

    public async Task<CafeVentaDto?> UpdateCafeVentaFlagsAsync(int id, UpdateCafeVentaFlagsRequest req)
        => await PutAsync<CafeVentaDto>($"/api/cafe/ventas/{id}/flags", req);

    public async Task<CafeVentaDto?> UpdateCafeVentaAsync(int id, UpdateCafeVentaRequest req)
        => await PutAsync<CafeVentaDto>($"/api/cafe/ventas/{id}", req);

    public async Task<DeleteCafeVentaSettingsDto?> GetCafeDeleteSettingsAsync()
        => await GetAsync<DeleteCafeVentaSettingsDto>("/api/cafe/ventas/delete-settings");

    public async Task<List<CafeTopProductoClienteDto>?> GetCafeTopProductosByClienteAsync(int clienteId, int count = 10)
        => await GetAsync<List<CafeTopProductoClienteDto>>($"/api/cafe/ventas/top-productos-cliente/{clienteId}?count={count}");

    // --- Cafe: Proveedores ---
    public async Task<List<CafeProveedorDto>?> GetCafeProveedoresAsync(bool? activos = null)
    {
        var url = "/api/cafe/proveedores" + (activos == true ? "?activos=true" : "");
        return await GetAsync<List<CafeProveedorDto>>(url);
    }
    public async Task<CafeProveedorDto?> GetCafeProveedorAsync(int id)
        => await GetAsync<CafeProveedorDto>($"/api/cafe/proveedores/{id}");
    public async Task<CafeProveedorDto?> CreateCafeProveedorAsync(CreateCafeProveedorRequest req)
        => await PostAsync<CafeProveedorDto>("/api/cafe/proveedores", req);
    public async Task<CafeProveedorDto?> UpdateCafeProveedorAsync(int id, UpdateCafeProveedorRequest req)
        => await PutAsync<CafeProveedorDto>($"/api/cafe/proveedores/{id}", req);
    public async Task<bool> DeleteCafeProveedorAsync(int id)
        => await DeleteAsync($"/api/cafe/proveedores/{id}");

    // --- Cafe: Marcas ---
    public async Task<List<CafeMarcaDto>?> GetCafeMarcasAsync(bool? activos = null, int? proveedorId = null)
    {
        var qs = new List<string>();
        if (activos == true) qs.Add("activos=true");
        if (proveedorId.HasValue) qs.Add($"proveedorId={proveedorId.Value}");
        var url = "/api/cafe/marcas" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<CafeMarcaDto>>(url);
    }
    public async Task<CafeMarcaDto?> GetCafeMarcaAsync(int id)
        => await GetAsync<CafeMarcaDto>($"/api/cafe/marcas/{id}");
    public async Task<CafeMarcaDto?> CreateCafeMarcaAsync(CreateCafeMarcaRequest req)
        => await PostAsync<CafeMarcaDto>("/api/cafe/marcas", req);
    public async Task<CafeMarcaDto?> UpdateCafeMarcaAsync(int id, UpdateCafeMarcaRequest req)
        => await PutAsync<CafeMarcaDto>($"/api/cafe/marcas/{id}", req);
    public async Task<bool> DeleteCafeMarcaAsync(int id)
        => await DeleteAsync($"/api/cafe/marcas/{id}");

    // --- Cafe: Listas de precios ---
    public async Task<CafeListaPreciosPreviewDto?> GetCafeListaPreciosPreviewAsync(CafeListaPreciosFiltroRequest req)
        => await PostAsync<CafeListaPreciosPreviewDto>("/api/cafe/listas-precios/preview", req);

    public async Task<CafeConsultaResultDto?> ConsultarCafeAsync(string query)
        => await PostAsync<CafeConsultaResultDto>("/api/cafe/consultas", new CafeConsultaRequest { Query = query });

    // --- Cotejo MeLi <-> Contabilium ---
    public async Task<CotejoImportResultDto?> ImportContabiliumStagingAsync()
        => await PostAsync<CotejoImportResultDto>("/api/meli/cotejo/import-staging", new { });

    public async Task<CotejoResumenDto?> GetCotejoResumenAsync()
        => await GetAsync<CotejoResumenDto>("/api/meli/cotejo/resumen");

    public async Task<List<string>?> GetCotejoMarcasContabAsync()
        => await GetAsync<List<string>>("/api/meli/cotejo/marcas-contab");

    public async Task<List<CotejoFilaDto>?> GetCotejoListadoAsync(string categoria = "todos", string? buscar = null, int take = 200, string? marcaContab = null, string? vinculacion = null)
    {
        var qs = new List<string> { $"categoria={Uri.EscapeDataString(categoria)}", $"take={take}" };
        if (!string.IsNullOrWhiteSpace(buscar)) qs.Add($"buscar={Uri.EscapeDataString(buscar)}");
        if (!string.IsNullOrWhiteSpace(marcaContab)) qs.Add($"marcaContab={Uri.EscapeDataString(marcaContab)}");
        if (!string.IsNullOrWhiteSpace(vinculacion)) qs.Add($"vinculacion={Uri.EscapeDataString(vinculacion)}");
        return await GetAsync<List<CotejoFilaDto>>("/api/meli/cotejo/listar?" + string.Join("&", qs));
    }

    public async Task<ComboDetalleDto?> GetCotejoComboDetalleAsync(string skuCombo)
        => await GetAsync<ComboDetalleDto>($"/api/meli/cotejo/combo/{Uri.EscapeDataString(skuCombo)}");

    public async Task<CrearProductosCotejoResultDto?> CrearProductosCotejoAsync(CrearProductosCotejoRequest req)
        => await PostAsync<CrearProductosCotejoResultDto>("/api/meli/cotejo/crear-productos", req);

    public async Task<CrearKitsCotejoResultDto?> CrearKitsCotejoAsync(CrearKitsCotejoRequest req)
        => await PostAsync<CrearKitsCotejoResultDto>("/api/meli/cotejo/crear-kits", req);

    public async Task<VincularOemsResultDto?> VincularOemsAutomaticoAsync()
        => await PostAsync<VincularOemsResultDto>("/api/meli/cotejo/vincular-oems", new { });

    // --- Kits (productos compuestos / BOM) ---
    public async Task<List<CafeKitDto>?> GetCafeKitsAsync(bool? activos = null, string? categoria = null)
    {
        var qs = new List<string>();
        if (activos == true) qs.Add("activos=true");
        if (!string.IsNullOrWhiteSpace(categoria)) qs.Add($"categoria={Uri.EscapeDataString(categoria)}");
        var url = "/api/cafe/kits" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<CafeKitDto>>(url);
    }

    public async Task<CafeKitDto?> CreateCafeKitAsync(CreateCafeKitRequest req)
        => await PostAsync<CafeKitDto>("/api/cafe/kits", req);

    public async Task<CafeKitDto?> UpdateCafeKitAsync(int id, UpdateCafeKitRequest req)
        => await PutAsync<CafeKitDto>($"/api/cafe/kits/{id}", req);

    public async Task<bool> DeleteCafeKitAsync(int id)
        => await DeleteAsync($"/api/cafe/kits/{id}");

    // --- Descuentos por canal x marca ---
    public async Task<CafeDescuentoGrillaResponse?> GetDescuentosGrillaAsync()
        => await GetAsync<CafeDescuentoGrillaResponse>("/api/cafe/descuentos/grilla");

    public async Task<bool> UpsertDescuentoAsync(UpsertDescuentoRequest req)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/cafe/descuentos", req);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return false; }
        if (!response.IsSuccessStatusCode) await ThrowIfErrorAsync(response);
        return true;
    }

    // --- Reglas de precios ---
    public async Task<CafeReglasPreciosResponse?> GetReglasPreciosAsync()
        => await GetAsync<CafeReglasPreciosResponse>("/api/cafe/reglas-precios");

    public async Task<bool> UpsertReglaPrecioAsync(UpsertReglaPrecioRequest req)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/cafe/reglas-precios", req);
        if (response.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return false; }
        if (!response.IsSuccessStatusCode) await ThrowIfErrorAsync(response);
        return true;
    }

    public async Task<bool> DeleteReglaPrecioAsync(int id)
        => await DeleteAsync($"/api/cafe/reglas-precios/{id}");

    public async Task<byte[]?> ExportCafeListaPreciosExcelAsync(CafeListaPreciosFiltroRequest req)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/cafe/listas-precios/export-excel", req);
        if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync();
        await ThrowIfErrorAsync(response);
        return null;
    }

    // --- Cafe: Compras ---
    public async Task<List<CafeCompraDto>?> GetCafeComprasAsync(DateTime? from = null, DateTime? to = null, string? estado = null, int? proveedorId = null)
    {
        var qs = new List<string>();
        if (from.HasValue) qs.Add($"from={from.Value:yyyy-MM-dd}");
        if (to.HasValue) qs.Add($"to={to.Value:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(estado)) qs.Add($"estado={Uri.EscapeDataString(estado)}");
        if (proveedorId.HasValue) qs.Add($"proveedorId={proveedorId.Value}");
        var url = "/api/cafe/compras" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<CafeCompraDto>>(url);
    }
    public async Task<CafeCompraDto?> GetCafeCompraAsync(int id)
        => await GetAsync<CafeCompraDto>($"/api/cafe/compras/{id}");
    public async Task<CafeCompraDto?> CreateCafeCompraAsync(CreateCafeCompraRequest req)
        => await PostAsync<CafeCompraDto>("/api/cafe/compras", req);
    public async Task<CafeCompraDto?> UpdateCafeCompraAsync(int id, UpdateCafeCompraRequest req)
        => await PutAsync<CafeCompraDto>($"/api/cafe/compras/{id}", req);
    public async Task<bool> DeleteCafeCompraAsync(int id)
        => await DeleteAsync($"/api/cafe/compras/{id}");
    public async Task<CafeCompraDto?> ConfirmarCafeCompraAsync(int id)
        => await PostAsync<CafeCompraDto>($"/api/cafe/compras/{id}/confirmar", new { });
    public async Task<CafeCompraDto?> PagarCafeCompraAsync(int id)
        => await PostAsync<CafeCompraDto>($"/api/cafe/compras/{id}/pagar", new { });
    public async Task<CafeCompraDto?> AnularCafeCompraAsync(int id)
        => await PostAsync<CafeCompraDto>($"/api/cafe/compras/{id}/anular", new { });

    public async Task<bool> DeleteCafeVentaAsync(int id, string password)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync($"/api/cafe/ventas/{id}/delete", new { password });
        if (response.IsSuccessStatusCode) return true;
        await ThrowIfErrorAsync(response);
        return false;
    }

    public async Task<int> BulkDeleteCafeVentasAsync(List<int> ids, string password)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/cafe/ventas/bulk-delete", new { ids, password });
        if (response.IsSuccessStatusCode)
        {
            var json = await response.Content.ReadFromJsonAsync<BulkDeleteResponse>();
            return json?.Deleted ?? 0;
        }
        await ThrowIfErrorAsync(response);
        return 0;
    }

    private class BulkDeleteResponse { public int Deleted { get; set; } }

    // --- Cafe: Combos ---
    public async Task<List<CafeComboDto>?> GetCafeCombosAsync(bool? activos = null)
    {
        var url = "/api/cafe/combos" + (activos == true ? "?activos=true" : "");
        return await GetAsync<List<CafeComboDto>>(url);
    }

    public async Task<CafeComboDto?> GetCafeComboAsync(int id)
        => await GetAsync<CafeComboDto>($"/api/cafe/combos/{id}");

    public async Task<CafeComboDto?> CreateCafeComboAsync(CreateCafeComboRequest req)
        => await PostAsync<CafeComboDto>("/api/cafe/combos", req);

    public async Task<CafeComboDto?> UpdateCafeComboAsync(int id, UpdateCafeComboRequest req)
        => await PutAsync<CafeComboDto>($"/api/cafe/combos/{id}", req);

    public async Task<bool> DeleteCafeComboAsync(int id)
        => await DeleteAsync($"/api/cafe/combos/{id}");

    // --- Cafe: OEMs ---
    public async Task<List<CafeOemDto>?> GetCafeOemsAsync(string? proveedor = null, string? marca = null, string? q = null)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(proveedor)) qs.Add($"proveedor={Uri.EscapeDataString(proveedor)}");
        if (!string.IsNullOrEmpty(marca)) qs.Add($"marca={Uri.EscapeDataString(marca)}");
        if (!string.IsNullOrEmpty(q)) qs.Add($"q={Uri.EscapeDataString(q)}");
        var url = "/api/cafe/oems" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<CafeOemDto>>(url);
    }

    public async Task<CafeOemDto?> GetCafeOemAsync(int id)
        => await GetAsync<CafeOemDto>($"/api/cafe/oems/{id}");

    public async Task<CafeOemDto?> CreateCafeOemAsync(CreateCafeOemRequest req)
        => await PostAsync<CafeOemDto>("/api/cafe/oems", req);

    public async Task<CafeOemDto?> UpdateCafeOemAsync(int id, UpdateCafeOemRequest req)
        => await PutAsync<CafeOemDto>($"/api/cafe/oems/{id}", req);

    public async Task<bool> DeleteCafeOemAsync(int id)
        => await DeleteAsync($"/api/cafe/oems/{id}");

    public async Task<CafeOemImportResultDto?> ImportCafeOemsAsync(Stream fileStream, string fileName, string proveedor)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(streamContent, "file", fileName);
        content.Add(new StringContent(proveedor ?? ""), "proveedor");
        var response = await _http.PostAsync("/api/cafe/oems/import", content);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<CafeOemImportResultDto>();
        await ThrowIfErrorAsync(response);
        return null;
    }

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

    // --- Customer Tiers (listas de precios por tipo de cliente) ---
    public async Task<List<CustomerTierDto>?> GetCustomerTiersAsync()
        => await GetAsync<List<CustomerTierDto>>("/api/customer-tiers");

    public async Task<CustomerTierDto?> CreateCustomerTierAsync(CreateCustomerTierRequest request)
        => await PostAsync<CustomerTierDto>("/api/customer-tiers", request);

    public async Task<CustomerTierDto?> UpdateCustomerTierAsync(int id, UpdateCustomerTierRequest request)
        => await PutAsync<CustomerTierDto>($"/api/customer-tiers/{id}", request);

    public async Task<bool> DeleteCustomerTierAsync(int id)
        => await DeleteAsync($"/api/customer-tiers/{id}");

    public async Task<List<ProductTierPriceDto>?> GetProductTierPricesAsync(int productId)
        => await GetAsync<List<ProductTierPriceDto>>($"/api/products/{productId}/tier-prices");

    public async Task<ProductTierPriceDto?> SetProductPriceOverrideAsync(SetProductPriceOverrideRequest request)
        => await PostAsync<ProductTierPriceDto>("/api/customer-tiers/price-override", request);

    public async Task<bool> DeleteProductPriceOverrideAsync(int productId, int tierId)
        => await DeleteAsync($"/api/customer-tiers/price-override/{productId}/{tierId}");

    // --- Inventory: depositos + ajustes de stock ---
    public async Task<List<WarehouseDto>?> GetWarehousesAsync()
        => await GetAsync<List<WarehouseDto>>("/api/inventory/warehouses");

    public async Task<StockMovementDto?> AdjustStockAsync(AdjustStockRequest request)
        => await PostAsync<StockMovementDto>("/api/inventory/stock-adjust", request);

    public async Task<List<StockMovementDto>?> GetStockMovementsAsync(int? productId = null, int? warehouseId = null, int take = 50)
    {
        var qs = new List<string>();
        if (productId.HasValue) qs.Add($"productId={productId.Value}");
        if (warehouseId.HasValue) qs.Add($"warehouseId={warehouseId.Value}");
        qs.Add($"take={take}");
        return await GetAsync<List<StockMovementDto>>("/api/inventory/movements?" + string.Join("&", qs));
    }

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

    public async Task<List<TopProductByClientDto>?> GetTopProductsByClientAsync(int clientId, int count = 10)
        => await GetAsync<List<TopProductByClientDto>>($"/api/sales/top-products-by-client/{clientId}?count={count}");

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

    /// <summary>Consulta el padrón oficial ARCA (datos fiscales completos: razón social,
    /// domicilio, CP, localidad, provincia, condición IVA). Requiere cert ARCA autorizado.</summary>
    public async Task<ArcaPadronDto?> ConsultarPadronArcaAsync(string cuit, string? cuitEmisor = null)
    {
        var url = $"/api/fiscal/padron?cuit={Uri.EscapeDataString(cuit)}";
        if (!string.IsNullOrEmpty(cuitEmisor))
            url += $"&cuitEmisor={Uri.EscapeDataString(cuitEmisor)}";
        return await GetAsync<ArcaPadronDto>(url);
    }

    // --- Cotizaciones ---
    public async Task<DolarBnaDto?> GetDolarBnaAsync()
        => await GetAsync<DolarBnaDto>("/api/quotes/dolar-bna");

    /// <summary>Sumatoria de kg de café FRIKAF vendidos en el mes en curso.</summary>
    public async Task<CoffeeMonthlyKgDto?> GetCoffeeMonthlyKgAsync()
        => await GetAsync<CoffeeMonthlyKgDto>("/api/dashboard/coffee-monthly-kg");

    public async Task<CoffeeStockKgDto?> GetCoffeeStockKgAsync()
        => await GetAsync<CoffeeStockKgDto>("/api/dashboard/coffee-stock-kg");

    /// <summary>Resumen financiero (ventas del mes + saldos a cobrar de clientes).</summary>
    public async Task<SalesSummaryDto?> GetSalesSummaryAsync()
        => await GetAsync<SalesSummaryDto>("/api/dashboard/sales-summary");

    /// <summary>
    /// Sube una imagen de marca (logo, fondo, etc) bajo una key. Si ya hay una con
    /// la misma key, se reemplaza. La imagen luego se sirve desde /api/branding/{key}.
    /// </summary>
    public async Task UploadBrandingImageAsync(string key, Stream stream, string fileName, string contentType)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(stream);
        if (!string.IsNullOrEmpty(contentType))
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(streamContent, "file", string.IsNullOrEmpty(fileName) ? "upload" : fileName);

        var response = await _http.PostAsync($"/api/branding/upload/{Uri.EscapeDataString(key)}", content);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(string.IsNullOrEmpty(error) ? response.ReasonPhrase : error);
        }
    }

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

    // --- ARCA (scraping) Accounts ---
    public async Task<List<ArcaAccountDto>?> GetArcaAccountsAsync()
        => await GetAsync<List<ArcaAccountDto>>("/api/arca/accounts");

    public async Task<ArcaAccountDto?> CreateArcaAccountAsync(CreateArcaAccountRequest request)
        => await PostAsync<ArcaAccountDto>("/api/arca/accounts", request);

    public async Task<ArcaAccountDto?> UpdateArcaAccountAsync(int id, UpdateArcaAccountRequest request)
        => await PutAsync<ArcaAccountDto>($"/api/arca/accounts/{id}", request);

    public async Task<bool> DeleteArcaAccountAsync(int id)
        => await DeleteAsync($"/api/arca/accounts/{id}");

    /// <summary>Dispara el test (login + scraping) — responde inmediato. Pollear status.</summary>
    public async Task<(bool ok, string? error)> StartArcaTestAsync(int accountId)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsync($"/api/arca/accounts/{accountId}/test", null);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (false, "Sesión expirada");
        }
        if (resp.IsSuccessStatusCode) return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return (false, err.GetString());
        }
        catch { }
        return (false, body);
    }

    public async Task<ArcaTestStatusDto?> GetArcaTestStatusAsync()
        => await GetAsync<ArcaTestStatusDto>("/api/arca/test/status");

    // --- ARCA Webservice (certificados .pfx) ---
    public async Task<List<ArcaWebserviceAccountDto>?> GetArcaWebserviceAccountsAsync()
        => await GetAsync<List<ArcaWebserviceAccountDto>>("/api/arca-webservice/accounts");

    public async Task<(bool ok, string? error, ArcaWebserviceAccountDto? dto)> UploadArcaWebserviceAccountAsync(
        string cuit, string? alias, string? password, string environment, Stream fileStream, string fileName)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-pkcs12");
        content.Add(streamContent, "file", fileName);
        content.Add(new StringContent(cuit ?? ""), "cuit");
        if (!string.IsNullOrEmpty(alias)) content.Add(new StringContent(alias), "alias");
        if (!string.IsNullOrEmpty(password)) content.Add(new StringContent(password), "password");
        content.Add(new StringContent(string.IsNullOrEmpty(environment) ? "production" : environment), "environment");

        var response = await _http.PostAsync("/api/arca-webservice/accounts", content);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (false, "Sesión expirada", null);
        }
        if (response.IsSuccessStatusCode)
        {
            var dto = await response.Content.ReadFromJsonAsync<ArcaWebserviceAccountDto>();
            return (true, null, dto);
        }
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return (false, err.GetString(), null);
        }
        catch { }
        return (false, body, null);
    }

    public async Task<ArcaWebserviceAccountDto?> UpdateArcaWebserviceAccountAsync(int id, UpdateArcaWebserviceAccountRequest req)
        => await PutAsync<ArcaWebserviceAccountDto>($"/api/arca-webservice/accounts/{id}", req);

    public async Task<bool> DeleteArcaWebserviceAccountAsync(int id)
        => await DeleteAsync($"/api/arca-webservice/accounts/{id}");

    // Wizard de generación de certificado (CSR → .crt → .pfx)
    public async Task<(bool ok, string? error, GenerateArcaCsrResponseDto? dto)> GenerateArcaCsrAsync(string cuit, string alias)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("/api/arca-webservice/csr", new { cuit, alias });
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (false, "Sesión expirada", null);
        }
        if (resp.IsSuccessStatusCode)
        {
            var dto = await resp.Content.ReadFromJsonAsync<GenerateArcaCsrResponseDto>();
            return (true, null, dto);
        }
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return (false, err.GetString(), null);
        }
        catch { }
        return (false, body, null);
    }

    /// <summary>Devuelve los bytes del .csr generado (para descargar desde Blazor).</summary>
    public async Task<(byte[]? bytes, string? error)> DownloadArcaCsrAsync(int csrId)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync($"/api/arca-webservice/csr/{csrId}/download");
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (null, "Sesión expirada");
        }
        if (!resp.IsSuccessStatusCode) return (null, $"HTTP {(int)resp.StatusCode}");
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return (bytes, null);
    }

    public async Task<TestCertificateResultDto?> TestArcaCertificateAsync(int accountId)
        => await PostAsync<TestCertificateResultDto>($"/api/arca-webservice/accounts/{accountId}/test-certificate", new { });

    public async Task<UltimosComprobantesResultDto?> GetArcaLastComprobantesAsync(int accountId, UltimosComprobantesRequest req)
        => await PostAsync<UltimosComprobantesResultDto>($"/api/arca-webservice/accounts/{accountId}/last-comprobantes", req);

    public async Task<ComprobanteEmitidoDto?> GenerateArcaComprobanteAsync(int accountId, EmitirComprobanteRequest req)
        => await PostAsync<ComprobanteEmitidoDto>($"/api/arca-webservice/accounts/{accountId}/generate-comprobante", req);

    // --- ARCA Emisor (ficha de empresa) ---
    public async Task<List<ArcaEmisorDto>?> GetArcaEmisoresAsync()
        => await GetAsync<List<ArcaEmisorDto>>("/api/arca-emisor");

    public async Task<ArcaEmisorDto?> GetArcaEmisorAsync(string cuit)
        => await GetAsync<ArcaEmisorDto>($"/api/arca-emisor/{cuit}");

    public async Task<ArcaEmisorDto?> UpsertArcaEmisorAsync(UpsertArcaEmisorRequest req)
        => await PostAsync<ArcaEmisorDto>("/api/arca-emisor", req);

    public async Task<(bool ok, string? error, ArcaEmisorDto? dto)> UploadArcaEmisorLogoAsync(string cuit, Stream stream, string fileName, string contentType)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(stream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
        content.Add(streamContent, "file", fileName);
        var resp = await _http.PostAsync($"/api/arca-emisor/{cuit}/logo", content);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (false, "Sesión expirada", null);
        }
        if (resp.IsSuccessStatusCode)
        {
            var dto = await resp.Content.ReadFromJsonAsync<ArcaEmisorDto>();
            return (true, null, dto);
        }
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err)) return (false, err.GetString(), null);
        }
        catch { }
        return (false, body, null);
    }

    public async Task<bool> DeleteArcaEmisorLogoAsync(string cuit)
        => await DeleteAsync($"/api/arca-emisor/{cuit}/logo");

    /// <summary>Devuelve bytes del PDF de un comprobante autorizado, o null si fallo.</summary>
    public async Task<(byte[]? bytes, string? error)> GetArcaComprobantePdfAsync(int accountId, int ptoVta, int cbteTipo, int cbteNro)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync(
            $"/api/arca-webservice/accounts/{accountId}/comprobante-pdf",
            new { ptoVta, cbteTipo, cbteNro });
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (null, "Sesión expirada");
        }
        if (resp.IsSuccessStatusCode)
        {
            return (await resp.Content.ReadAsByteArrayAsync(), null);
        }
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return (null, err.GetString());
        }
        catch { }
        return (null, body);
    }

    public async Task<(bool ok, string? error, ArcaWebserviceAccountDto? dto)> FinalizeArcaCsrAsync(
        int csrId, Stream crtStream, string crtFileName, string? password, string environment, string? alias)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(crtStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Add(streamContent, "crt", crtFileName);
        if (!string.IsNullOrEmpty(password)) content.Add(new StringContent(password), "password");
        content.Add(new StringContent(string.IsNullOrEmpty(environment) ? "production" : environment), "environment");
        if (!string.IsNullOrEmpty(alias)) content.Add(new StringContent(alias), "alias");

        var resp = await _http.PostAsync($"/api/arca-webservice/csr/{csrId}/finalize", content);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (false, "Sesión expirada", null);
        }
        if (resp.IsSuccessStatusCode)
        {
            var dto = await resp.Content.ReadFromJsonAsync<ArcaWebserviceAccountDto>();
            return (true, null, dto);
        }
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return (false, err.GetString(), null);
        }
        catch { }
        return (false, body, null);
    }

    /// <summary>Dispara la descarga de Mis Comprobantes (Emitidos + Recibidos) — pollear status.</summary>
    public async Task<(bool ok, string? error)> StartArcaComprobantesAsync(int accountId, ArcaRangoFechasRequest rango)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/arca/accounts/{accountId}/comprobantes", rango);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (false, "Sesión expirada");
        }
        if (resp.IsSuccessStatusCode) return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
                return (false, err.GetString());
        }
        catch { }
        return (false, body);
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

    public async Task<MeliPushResultDto?> PushMeliItemFromProductAsync(int itemId, bool pushPrice = true, bool pushStock = true)
    {
        await SetAuthHeaderAsync();
        var body = new { pushPrice, pushStock };
        var response = await _http.PostAsJsonAsync($"/api/meli/items/{itemId}/push-from-product", body);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<MeliPushResultDto>();
        await ThrowIfErrorAsync(response);
        return null;
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

    // --- Pricing por empresa ---
    public async Task<List<CompanyDto>?> GetCompaniesAsync()
        => await GetAsync<List<CompanyDto>>("/api/pricing/companies");

    public async Task<List<ProductCompanyPriceDto>?> GetProductPricesAsync(int productId)
        => await GetAsync<List<ProductCompanyPriceDto>>($"/api/pricing/products/{productId}/prices");

    public async Task<ProductCompanyPriceDto?> SetProductPriceAsync(SetProductCompanyPriceRequest req)
        => await PostAsync<ProductCompanyPriceDto>("/api/pricing/products/prices", req);

    public async Task<bool> DeleteProductPriceAsync(int productId, int companyId)
    {
        var resp = await _http.DeleteAsync($"/api/pricing/products/{productId}/prices/{companyId}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<List<BrandCompanyMarkupDto>?> GetBrandMarkupsAsync(int brandId)
        => await GetAsync<List<BrandCompanyMarkupDto>>($"/api/pricing/brands/{brandId}/markups");

    public async Task<BrandCompanyMarkupDto?> SetBrandMarkupAsync(SetBrandCompanyMarkupRequest req)
        => await PostAsync<BrandCompanyMarkupDto>("/api/pricing/brands/markups", req);

    public async Task<bool> DeleteBrandMarkupAsync(int brandId, int companyId)
    {
        var resp = await _http.DeleteAsync($"/api/pricing/brands/{brandId}/markups/{companyId}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<ResolvedPriceDto?> ResolvePriceAsync(int productId, int? companyId)
        => await GetAsync<ResolvedPriceDto>($"/api/pricing/resolve?productId={productId}{(companyId.HasValue ? $"&companyId={companyId}" : "")}");

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

    /// <summary>Envía un mensaje cualquiera a un teléfono usando la sesión WhatsApp vinculada.</summary>
    public async Task<WhatsAppSendResultDto?> SendWhatsAppMessageAsync(string phone, string message)
    {
        return await PostAsync<WhatsAppSendResultDto>("/api/whatsapp/send", new { phone, message });
    }

    /// <summary>Re-vincula publicaciones ML huerfanas a productos por SKU u OEM exacto.</summary>
    public async Task<RelinkOrphansReportDto?> RelinkMeliOrphansAsync()
    {
        return await PostAsync<RelinkOrphansReportDto>("/api/products/relink-meli-orphans", new { });
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

    // ===== Cafe ↔ MeLi push =====
    public async Task<CafeMeliPreviewDto?> GetCafeMeliPreviewAsync(int cafeProductoId)
        => await GetAsync<CafeMeliPreviewDto>($"/api/cafe/productos/{cafeProductoId}/meli-preview");

    public async Task<CafeMeliPushResultDto?> PushCafeToMeliAsync(int cafeProductoId, List<int>? meliItemIds = null, bool pushPrice = true, bool pushStock = true)
        => await PostAsync<CafeMeliPushResultDto>($"/api/cafe/productos/{cafeProductoId}/push-meli",
            new { meliItemIds, pushPrice, pushStock });

    public async Task<object?> RefreshCafeMeliLogisticAsync(int cafeProductoId)
        => await PostAsync<object>($"/api/cafe/productos/{cafeProductoId}/refresh-meli-logistic", new { });

    public async Task<RenameMeliSkuResultDto?> RenameMeliSkuAsync(int cafeProductoId, List<int>? meliItemIds = null)
        => await PostAsync<RenameMeliSkuResultDto>($"/api/cafe/productos/{cafeProductoId}/rename-meli-sku",
            new { meliItemIds });

    // ===== MeLi Shipments (Mapeo Flex) =====
    public async Task<List<MeliShipmentDto>?> GetMeliFlexShipmentsAsync(string mode = "today", string? internalStatus = null, bool excludeDelivered = false)
    {
        var qs = new List<string> { $"mode={Uri.EscapeDataString(mode)}" };
        if (!string.IsNullOrWhiteSpace(internalStatus)) qs.Add($"internalStatus={Uri.EscapeDataString(internalStatus)}");
        if (excludeDelivered) qs.Add("excludeDelivered=true");
        return await GetAsync<List<MeliShipmentDto>>("/api/meli/shipments/flex?" + string.Join("&", qs));
    }

    public async Task<MeliShipmentSyncResultDto?> SyncMeliFlexShipmentsAsync(int days = 7, int maxOrders = 200)
        => await PostAsync<MeliShipmentSyncResultDto>("/api/meli/shipments/sync-flex", new { days, maxOrders });

    public async Task<bool> UpdateMeliShipmentInternalStatusAsync(int id, string internalStatus, string? notes = null)
    {
        var r = await PutAsync<object>($"/api/meli/shipments/{id}/internal-status", new { internalStatus, notes });
        return r is not null;
    }

    public async Task<StartPointDto?> GetMapeoStartPointAsync()
        => await GetAsync<StartPointDto>("/api/meli/shipments/start-point");

    public async Task<bool> SetMapeoStartPointAsync(string? address, decimal? lat, decimal? lng)
    {
        var r = await PutAsync<object>("/api/meli/shipments/start-point", new { address, lat, lng });
        return r is not null;
    }

    public async Task<List<GeocodeResultDto>?> GeocodeAsync(string query)
        => await GetAsync<List<GeocodeResultDto>>($"/api/meli/shipments/geocode?q={Uri.EscapeDataString(query)}");

    public async Task<PublicBaseUrlDto?> GetMapeoPublicBaseUrlAsync()
        => await GetAsync<PublicBaseUrlDto>("/api/meli/shipments/public-base-url");
    public async Task<bool> SetMapeoPublicBaseUrlAsync(string? url)
    {
        var r = await PutAsync<object>("/api/meli/shipments/public-base-url", new { url });
        return r is not null;
    }

    // ===== Mapeo: Drivers =====
    public async Task<List<MapeoDriverDto>?> GetMapeoDriversAsync()
        => await GetAsync<List<MapeoDriverDto>>("/api/mapeo/drivers");
    public async Task<MapeoDriverDto?> CreateMapeoDriverAsync(string nombre, string? telefono, string? color)
        => await PostAsync<MapeoDriverDto>("/api/mapeo/drivers", new { nombre, telefono, color });
    public async Task<MapeoDriverDto?> UpdateMapeoDriverAsync(int id, string? nombre, string? telefono, string? color, bool? isActive)
        => await PutAsync<MapeoDriverDto>($"/api/mapeo/drivers/{id}", new { nombre, telefono, color, isActive });
    public async Task<bool> DeleteMapeoDriverAsync(int id)
        => await DeleteAsync($"/api/mapeo/drivers/{id}");
    public async Task<string?> GenerateMapeoDriverShareTokenAsync(int id, bool regenerate = false)
    {
        var r = await PostAsync<TokenResponse>($"/api/mapeo/drivers/{id}/share-token?regenerate={regenerate.ToString().ToLower()}", new { });
        return r?.Token;
    }
    private class TokenResponse { public string? Token { get; set; } }

    // ===== Mapeo: Favoritos =====
    public async Task<List<MapeoFavoritoDto>?> GetMapeoFavoritosAsync(string? q = null)
        => await GetAsync<List<MapeoFavoritoDto>>("/api/mapeo/favoritos" + (string.IsNullOrWhiteSpace(q) ? "" : $"?q={Uri.EscapeDataString(q)}"));
    public async Task<MapeoFavoritoDto?> CreateMapeoFavoritoAsync(MapeoFavoritoDto f)
        => await PostAsync<MapeoFavoritoDto>("/api/mapeo/favoritos", new {
            alias = f.Alias, direccion = f.Direccion, latitude = f.Latitude, longitude = f.Longitude,
            contactName = f.ContactName, telefono = f.Telefono, notas = f.Notas
        });
    public async Task<MapeoFavoritoDto?> UpdateMapeoFavoritoAsync(int id, MapeoFavoritoDto f)
        => await PutAsync<MapeoFavoritoDto>($"/api/mapeo/favoritos/{id}", new {
            alias = f.Alias, direccion = f.Direccion, latitude = f.Latitude, longitude = f.Longitude,
            contactName = f.ContactName, telefono = f.Telefono, notas = f.Notas, isActive = f.IsActive
        });
    public async Task<bool> DeleteMapeoFavoritoAsync(int id)
        => await DeleteAsync($"/api/mapeo/favoritos/{id}");

    // ===== Mapeo: Snapshots (historial de rutas) =====
    public async Task<List<MapeoSnapshotListItemDto>?> GetMapeoSnapshotsAsync(int days = 30)
        => await GetAsync<List<MapeoSnapshotListItemDto>>($"/api/mapeo/snapshots?days={days}");
    public async Task<object?> CreateMapeoSnapshotAsync(string? notes = null)
        => await PostAsync<object>("/api/mapeo/snapshots", new { notes });
    public async Task<bool> DeleteMapeoSnapshotAsync(int id)
        => await DeleteAsync($"/api/mapeo/snapshots/{id}");
    public async Task<object?> GetMapeoSnapshotDetailAsync(int id)
        => await GetAsync<object>($"/api/mapeo/snapshots/{id}");

    // ===== Mapeo: Stops (paradas a repartir) =====
    public async Task<List<MapeoStopDto>?> GetMapeoStopsAsync(int? driverId = null, string? internalStatus = null)
    {
        var qs = new List<string>();
        if (driverId.HasValue) qs.Add($"driverId={driverId.Value}");
        if (!string.IsNullOrWhiteSpace(internalStatus)) qs.Add($"internalStatus={Uri.EscapeDataString(internalStatus)}");
        var url = "/api/mapeo/stops" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<MapeoStopDto>>(url);
    }
    public async Task<MapeoStopDto?> CreateMapeoStopAsync(string origin, string? originRefId, string? alias,
        string direccion, decimal lat, decimal lng, string? contact, string? tel, string? notas)
        => await PostAsync<MapeoStopDto>("/api/mapeo/stops", new {
            origin, originRefId, alias, direccion, latitude = lat, longitude = lng,
            contactName = contact, telefono = tel, notas
        });
    public async Task<MapeoStopDto?> UpdateMapeoStopAsync(int id, object req)
        => await PutAsync<MapeoStopDto>($"/api/mapeo/stops/{id}", req);
    public async Task<bool> DeleteMapeoStopAsync(int id)
        => await DeleteAsync($"/api/mapeo/stops/{id}");
    public async Task<bool> ClearMapeoStopsAsync()
        => await DeleteAsync("/api/mapeo/stops");
    public async Task<object?> ImportFlexAsStopsAsync(string mode = "today")
        => await PostAsync<object>($"/api/mapeo/stops/import-flex?mode={Uri.EscapeDataString(mode)}", new { });
    public async Task<ImportFlexPreviewDto?> ImportFlexPreviewAsync(string mode = "today")
        => await GetAsync<ImportFlexPreviewDto>($"/api/mapeo/stops/import-flex-preview?mode={Uri.EscapeDataString(mode)}");

    public async Task<object?> AssignBulkStopsAsync(List<int> stopIds, int? driverId)
        => await PostAsync<object>("/api/mapeo/stops/assign-bulk", new { stopIds, driverId });

    public async Task<object?> AutoAssignStopsAsync(bool reassignAll = false)
        => await PostAsync<object>($"/api/mapeo/stops/auto-assign?reassignAll={reassignAll.ToString().ToLower()}", new { });

    public async Task<object?> OptimizeStopsOrderAsync(int? driverId = null, int? vehicleSlot = null)
    {
        var qs = new List<string>();
        if (driverId.HasValue && driverId.Value > 0) qs.Add($"driverId={driverId.Value}");
        if (vehicleSlot.HasValue && vehicleSlot.Value > 0) qs.Add($"vehicleSlot={vehicleSlot.Value}");
        var url = "/api/mapeo/stops/optimize-order" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await PostAsync<object>(url, new { });
    }

    public async Task<object?> AssignVehicleSlotAsync(int stopId, int? slot)
        => await PutAsync<object>($"/api/mapeo/stops/{stopId}/vehicle-slot", new { slot });

    public async Task<object?> ClearVehicleAssignmentsAsync()
        => await PostAsync<object>("/api/mapeo/stops/clear-vehicle-assignments", new { });

    public async Task<object?> AssignDriverToSlotAsync(int slot, int? driverId)
        => await PostAsync<object>("/api/mapeo/stops/assign-driver-to-slot", new { slot, driverId });

    // ===== MeLi Questions =====
    public async Task<MeliQuestionsUnreadDto?> GetMeliQuestionsUnreadCountAsync()
        => await GetAsync<MeliQuestionsUnreadDto>("/api/meli/questions/unread-count");

    public async Task<List<MeliQuestionDto>?> GetMeliQuestionsAsync(string status = "UNANSWERED")
        => await GetAsync<List<MeliQuestionDto>>($"/api/meli/questions?status={status}");

    public async Task<bool> AnswerMeliQuestionAsync(int id, string text)
    {
        var r = await PostAsync<object>($"/api/meli/questions/{id}/answer", new { text });
        return r is not null;
    }

    public async Task MarkMeliQuestionsSeenAsync()
    {
        await PostAsync<object>("/api/meli/questions/mark-seen", new { });
    }

    public async Task<object?> SyncMeliQuestionsNowAsync()
        => await PostAsync<object>("/api/meli/questions/sync-now", new { });

}
