using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Web.Models;

namespace Web.Services;

public class ApiClient
{
    private readonly HttpClient _http;
    // Cliente con timeout largo para operaciones lentas (ej: leer saldo de Shell,
    // que espera el token del mail). Mismo origen → la cookie de auth viaja igual.
    private readonly HttpClient _httpLong;
    private readonly AuthService _authService;
    private readonly NavigationManager _navigation;
    private readonly OperatorService _operator;

    // 2026-06-24: ultimo touch al timestamp de inactividad del operador. Throttle
    // para no escribir a localStorage en cada request — basta con refrescar 1 vez
    // por minuto para mantener la sesion viva mientras el usuario opera.
    private DateTime _lastOperatorTouchUtc = DateTime.MinValue;
    private const int OperatorTouchThrottleSeconds = 60;

    public ApiClient(HttpClient http, AuthService authService, NavigationManager navigation, OperatorService op)
    {
        _http = http;
        _httpLong = new HttpClient { BaseAddress = http.BaseAddress, Timeout = TimeSpan.FromMinutes(4) };
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

    // --- 2026-06-19: Refresco de sale_fee real (comision MeLi) ---
    public async Task<bool> RefreshMeliItemSaleFeeAsync(string meliItemId)
    {
        var resp = await _http.PostAsync($"/api/meli/items/{meliItemId}/refresh-salefee", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> RefreshMeliItemsSaleFeeBulkAsync()
    {
        var resp = await _http.PostAsync("/api/meli/items/refresh-salefee-bulk", null);
        return resp.IsSuccessStatusCode;
    }

    // --- 2026-07-06: Aplicar el codigo del compuesto/combo a sus publicaciones MeLi ---
    public async Task<(int total, int ok, string newSku)?> RenameComboMeliSkuAsync(int comboId)
    {
        var resp = await _http.PostAsync($"/api/cafe/combos/{comboId}/rename-meli-sku", null);
        if (!resp.IsSuccessStatusCode) return null;
        var el = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        int total = el.TryGetProperty("total", out var t) ? t.GetInt32() : 0;
        int ok = el.TryGetProperty("ok", out var o) ? o.GetInt32() : 0;
        string newSku = el.TryGetProperty("newSku", out var s) ? (s.GetString() ?? "") : "";
        return (total, ok, newSku);
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

    /// <summary>2026-07-04: emite la reserva como factura AFIP. Devuelve (ok, error).
    /// Opcionalmente permite override de la empresa emisora en el momento.</summary>
    public async Task<(bool ok, string? error)> EmitirArcaReservaAsync(int id, int? arcaWebserviceAccountId = null)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/alquileres/reservas/{id}/emitir-arca",
            new { arcaWebserviceAccountId });
        if (resp.IsSuccessStatusCode) return (true, null);
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorResp>();
            return (false, err?.Error ?? "No se pudo facturar");
        }
        catch { return (false, "No se pudo facturar"); }
    }

    /// <summary>Elimina una reserva. Requiere clave del usuario y falla si tiene cobranzas.
    /// Devuelve (ok, mensajeDeError).</summary>
    public async Task<(bool ok, string? error)> DeleteAlqReservaAsync(int id, string password)
    {
        await SetAuthHeaderAsync();
        var req = new HttpRequestMessage(HttpMethod.Delete, $"/api/alquileres/reservas/{id}")
        {
            Content = JsonContent.Create(new { password })
        };
        var resp = await _http.SendAsync(req);
        if (resp.IsSuccessStatusCode) return (true, null);
        try
        {
            var err = await resp.Content.ReadFromJsonAsync<ErrorResp>();
            return (false, err?.Error ?? "No se pudo eliminar");
        }
        catch { return (false, "No se pudo eliminar"); }
    }
    private record ErrorResp(string? Error);

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

    public async Task<AlqDashboardResumen?> GetAlqResumenDashboardAsync()
        => await GetAsync<AlqDashboardResumen>("/api/alquileres/reservas/resumen-dashboard");

    public async Task<(byte[]? bytes, string? error)> GetAlqReservaPdfAsync(int id)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync($"/api/alquileres/reservas/{id}/pdf");
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return (null, "Sesión expirada"); }
        if (resp.IsSuccessStatusCode) return (await resp.Content.ReadAsByteArrayAsync(), null);
        return (null, $"Error {(int)resp.StatusCode}");
    }

    /// <summary>2026-07-04: PDF de la FACTURA AFIP (sobria, con CAE) de una reserva facturada.</summary>
    public async Task<(byte[]? bytes, string? error)> GetAlqFacturaPdfAsync(int id)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync($"/api/alquileres/reservas/{id}/factura-pdf");
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return (null, "Sesión expirada"); }
        if (resp.IsSuccessStatusCode) return (await resp.Content.ReadAsByteArrayAsync(), null);
        try { var err = await resp.Content.ReadFromJsonAsync<ErrorResp>(); return (null, err?.Error ?? $"Error {(int)resp.StatusCode}"); }
        catch { return (null, $"Error {(int)resp.StatusCode}"); }
    }

    // ===== Alquileres: Repartidor / Cobranzas pendientes (2026-06-26) =====
    /// <summary>URL (relativa) del PNG del QR de la reserva, para usar directo en un &lt;img src&gt;.</summary>
    public string AlqReservaQrUrl(int reservaId) => $"/api/alquileres/reservas/{reservaId}/qr";

    public async Task<List<AlqCobranzaPendienteDto>?> GetAlqCobranzasPendientesAsync(string? estado = null, int? repartidorId = null)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(estado)) qs.Add($"estado={estado}");
        if (repartidorId.HasValue) qs.Add($"repartidorId={repartidorId}");
        var url = "/api/alquileres/cobranzas-pendientes" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<AlqCobranzaPendienteDto>>(url);
    }

    public async Task<int> GetCountAlqCobranzasPendientesAsync()
    {
        var r = await GetAsync<CountResultDto>("/api/alquileres/cobranzas-pendientes/count-pendientes");
        return r?.Count ?? 0;
    }

    public async Task<bool> AprobarAlqCobranzaAsync(int id, string? operador)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/alquileres/cobranzas-pendientes/{id}/aprobar", new { operador });
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Anula un cobro de alquiler ya aprobado (pide clave). Devuelve (ok, error).</summary>
    public async Task<(bool ok, string? error)> AnularAlqCobranzaAsync(int id, string password, string? operador)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/alquileres/cobranzas-pendientes/{id}/anular", new { password, operador });
        if (resp.IsSuccessStatusCode) return (true, null);
        try { var e = await resp.Content.ReadFromJsonAsync<ErrorResp>(); return (false, e?.Error ?? "No se pudo anular"); }
        catch { return (false, "No se pudo anular"); }
    }

    public async Task<bool> RechazarAlqCobranzaAsync(int id, string? motivo, string? operador)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/alquileres/cobranzas-pendientes/{id}/rechazar", new { motivo, operador });
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Devuelve una cobranza de alquiler RECHAZADA a PENDIENTE (se rechazó por error).</summary>
    public async Task<bool> RestaurarAlqCobranzaAsync(int id, string? operador)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/alquileres/cobranzas-pendientes/{id}/restaurar", new { operador });
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Asigna una reserva a un repartidor desde el panel admin (o la deja sin asignar si es null).</summary>
    public async Task<bool> AsignarRepartoAlqAsync(int reservaId, int? nuevoRepartidorId)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/alquileres/reservas/{reservaId}/asignar-reparto", new { nuevoRepartidorId });
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Deshace la entrega o el retiro marcado por un repartidor (tipo = "entrega" | "retiro").</summary>
    public async Task<bool> LimpiarRepartoAlqAsync(int reservaId, string tipo)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/alquileres/reservas/{reservaId}/limpiar-reparto", new { tipo });
        return resp.IsSuccessStatusCode;
    }

    // --- Avisos / novedades (globito de bienvenida) ---
    public async Task<bool> GetWelcomeNoticeAsync()
    {
        try
        {
            var r = await GetAsync<WelcomeNoticeStatus>("/api/notices/welcome");
            return r?.Show ?? false;
        }
        catch { return false; }
    }

    public async Task<bool> DismissWelcomeNoticeAsync()
    {
        try
        {
            await SetAuthHeaderAsync();
            var resp = await _http.PostAsync("/api/notices/welcome/dismiss", null);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    public record WelcomeNoticeStatus(bool Show);

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

    /// <summary>Trae los datos del panel '¿Cuanto debo pagar?' — empleados con saldo,
    /// agrupados con sus liquidaciones pendientes y conceptos.</summary>
    public async Task<DashboardDeudasDto?> GetNomDashboardDeudasAsync()
        => await GetAsync<DashboardDeudasDto>("/api/nominas/dashboard/deudas");

    /// <summary>Devuelve (y crea si no existe) el token publico del panel.</summary>
    public async Task<string?> GetNomPanelPublicTokenAsync()
    {
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync("/api/nominas/dashboard/public-token");
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        if (!resp.IsSuccessStatusCode) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("token", out var t)) return t.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Regenera el token publico del panel (invalida el anterior).</summary>
    public async Task<string?> RegenerateNomPanelPublicTokenAsync()
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsync("/api/nominas/dashboard/public-token/regenerate", null);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        if (!resp.IsSuccessStatusCode) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("token", out var t)) return t.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Version PUBLICA (sin login) del dashboard de deudas. Token en la URL.</summary>
    public async Task<DashboardDeudasDto?> GetNomDashboardDeudasPublicAsync(string token)
    {
        var resp = await _http.GetAsync($"/api/nominas/dashboard/publica/{Uri.EscapeDataString(token)}/deudas");
        if (!resp.IsSuccessStatusCode) return null;
        try { return await resp.Content.ReadFromJsonAsync<DashboardDeudasDto>(); }
        catch { return null; }
    }

    /// <summary>Version PUBLICA (sin login) de pagar — token en URL + clave global en body.</summary>
    public async Task<(bool ok, string? error)> NomDashboardPagarPublicAsync(string token, DashboardPagarRequest req)
    {
        var resp = await _http.PostAsJsonAsync($"/api/nominas/dashboard/publica/{Uri.EscapeDataString(token)}/pagar", req);
        if (resp.IsSuccessStatusCode) return (true, null);
        var content = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var err)) return (false, err.GetString());
        }
        catch { }
        return (false, content);
    }

    /// <summary>Registra un pago desde el panel — siempre requiere operador + clave.</summary>
    public async Task<(bool ok, string? error)> NomDashboardPagarAsync(DashboardPagarRequest req)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("/api/nominas/dashboard/pagar", req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return (false, "Sesión expirada"); }
        if (resp.IsSuccessStatusCode) return (true, null);
        var content = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var err)) return (false, err.GetString());
        }
        catch { }
        return (false, content);
    }

    /// <summary>Descarga un Excel multi-hoja con resumen, pendientes, pagos detallados, por empleado y por mes.
    /// Filtros: rango de meses YYYYMM + ids de empleados (vacio = todos) + soloPendientes.</summary>
    public async Task<byte[]?> ExportNomLiquidacionesExcelAsync(int? desdeYYYYMM, int? hastaYYYYMM, List<int>? empleadoIds, bool soloPendientes)
    {
        await SetAuthHeaderAsync();
        var body = new { DesdeYYYYMM = desdeYYYYMM, HastaYYYYMM = hastaYYYYMM, EmpleadoIds = empleadoIds, SoloPendientes = soloPendientes };
        var resp = await _http.PostAsJsonAsync("/api/nominas/liquidaciones/export", body);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Edita un pago de liquidacion. Requiere operador + clave (los mismos que
    /// para borrar comprobantes del modulo Cafe).</summary>
    public async Task<(NomLiquidacionDto? liq, string? error)> UpdateNomPagoAsync(int id, UpdateNomPagoRequest req)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PutAsJsonAsync($"/api/nominas/pagos/{id}", req);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (null, "Sesión expirada");
        }
        var content = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode)
        {
            try
            {
                return (System.Text.Json.JsonSerializer.Deserialize<NomLiquidacionDto>(content,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }), null);
            }
            catch { return (null, "No se pudo leer la respuesta"); }
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var err)) return (null, err.GetString());
        }
        catch { }
        return (null, content);
    }

    public async Task<NomResumenMensualDto?> GetNomResumenAsync(int anio, int mes)
        => await GetAsync<NomResumenMensualDto>($"/api/nominas/resumen?anio={anio}&mes={mes}");

    // --- 2026-07-01: Nominas: archivos adjuntos (recibos / nóminas) por liquidación ---
    public async Task<List<NomNominaArchivoDto>?> GetNominaArchivosAsync(int liqId)
        => await GetAsync<List<NomNominaArchivoDto>>($"/api/nominas/liquidaciones/{liqId}/archivos");

    public async Task<(NomNominaArchivoDto? archivo, string? error)> UploadNominaArchivoAsync(int liqId, string fileName, string contentType, string base64)
    {
        await SetAuthHeaderAsync();
        var body = new { FileName = fileName, ContentType = contentType, Base64 = base64 };
        var resp = await _http.PostAsJsonAsync($"/api/nominas/liquidaciones/{liqId}/archivos", body);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return (null, "Sesión expirada"); }
        var content = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode)
        {
            try
            {
                return (System.Text.Json.JsonSerializer.Deserialize<NomNominaArchivoDto>(content,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }), null);
            }
            catch { return (null, "No se pudo leer la respuesta"); }
        }
        try { using var doc = System.Text.Json.JsonDocument.Parse(content); if (doc.RootElement.TryGetProperty("error", out var err)) return (null, err.GetString()); }
        catch { }
        return (null, "No se pudo subir el archivo");
    }

    public async Task<byte[]?> DownloadNominaArchivoAsync(int liqId, int archivoId)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync($"/api/nominas/liquidaciones/{liqId}/archivos/{archivoId}/download");
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync();
    }

    public async Task<bool> DeleteNominaArchivoAsync(int liqId, int archivoId)
        => await DeleteAsync($"/api/nominas/liquidaciones/{liqId}/archivos/{archivoId}");

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

    public Task<List<string>?> ListVaultCategoriasAsync(string token)
        => VaultRequestAsync<List<string>>(HttpMethod.Get, "/api/vault/categorias", null, token);

    public async Task<byte[]?> DownloadVaultTemplateAsync()
    {
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync("/api/vault/template-excel");
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadAsByteArrayAsync();
    }

    public async Task<VaultImportResultDto?> ImportVaultExcelAsync(string token, Stream fileStream, string fileName)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(fileStream);
        streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(streamContent, "file", fileName);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/vault/import-excel");
        req.Content = content;
        req.Headers.Add("X-Vault-Token", token);

        using var resp = await _http.SendAsync(req);
        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new VaultLockedException("Bóveda bloqueada");
        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync();
            throw new Exception(string.IsNullOrEmpty(err) ? $"Error {(int)resp.StatusCode}" : err);
        }
        return await resp.Content.ReadFromJsonAsync<VaultImportResultDto>();
    }

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

    // --- Chat interno entre usuarios (2026-07-02) ---
    public async Task<ChatConversacionesDto?> GetChatConversacionesAsync()
        => await GetAsync<ChatConversacionesDto>("/api/chat/conversaciones");

    public async Task<List<ChatMensajeDto>?> GetChatMensajesAsync(string con)
        => await GetAsync<List<ChatMensajeDto>>($"/api/chat/mensajes?con={Uri.EscapeDataString(con)}");

    public async Task<ChatMensajeDto?> EnviarChatAsync(EnviarChatRequest request)
        => await PostAsync<ChatMensajeDto>("/api/chat/enviar", request);

    /// <summary>2026-07-06: enviar un mensaje con adjunto (foto/archivo/audio) por multipart.</summary>
    public async Task<ChatMensajeDto?> EnviarChatAdjuntoAsync(int? paraUserId, string? cuerpo, string? firma,
        Stream fileStream, string fileName, string contentType)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        var sc = new StreamContent(fileStream);
        if (!string.IsNullOrEmpty(contentType))
            sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
        content.Add(sc, "archivo", string.IsNullOrEmpty(fileName) ? "adjunto" : fileName);
        if (paraUserId.HasValue) content.Add(new StringContent(paraUserId.Value.ToString()), "paraUserId");
        if (!string.IsNullOrWhiteSpace(cuerpo)) content.Add(new StringContent(cuerpo), "cuerpo");
        if (!string.IsNullOrWhiteSpace(firma)) content.Add(new StringContent(firma), "firma");
        var resp = await _http.PostAsync("/api/chat/enviar-adjunto", content);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ChatMensajeDto>();
    }

    public async Task<ChatNoLeidosDto?> GetChatNoLeidosAsync()
        => await GetAsync<ChatNoLeidosDto>("/api/chat/no-leidos");

    // --- Visibilidad granular del sidebar por rol (2026-05-28) ---
    public async Task<Dictionary<string, List<string>>?> GetMenuVisibilityAsync()
        => await GetAsync<Dictionary<string, List<string>>>("/api/menu-visibility");

    public async Task<bool> SetMenuVisibilityAsync(string role, string key, bool enabled)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("/api/menu-visibility/set", new { role, key, enabled });
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Verifica si la password coincide con la del usuario logueado. Usado para
    /// proteger acciones sensibles (ej: activar modo edición del sidebar).</summary>
    public async Task<bool> VerifyMyPasswordAsync(string password)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("/api/auth/verify-password", new { password });
        if (!resp.IsSuccessStatusCode) return false;
        var data = await resp.Content.ReadFromJsonAsync<VerifyPasswordResponse>();
        return data?.ok ?? false;
    }
    private record VerifyPasswordResponse(bool ok);

    // --- Asistente ---
    public async Task<AssistantChatResponse?> AssistantChatAsync(List<AssistantChatMessage> messages)
        => await PostAsync<AssistantChatResponse>("/api/assistant/chat", new AssistantChatRequest { Messages = messages });

    // --- Cafe: Saldos migracion (saldos del sistema viejo a matchear con clientes) ---
    public async Task<List<CafeSaldoMigracionDto>?> GetCafeSaldosMigracionAsync(string? estado = null, string? q = null)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(estado)) qs.Add($"estado={Uri.EscapeDataString(estado)}");
        if (!string.IsNullOrEmpty(q)) qs.Add($"q={Uri.EscapeDataString(q)}");
        var url = "/api/cafe/saldos-migracion" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<CafeSaldoMigracionDto>>(url);
    }
    public async Task<CafeSaldosMigracionStatsDto?> GetCafeSaldosMigracionStatsAsync()
        => await GetAsync<CafeSaldosMigracionStatsDto>("/api/cafe/saldos-migracion/stats");
    public async Task<object?> ImportarSaldosMigracionAsync(List<CafeSaldoMigracionImportItem> items, bool reemplazarPendientes)
        => await PostAsync<object>("/api/cafe/saldos-migracion/import", new { items, reemplazarPendientes });
    public async Task<CafeSaldoMigracionAsociarResultDto?> AsociarSaldoMigracionAsync(int id, int clienteId, DateTime? fechaVenta, string? notas)
        => await PostAsync<CafeSaldoMigracionAsociarResultDto>($"/api/cafe/saldos-migracion/{id}/asociar", new { clienteId, fechaVenta, notasInternas = notas });
    public async Task<bool> IgnorarSaldoMigracionAsync(int id, string? notas)
        => await PostAsync<object>($"/api/cafe/saldos-migracion/{id}/ignorar", new { notas }) is not null;
    public async Task<bool> ReactivarSaldoMigracionAsync(int id)
        => await PostAsync<object>($"/api/cafe/saldos-migracion/{id}/reactivar", new { }) is not null;
    public async Task<List<CafeSaldoMigracionSugerenciaDto>?> GetSaldoMigracionSugerenciasAsync(int id)
        => await GetAsync<List<CafeSaldoMigracionSugerenciaDto>>($"/api/cafe/saldos-migracion/{id}/sugerencias");
    public async Task<bool> DeleteSaldoMigracionAsync(int id)
        => await DeleteAsync($"/api/cafe/saldos-migracion/{id}");

    // --- Cafe: Comodatos / Máquinas (comodato + financiada) ---
    public async Task<List<CafeComodatoDto>?> GetCafeComodatosAsync(string? modalidad = null, string? estado = null, int? clienteId = null, string? q = null)
    {
        var qs = new List<string>();
        if (!string.IsNullOrEmpty(modalidad)) qs.Add($"modalidad={Uri.EscapeDataString(modalidad)}");
        if (!string.IsNullOrEmpty(estado)) qs.Add($"estado={Uri.EscapeDataString(estado)}");
        if (clienteId.HasValue) qs.Add($"clienteId={clienteId}");
        if (!string.IsNullOrEmpty(q)) qs.Add($"q={Uri.EscapeDataString(q)}");
        var url = "/api/cafe/comodatos" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<CafeComodatoDto>>(url);
    }
    public async Task<CafeComodatoDetalleDto?> GetCafeComodatoDetalleAsync(int id)
        => await GetAsync<CafeComodatoDetalleDto>($"/api/cafe/comodatos/{id}");
    public async Task<CafeComodatosStatsDto?> GetCafeComodatosStatsAsync()
        => await GetAsync<CafeComodatosStatsDto>("/api/cafe/comodatos/stats");
    public async Task<CafeComodatoDto?> CreateCafeComodatoAsync(CafeComodatoCreateRequest req)
        => await PostAsync<CafeComodatoDto>("/api/cafe/comodatos", req);
    public async Task<CafeComodatoDto?> UpdateCafeComodatoAsync(int id, CafeComodatoUpdateRequest req)
        => await PutAsync<CafeComodatoDto>($"/api/cafe/comodatos/{id}", req);
    public async Task<bool> DeleteCafeComodatoAsync(int id)
        => await DeleteAsync($"/api/cafe/comodatos/{id}");
    public async Task<object?> RegistrarPagoComodatoAsync(int id, CafeComodatoPagoRequest req)
        => await PostAsync<object>($"/api/cafe/comodatos/{id}/pagos", req);
    public async Task<bool> AnularPagoComodatoAsync(int id, int pagoId)
        => await DeleteAsync($"/api/cafe/comodatos/{id}/pagos/{pagoId}");

    /// <summary>Descarga el PDF del comprobante de Comodato / Financiación para que firme el cliente.</summary>
    public async Task<(byte[]? bytes, string? error)> GetCafeComodatoComprobantePdfAsync(int id)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync($"/api/cafe/comodatos/{id}/comprobante.pdf");
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (null, "Sesión expirada");
        }
        if (resp.IsSuccessStatusCode)
            return (await resp.Content.ReadAsByteArrayAsync(), null);
        return (null, $"Error {resp.StatusCode}");
    }

    // --- Cafe: Clientes ---
    public async Task<List<CafeClienteDto>?> GetCafeClientesAsync()
        => await GetAsync<List<CafeClienteDto>>("/api/cafe/clientes");

    public async Task<CafeClienteDto?> CreateCafeClienteAsync(CreateCafeClienteRequest request)
        => await PostAsync<CafeClienteDto>("/api/cafe/clientes", request);

    public async Task<CafeClienteDto?> UpdateCafeClienteAsync(int id, UpdateCafeClienteRequest request)
        => await PutAsync<CafeClienteDto>($"/api/cafe/clientes/{id}", request);

    public async Task<bool> DeleteCafeClienteAsync(int id)
        => await DeleteAsync($"/api/cafe/clientes/{id}");

    /// <summary>Asigna un código interno correlativo al cliente (max + 1).</summary>
    public async Task<CafeClienteDto?> AsignarCodigoInternoAsync(int id)
        => await PostAsync<CafeClienteDto>($"/api/cafe/clientes/{id}/asignar-codigo-interno", new { });

    /// <summary>Devuelve el próximo código interno disponible (MAX + 1) SIN asignarlo.
    /// Usado para mostrar el código antes de guardar un cliente nuevo.</summary>
    public async Task<int?> GetNextCodigoInternoAsync()
    {
        var resp = await GetAsync<NextCodigoInternoResponse>("/api/cafe/clientes/next-codigo-interno");
        return resp?.CodigoInterno;
    }
    private class NextCodigoInternoResponse { public int CodigoInterno { get; set; } }

    /// <summary>Re-extrae las coordenadas a partir del MapeoLink (si falló la primera vez).</summary>
    public async Task<CafeClienteDto?> ReExtraerCoordsAsync(int id)
        => await PostAsync<CafeClienteDto>($"/api/cafe/clientes/{id}/reextraer-coords", new { });

    /// <summary>Saca el código interno (vuelve a null).</summary>
    public async Task<CafeClienteDto?> QuitarCodigoInternoAsync(int id)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.DeleteAsync($"/api/cafe/clientes/{id}/codigo-interno");
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync();
        return System.Text.Json.JsonSerializer.Deserialize<CafeClienteDto>(body, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

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

    /// <summary>2026-07-07: busca ventas por texto libre en TODO el historial (nombre, nro, CUIT,
    /// telefono, direccion, etc.). El backend ignora paginacion y fechas, y devuelve hasta 500
    /// coincidencias. Usado por el buscador del listado para que encuentre clientes viejos sin
    /// tener que ampliar el rango a mano.</summary>
    public async Task<List<CafeVentaDto>?> BuscarCafeVentasAsync(string search)
    {
        var url = "/api/cafe/ventas?search=" + Uri.EscapeDataString(search);
        return await GetAsync<List<CafeVentaDto>>(url);
    }

    /// <summary>2026-06-22: variante paginada. Devuelve (lista, totalCount). totalCount viene en
    /// el header X-Total-Count del response. Si se pasan fechas, el backend ignora la paginacion
    /// y trae todo el rango (totalCount queda = lista.Count en ese caso).</summary>
    public async Task<(List<CafeVentaDto> Items, int TotalCount)> GetCafeVentasPagedAsync(
        int limit = 50, int offset = 0, DateTime? from = null, DateTime? to = null)
    {
        var qs = new List<string> { $"limit={limit}", $"offset={offset}" };
        if (from.HasValue) qs.Add($"from={from.Value:yyyy-MM-dd}");
        if (to.HasValue) qs.Add($"to={to.Value:yyyy-MM-dd}");
        var url = "/api/cafe/ventas?" + string.Join("&", qs);
        try
        {
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return (new(), 0);
            var items = await resp.Content.ReadFromJsonAsync<List<CafeVentaDto>>() ?? new();
            int total = items.Count;
            if (resp.Headers.TryGetValues("X-Total-Count", out var vals))
            {
                var v = vals.FirstOrDefault();
                if (int.TryParse(v, out var n)) total = n;
            }
            return (items, total);
        }
        catch { return (new(), 0); }
    }

    /// <summary>2026-06-24: trae TODAS las ventas impagas (saldo > 0) de un cliente, sin paginar.
    /// Usado por el formulario de Nueva Venta para calcular el saldo anterior sin depender de la
    /// lista paginada (que solo tiene 50 ventas en memoria y se perdia las deudas viejas).</summary>
    public async Task<List<CafeVentaDto>?> GetCafeVentasImpagasClienteAsync(int clienteId, int? excludeVentaId = null)
    {
        var url = $"/api/cafe/ventas/cliente/{clienteId}/impagas";
        if (excludeVentaId.HasValue) url += $"?excludeVentaId={excludeVentaId.Value}";
        return await GetAsync<List<CafeVentaDto>>(url);
    }

    public async Task<CafeVentaDto?> GetCafeVentaAsync(int id)
        => await GetAsync<CafeVentaDto>($"/api/cafe/ventas/{id}");

    /// <summary>Setea / borra la nota pin de una venta. Pasar null o vacío para borrar.</summary>
    public async Task<bool> UpdateCafeVentaPinNotaAsync(int id, string? nota)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PutAsJsonAsync($"/api/cafe/ventas/{id}/pin", new { Nota = nota });
        return resp.IsSuccessStatusCode;
    }

    // === Extracto Bancario (2026-05-19) ===
    public async Task<SaldoBancoDto?> GetSaldoBancoAsync()
        => await GetAsync<SaldoBancoDto>("/api/cafe/extracto-banco/saldo");

    public async Task<List<ExtractoMovimientoDto>?> GetExtractoMovimientosAsync(
        DateTime? desde = null, DateTime? hasta = null, string? tipo = null, string? asociado = null)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(tipo)) qs.Add("tipo=" + Uri.EscapeDataString(tipo));
        if (!string.IsNullOrEmpty(asociado)) qs.Add("asociado=" + asociado);
        var url = "/api/cafe/extracto-banco" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<ExtractoMovimientoDto>>(url);
    }

    public async Task<(List<ImportExtractoResultDto>? result, string? error)> ImportExtractoAsync(IEnumerable<(string name, Stream stream)> archivos)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        foreach (var f in archivos)
        {
            var sc = new StreamContent(f.stream);
            sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            content.Add(sc, "files", f.name);
        }
        var resp = await _http.PostAsync("/api/cafe/extracto-banco/import", content);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            return (null, $"HTTP {(int)resp.StatusCode}: {body}");
        }
        var data = await resp.Content.ReadFromJsonAsync<List<ImportExtractoResultDto>>();
        return (data, null);
    }

    public async Task<bool> AsociarMovimientoExtractoAsync(int id, int ventaId, string? operador = null)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/cafe/extracto-banco/{id}/asociar",
            new AsociarMovimientoRequest { VentaId = ventaId, Operador = operador });
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DesasociarMovimientoExtractoAsync(int id)
        => await DeleteAsync($"/api/cafe/extracto-banco/{id}/asociar");

    public async Task<List<MovimientoDisponibleDto>?> GetMovimientosAsociadosSinCobrarAsync(int clienteId)
        => await GetAsync<List<MovimientoDisponibleDto>>($"/api/cafe/extracto-banco/asociados-sin-cobrar/{clienteId}");

    public async Task<bool> MarcarMovimientosUsadosAsync(List<int> ids, int cobranzaId)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("/api/cafe/extracto-banco/marcar-usados",
            new MarcarMovimientosUsadosRequest { MovimientoIds = ids, CobranzaId = cobranzaId });
        return resp.IsSuccessStatusCode;
    }

    // ─── 2026-06-03: Descartar/restaurar movimiento bancario por cliente ──────
    public async Task<bool> DescartarMovimientoParaClienteAsync(int movId, int clienteId, string? operador = null)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/cafe/extracto-banco/{movId}/descartar-cliente/{clienteId}", new { Operador = operador });
        return resp.IsSuccessStatusCode;
    }
    public async Task<bool> RestaurarMovimientoParaClienteAsync(int movId, int clienteId)
        => await DeleteAsync($"/api/cafe/extracto-banco/{movId}/descartar-cliente/{clienteId}");

    public record MovDescartadoDto(int MovimientoId, DateTime Fecha, string? Descripcion, decimal Importe, DateTime DescartadoAt, string? DescartadoPor);
    public async Task<List<MovDescartadoDto>?> GetMovimientosDescartadosParaClienteAsync(int clienteId)
        => await GetAsync<List<MovDescartadoDto>>($"/api/cafe/extracto-banco/descartados/{clienteId}");

    // === Repartidores (admin) ===
    public async Task<List<RepartidorDto>?> GetRepartidoresAsync()
        => await GetAsync<List<RepartidorDto>>("/api/cafe/repartidores");
    public async Task<RepartidorDto?> CrearRepartidorAsync(CrearRepartidorRequest req)
        => await PostAsync<RepartidorDto>("/api/cafe/repartidores", req);
    public async Task<bool> EditarRepartidorAsync(int id, EditarRepartidorRequest req)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PutAsJsonAsync($"/api/cafe/repartidores/{id}", req);
        return resp.IsSuccessStatusCode;
    }
    public async Task<bool> BorrarRepartidorAsync(int id)
        => await DeleteAsync($"/api/cafe/repartidores/{id}");
    public record RegenerarTokenResp(string PublicToken, int SesionesRevocadas);
    public async Task<RegenerarTokenResp?> RegenerarRepartidorTokenAsync(int id)
        => await PostAsync<RegenerarTokenResp>($"/api/cafe/repartidores/{id}/regenerar-public-token", new {});

    public record QrEscaneoDto(int Id, int VentaId, string? VentaNumero, int RepartidorId,
        string RepartidorNombre, string Accion, DateTime CreatedAt, string? Ip);
    public async Task<List<QrEscaneoDto>?> GetQrEscaneosAsync(int? repartidorId = null, int dias = 30)
    {
        var qs = new List<string> { $"dias={dias}" };
        if (repartidorId.HasValue) qs.Add($"repartidorId={repartidorId.Value}");
        return await GetAsync<List<QrEscaneoDto>>($"/api/cafe/repartidores/qr-escaneos?{string.Join("&", qs)}");
    }

    public async Task<bool> ReasignarEscaneoAsync(int ventaId, int? nuevoRepartidorId,
        bool esRetira = false, bool marcarEntregada = false,
        DateTime? fechaEntrega = null, string? comentarioEntrega = null)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("/api/cafe/repartidores/qr-escaneos/reasignar",
            new
            {
                VentaId = ventaId,
                NuevoRepartidorId = nuevoRepartidorId,
                EsRetira = esRetira,
                MarcarEntregada = marcarEntregada,
                FechaEntrega = fechaEntrega,
                ComentarioEntrega = comentarioEntrega
            });
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> DesmarcarEntregaAsync(int ventaId)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsync($"/api/cafe/repartidores/ventas/{ventaId}/desmarcar-entrega", null);
        return resp.IsSuccessStatusCode;
    }

    // === Cobranzas pendientes (admin) ===
    public async Task<List<CobranzaPendienteDto>?> GetCobranzasPendientesAsync(string estado = "PENDIENTE",
        int? repartidorId = null, DateTime? desde = null, DateTime? hasta = null)
    {
        var url = $"/api/cafe/cobranzas-pendientes?estado={Uri.EscapeDataString(estado)}";
        if (repartidorId.HasValue && repartidorId.Value > 0) url += $"&repartidorId={repartidorId.Value}";
        if (desde.HasValue) url += $"&desde={desde.Value:yyyy-MM-dd}";
        if (hasta.HasValue) url += $"&hasta={hasta.Value:yyyy-MM-dd}";
        return await GetAsync<List<CobranzaPendienteDto>>(url);
    }

    public string BuildCobranzasPendientesExportUrl(string formato, string estado,
        int? repartidorId, DateTime? desde, DateTime? hasta)
    {
        var url = $"/api/cafe/cobranzas-pendientes/export-{formato}?estado={Uri.EscapeDataString(estado)}";
        if (repartidorId.HasValue && repartidorId.Value > 0) url += $"&repartidorId={repartidorId.Value}";
        if (desde.HasValue) url += $"&desde={desde.Value:yyyy-MM-dd}";
        if (hasta.HasValue) url += $"&hasta={hasta.Value:yyyy-MM-dd}";
        return url;
    }

    public async Task<(byte[]? bytes, string? error)> DownloadCobranzasPendientesAsync(string formato, string estado,
        int? repartidorId, DateTime? desde, DateTime? hasta)
    {
        var url = BuildCobranzasPendientesExportUrl(formato, estado, repartidorId, desde, hasta);
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync(url);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return (null, "Sesión expirada"); }
        if (!resp.IsSuccessStatusCode) return (null, $"Error {(int)resp.StatusCode}");
        return (await resp.Content.ReadAsByteArrayAsync(), null);
    }
    public async Task<int> GetCountCobranzasPendientesAsync()
    {
        var r = await GetAsync<CountResultDto>("/api/cafe/cobranzas-pendientes/count-pendientes");
        return r?.Count ?? 0;
    }
    public record CountResultDto(int Count);

    // 2026-06-18: dropdown hover de la topbar — próximos cheques EMITIDOS por pagar.
    public record ChequeProximoDto(int Id, string Numero, DateTime? FechaPago, decimal Importe,
        string? ContraparteNombre, string? Motivo, string Estado);
    public async Task<List<ChequeProximoDto>> GetChequesProximosPagarAsync(int take = 5)
        => await GetAsync<List<ChequeProximoDto>>($"/api/cafe/cheques-banco/proximos-pagar?take={take}") ?? new();
    public async Task<bool> AprobarCobranzaPendienteAsync(int id, AprobarCobranzaPendienteRequest req)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/cafe/cobranzas-pendientes/{id}/aprobar", req);
        return resp.IsSuccessStatusCode;
    }
    public async Task<CobranzaPendienteDto?> GetCobranzaPendienteAsync(int id)
        => await GetAsync<CobranzaPendienteDto>($"/api/cafe/cobranzas-pendientes/{id}");
    public async Task<bool> VincularCobranzaPendienteAsync(int pendienteId, int cobranzaId, string? operador = null)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/cafe/cobranzas-pendientes/{pendienteId}/vincular",
            new { CobranzaId = cobranzaId, Operador = operador });
        return resp.IsSuccessStatusCode;
    }
    public async Task<bool> RechazarCobranzaPendienteAsync(int id, RechazarCobranzaPendienteRequest req)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/cafe/cobranzas-pendientes/{id}/rechazar", req);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Devuelve una cobranza de venta RECHAZADA a PENDIENTE (se rechazó por error).</summary>
    public async Task<bool> RestaurarCobranzaPendienteAsync(int id, string? operador)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/cafe/cobranzas-pendientes/{id}/restaurar", new { operador });
        return resp.IsSuccessStatusCode;
    }
    public async Task<ArqueoDto?> GetArqueoRepartidorAsync(int repartidorId, DateTime? fecha = null)
    {
        var url = $"/api/cafe/cobranzas-pendientes/arqueo/{repartidorId}"
            + (fecha.HasValue ? $"?fecha={fecha.Value:yyyy-MM-dd}" : "");
        return await GetAsync<ArqueoDto>(url);
    }

    public async Task<List<ArqueoDto>?> GetArqueoTodosAsync(DateTime? fecha = null)
    {
        var url = "/api/cafe/cobranzas-pendientes/arqueo/todos"
            + (fecha.HasValue ? $"?fecha={fecha.Value:yyyy-MM-dd}" : "");
        return await GetAsync<List<ArqueoDto>>(url);
    }

    // === Calendario Notas (2026-05-19) ===
    public async Task<List<CalendarioNotaDto>?> GetCalendarioNotasAsync(DateTime? desde = null, DateTime? hasta = null)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        var url = "/api/cafe/calendario/notas" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<CalendarioNotaDto>>(url);
    }

    public async Task<CalendarioNotaDto?> CrearCalendarioNotaAsync(CrearCalendarioNotaRequest req)
        => await PostAsync<CalendarioNotaDto>("/api/cafe/calendario/notas", req);

    public async Task<bool> BorrarCalendarioNotaAsync(int id)
        => await DeleteAsync($"/api/cafe/calendario/notas/{id}");

    // === Cheques Banco (2026-05-19) ===
    public async Task<ChequesBancoStatsDto?> GetChequesBancoStatsAsync()
        => await GetAsync<ChequesBancoStatsDto>("/api/cafe/cheques-banco/stats");

    public async Task<List<ChequeBancoDto>?> GetChequesBancoAsync(string? tipo = null, string? estado = null, string? q = null)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(tipo)) qs.Add("tipo=" + Uri.EscapeDataString(tipo));
        if (!string.IsNullOrWhiteSpace(estado)) qs.Add("estado=" + Uri.EscapeDataString(estado));
        if (!string.IsNullOrWhiteSpace(q)) qs.Add("q=" + Uri.EscapeDataString(q));
        var url = "/api/cafe/cheques-banco" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<ChequeBancoDto>>(url);
    }

    /// <summary>Clientes cuyo CUIT matchea el del librador del e-cheq. Vacio si no hay match.</summary>
    public async Task<List<SugerenciaClienteEcheqDto>?> GetClienteSugeridoECheqAsync(int echeqId)
        => await GetAsync<List<SugerenciaClienteEcheqDto>>($"/api/cafe/cheques-banco/{echeqId}/cliente-sugerido");

    /// <summary>Asocia el e-cheq a una cobranza nueva del cliente, imputando a las facturas elegidas.
    /// Devuelve (cobranzaId, numero, error).</summary>
    public async Task<(int? cobranzaId, string? numero, string? error)> AsociarECheqACobranzaAsync(int echeqId, AsociarECheqRequest req)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/api/cafe/cheques-banco/{echeqId}/asociar-cobranza", req);
            if (resp.IsSuccessStatusCode)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var root = doc.RootElement;
                int? cid = root.TryGetProperty("cobranzaId", out var c) ? c.GetInt32() : null;
                string? num = root.TryGetProperty("numero", out var n) ? n.GetString() : null;
                return (cid, num, null);
            }
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (null, null, err);
        }
        catch (Exception ex) { return (null, null, ex.Message); }
    }

    public async Task<List<ImportChequeBancoResultDto>?> ImportChequesBancoAsync(IEnumerable<(string name, Stream stream)> archivos)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        foreach (var f in archivos)
        {
            var sc = new StreamContent(f.stream);
            sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            content.Add(sc, "files", f.name);
        }
        var resp = await _http.PostAsync("/api/cafe/cheques-banco/import", content);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<List<ImportChequeBancoResultDto>>();
    }

    // === Preparacion de Pedidos (2026-05-19) ===
    public async Task<List<CafePreparacionVentaDto>?> GetCafePreparacionAsync(int dias = 7)
        => await GetAsync<List<CafePreparacionVentaDto>>($"/api/cafe/ventas/preparacion?dias={dias}");

    public async Task<bool> CambiarEstadoPreparacionAsync(int id, CafeCambiarEstadoPreparacionRequest req)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PatchAsJsonAsync($"/api/cafe/ventas/{id}/estado-preparacion", req);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Oculta UNA venta del tablero de Preparacion (la venta y el PDF en Drive siguen intactos).</summary>
    public async Task<bool> OcultarPreparacionAsync(int id)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsync($"/api/cafe/ventas/preparacion/ocultar/{id}", null);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Oculta TODAS las ventas que estan en el tablero ahora. Devuelve cuantas oculto.</summary>
    public async Task<int> LimpiarTableroPreparacionAsync(int dias = 7)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsync($"/api/cafe/ventas/preparacion/limpiar-tablero?dias={dias}", null);
        if (!resp.IsSuccessStatusCode) return 0;
        var data = await resp.Content.ReadFromJsonAsync<LimpiarTableroResponse>();
        return data?.ocultas ?? 0;
    }
    private record LimpiarTableroResponse(int ocultas);

    /// <summary>Lista las ventas ya MARCADAS COMO LISTO/EN_CAMINO/ENTREGADO — para la sección
    /// colapsable "Ya armados" del tablero. Rango: hoy / ayer / 7d / 30d / todos.</summary>
    public async Task<List<CafePreparacionVentaDto>?> GetCafePreparacionArmadosAsync(string rango = "7d")
        => await GetAsync<List<CafePreparacionVentaDto>>($"/api/cafe/ventas/preparacion/armados?rango={rango}");

    // ─── 2026-06-03: Config del modo nuevo de fichada (piloto WiFi + GPS) ───
    public record ConfigFichadaDto(bool ActivarModoNuevo, string? Wifi1Ip, string? Wifi1Label,
        string? Wifi2Ip, string? Wifi2Label, bool RequiereHuella, bool LoguearGps,
        bool BloquearPorGps, decimal? NegocioLat, decimal? NegocioLon, int RadioMetros,
        DateTime? UpdatedAt, string? UpdatedBy);

    public async Task<ConfigFichadaDto?> GetConfigFichadaAsync()
        => await GetAsync<ConfigFichadaDto>("/api/horas-extras/admin/config-fichada");

    public class UpdateConfigFichadaRequest
    {
        public bool? ActivarModoNuevo { get; set; }
        public string? Wifi1Ip { get; set; }
        public string? Wifi1Label { get; set; }
        public string? Wifi2Ip { get; set; }
        public string? Wifi2Label { get; set; }
        public bool? RequiereHuella { get; set; }
        public bool? LoguearGps { get; set; }
        public bool? BloquearPorGps { get; set; }
        public decimal? NegocioLat { get; set; }
        public decimal? NegocioLon { get; set; }
        public int? RadioMetros { get; set; }
        public string? UpdatedBy { get; set; }
    }

    // ─── 2026-06-27: toda la fichada por empleado (WiFi, GPS, kiosco, solo-info) en una pantalla ───
    public record EmpleadoPilotoDto(int Id, string Nombre, bool ProbarWifi, bool ProbarGps, bool Kiosco, bool SoloInfo, bool KioscoPersonal);
    public async Task<List<EmpleadoPilotoDto>?> GetEmpleadosPilotoAsync()
        => await GetAsync<List<EmpleadoPilotoDto>>("/api/horas-extras/admin/config-fichada/empleados-piloto");
    /// <summary>Setea uno o varios flags del empleado. Pasá null en el que no querés tocar.</summary>
    public async Task<bool> SetEmpleadoPilotoAsync(int id, bool? wifi = null, bool? gps = null, bool? kiosco = null, bool? soloInfo = null, bool? kioscoPersonal = null)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PutAsJsonAsync($"/api/horas-extras/admin/config-fichada/empleados-piloto/{id}",
            new { Wifi = wifi, Gps = gps, Kiosco = kiosco, SoloInfo = soloInfo, KioscoPersonal = kioscoPersonal });
        return resp.IsSuccessStatusCode;
    }
    public async Task<(ConfigFichadaDto? cfg, string? error)> UpdateConfigFichadaAsync(UpdateConfigFichadaRequest req)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PutAsJsonAsync("/api/horas-extras/admin/config-fichada", req);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            return (null, body);
        }
        return (await resp.Content.ReadFromJsonAsync<ConfigFichadaDto>(), null);
    }
    public async Task<string?> GetMiIpFichadaAsync()
    {
        var d = await GetAsync<MiIpResponse>("/api/horas-extras/admin/config-fichada/mi-ip");
        return d?.Ip;
    }
    private record MiIpResponse(string? Ip);

    /// <summary>Marca una venta como impresa (chip "Impreso hace X min" en la card).</summary>
    public async Task<bool> MarcarImpresaAsync(int id)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsync($"/api/cafe/ventas/{id}/marcar-impresa", null);
        return resp.IsSuccessStatusCode;
    }

    /// <summary>URL del endpoint para imprimir PDF combinado de varias ventas. Para usar con un &lt;a target="_blank"&gt;.</summary>
    public string GetImprimirPdfCombinadoUrl(IEnumerable<int> ids)
        => $"/api/cafe/ventas/imprimir-pdf-combinado?ids={string.Join(",", ids)}";

    // === Preventas (admin) ===
    public async Task<List<CafePreventaAdminDto>?> GetCafePreventasAdminAsync(string estado = "pendiente")
        => await GetAsync<List<CafePreventaAdminDto>>($"/api/preventas/admin?estado={Uri.EscapeDataString(estado)}");

    public async Task<CafePreventaDetalleDto?> GetCafePreventaDetalleAsync(int id)
        => await GetAsync<CafePreventaDetalleDto>($"/api/preventas/admin/{id}");

    public async Task<int> GetCafePreventasPendientesCountAsync()
    {
        var r = await GetAsync<PendientesCountDto>("/api/preventas/admin/pendientes-count");
        return r?.count ?? 0;
    }

    public async Task<bool> MarcarPreventaProcesadaAsync(int id)
    {
        await SetAuthHeaderAsync();
        var r = await _http.PostAsync($"/api/preventas/admin/{id}/marcar-procesada", null);
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> CancelarPreventaAsync(int id)
    {
        await SetAuthHeaderAsync();
        var r = await _http.PostAsync($"/api/preventas/admin/{id}/cancelar", null);
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> ReabrirPreventaAsync(int id)
    {
        await SetAuthHeaderAsync();
        var r = await _http.PostAsync($"/api/preventas/admin/{id}/reabrir", null);
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> VincularPreventaVentaAsync(int preventaId, int ventaId)
    {
        await SetAuthHeaderAsync();
        var r = await _http.PostAsJsonAsync($"/api/preventas/admin/{preventaId}/vincular-venta", new { VentaId = ventaId });
        return r.IsSuccessStatusCode;
    }

    public async Task<List<CafePreventaVendedorDto>?> GetCafePreventaVendedoresAsync()
        => await GetAsync<List<CafePreventaVendedorDto>>("/api/preventas/admin/vendedores");

    private record PendientesCountDto(int count);

    /// <summary>Lista de clientes con saldo pendiente (deudores), ordenada por venta más antigua primero.</summary>
    public async Task<List<ClienteSaldoPendienteDto>?> GetCafeClientesSaldosPendientesAsync()
        => await GetAsync<List<ClienteSaldoPendienteDto>>("/api/cafe/clientes/saldos-pendientes");

    /// <summary>2026-06-06: ventas ocasionales (sin cliente del catálogo) con saldo pendiente.</summary>
    public async Task<List<VentaOcasionalSaldoDto>?> GetCafeVentasOcasionalesSaldosAsync()
        => await GetAsync<List<VentaOcasionalSaldoDto>>("/api/cafe/clientes/saldos-ocasionales");

    /// <summary>Token publico del panel de saldos de clientes — para compartir link sin login.</summary>
    public async Task<string?> GetCafeClientesPanelPublicTokenAsync()
    {
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync("/api/cafe/clientes/saldos-pendientes/public-token");
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        if (!resp.IsSuccessStatusCode) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("token", out var t)) return t.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Regenera el token publico del panel de saldos (invalida el anterior).</summary>
    public async Task<string?> RegenerateCafeClientesPanelPublicTokenAsync()
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsync("/api/cafe/clientes/saldos-pendientes/public-token/regenerate", null);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        if (!resp.IsSuccessStatusCode) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("token", out var t)) return t.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Descarga Excel de cuentas corrientes de los clientes seleccionados (o todos los deudores si la lista está vacía).</summary>
    public async Task<byte[]?> ExportSaldosPendientesExcelAsync(List<int>? clienteIds)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("/api/cafe/clientes/saldos-pendientes/excel",
            new { ClienteIds = clienteIds });
        if (resp.IsSuccessStatusCode) return await resp.Content.ReadAsByteArrayAsync();
        return null;
    }

    /// <summary>Devuelve los bytes del PDF de una venta Café (cotización/proforma) o null si fallo.</summary>
    /// <summary>Descarga el PDF "Recibo de Visita por Cobranza" — para imprimir cuando el
    /// repartidor va solo a cobrar un comprobante antiguo.</summary>
    public async Task<(byte[]? bytes, string? error)> GetCafeVentaReciboVisitaPdfAsync(int id)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync($"/api/cafe/ventas/{id}/recibo-visita.pdf");
        if (!resp.IsSuccessStatusCode) return (null, $"HTTP {(int)resp.StatusCode}");
        return (await resp.Content.ReadAsByteArrayAsync(), null);
    }

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

    /// <summary>Asegura que la venta tenga un PublicToken (lo genera si no tiene)
    /// y devuelve el token, asi el frontend puede armar la URL publica.</summary>
    public async Task<string?> EnsurePublicTokenAsync(int id)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsync($"/api/cafe/ventas/{id}/ensure-public-token", null);
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return null;
        }
        if (!resp.IsSuccessStatusCode) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("token", out var t)) return t.GetString();
        }
        catch { }
        return null;
    }

    /// <summary>Manda el comprobante por email al destinatario indicado (con PDF adjunto).
    /// publicUrl: si esta seteada, se agrega como "Ver online: {url}" al final del body.</summary>
    public async Task<(bool sent, string? error)> SendCafeVentaEmailAsync(int id, string to, string? subject = null, string? body = null, string? publicUrl = null)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/cafe/ventas/{id}/send-email", new { To = to, Subject = subject, Body = body, PublicUrl = publicUrl });
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (false, "Sesión expirada");
        }
        var content = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode) return (true, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var err)) return (false, err.GetString());
        }
        catch { }
        return (false, content);
    }

    /// <summary>Manda el comprobante por WhatsApp via el container vinculado (con PDF adjunto).</summary>
    public async Task<(bool sent, string? error)> SendCafeVentaWhatsappInternoAsync(int id, string phone, string? caption = null)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/cafe/ventas/{id}/send-whatsapp-interno", new { Phone = phone, Caption = caption });
        if (resp.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleUnauthorizedAsync();
            return (false, "Sesión expirada");
        }
        var content = await resp.Content.ReadAsStringAsync();
        if (resp.IsSuccessStatusCode) return (true, null);
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            if (doc.RootElement.TryGetProperty("error", out var err)) return (false, err.GetString());
        }
        catch { }
        return (false, content);
    }

    public async Task<CafeVentaDto?> AnularCafeVentaAsync(int id)
        => await PostAsync<CafeVentaDto>($"/api/cafe/ventas/{id}/anular", new { });

    public async Task<CafeVentaDto?> RetryArcaCafeVentaAsync(int id)
        => await PostAsync<CafeVentaDto>($"/api/cafe/ventas/{id}/retry-arca", new { });

    /// <summary>Devuelve todas las FA/FB/FC que no estan autorizadas en ARCA — pendientes,
    /// rechazadas, etc — para la pantalla 'Errores ARCA'.</summary>
    public async Task<List<CafeVentaDto>?> GetCafeArcaErroresAsync()
        => await GetAsync<List<CafeVentaDto>>("/api/cafe/ventas/arca/errores");

    /// <summary>Pide al backend un payload pre-armado para duplicar el comprobante.
    /// NO crea la venta — solo devuelve los datos para llenar el modal de Nueva Venta.</summary>
    public async Task<DuplicarVentaPayloadDto?> DuplicarCafeVentaAsync(int id)
        => await PostAsync<DuplicarVentaPayloadDto>($"/api/cafe/ventas/{id}/duplicar", new { });

    /// <summary>Convierte una proforma (X o PRO) en factura real (FA/FB/FC) con CAE de ARCA.
    /// Crea una venta NUEVA, vincula a la original. Devuelve la nueva venta con su CAE.</summary>
    public async Task<CafeVentaDto?> ConvertirCafeVentaAFacturaAsync(int id, ConvertirAFacturaRequest req)
        => await PostAsync<CafeVentaDto>($"/api/cafe/ventas/{id}/convertir-a-factura", req);

    // 2026-06-09 Nota de Credito Fase 1 (TOTAL)
    public record EmitirNcRequest(string? Motivo);
    public class EmitirNcResponse
    {
        public bool ok { get; set; }
        public int? ncVentaId { get; set; }
        public string? ncNumero { get; set; }
        public string? ncTipo { get; set; }
        public string? cae { get; set; }
        public string? mensaje { get; set; }
        public string? error { get; set; }
    }
    public async Task<(EmitirNcResponse? data, string? errorMsg)> EmitirNotaCreditoAsync(int ventaId, string? motivo)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync($"/api/cafe/ventas/{ventaId}/nota-credito", new EmitirNcRequest(motivo));
            if (resp.IsSuccessStatusCode)
            {
                var data = await resp.Content.ReadFromJsonAsync<EmitirNcResponse>();
                return (data, null);
            }
            var errBody = await resp.Content.ReadAsStringAsync();
            try
            {
                var errObj = System.Text.Json.JsonSerializer.Deserialize<EmitirNcResponse>(errBody,
                    new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return (null, errObj?.error ?? errBody);
            }
            catch { return (null, errBody); }
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

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

    /// <summary>2026-06-08: marca un producto como "descartado" en las sugerencias del cliente.
    /// El producto deja de aparecer en "Más comprados" hasta que el cliente lo vuelva a comprar.</summary>
    public async Task<bool> DescartarTopProductoClienteAsync(int clienteId, int productoId)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/cafe/ventas/top-productos-cliente/{clienteId}/descartar",
            new { ProductoId = productoId });
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return false; }
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Restaura un producto descartado (vuelve a aparecer en sugerencias).
    /// Si productoId &lt;= 0 → restaura TODOS los descartados del cliente.</summary>
    public async Task<bool> RestaurarTopProductoClienteAsync(int clienteId, int productoId = 0)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/cafe/ventas/top-productos-cliente/{clienteId}/restaurar",
            new { ProductoId = productoId });
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return false; }
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Devuelve los IDs de productos descartados de un cliente (para mostrar contador).</summary>
    public async Task<List<int>?> GetCafeTopProductosDescartadosAsync(int clienteId)
        => await GetAsync<List<int>>($"/api/cafe/ventas/top-productos-cliente/{clienteId}/descartados");

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

    // --- Cafe: Listas de precios personalizadas (Fase 1) ---
    public async Task<List<ListaCustomDto>?> ListarListasCustomAsync()
        => await GetAsync<List<ListaCustomDto>>("/api/cafe/listas-custom");

    public async Task<ListaCustomDto?> GetListaCustomAsync(int id)
        => await GetAsync<ListaCustomDto>($"/api/cafe/listas-custom/{id}");

    public async Task<CrearListaCustomResponse?> CrearListaCustomAsync(CrearListaCustomRequest req)
        => await PostAsync<CrearListaCustomResponse>("/api/cafe/listas-custom", req);

    public async Task<bool> ActualizarListaCustomAsync(int id, CrearListaCustomRequest req)
    {
        var r = await PutAsync<object>($"/api/cafe/listas-custom/{id}", req);
        return r is not null;
    }

    public async Task<bool> BorrarListaCustomAsync(int id)
        => await DeleteAsync($"/api/cafe/listas-custom/{id}");

    public async Task<CrearListaCustomResponse?> DuplicarListaCustomAsync(int id)
        => await PostAsync<CrearListaCustomResponse>($"/api/cafe/listas-custom/{id}/duplicar", new { });

    // --- Fase 2: secciones + items + items disponibles ---
    public async Task<ContenidoListaCustomDto?> GetContenidoListaCustomAsync(int listaId)
        => await GetAsync<ContenidoListaCustomDto>($"/api/cafe/listas-custom/{listaId}/contenido");

    public async Task<List<ItemDisponibleDto>?> GetItemsDisponiblesAsync(string tipo, string? q = null, string? marca = null, string? categoria = null)
        => await GetAsync<List<ItemDisponibleDto>>($"/api/cafe/listas-custom/items-disponibles?tipo={Uri.EscapeDataString(tipo)}&q={Uri.EscapeDataString(q ?? "")}&marca={Uri.EscapeDataString(marca ?? "")}&categoria={Uri.EscapeDataString(categoria ?? "")}");

    public record AgregarItemsBulkResult(int Agregados, int Salteados);
    public async Task<AgregarItemsBulkResult?> AgregarItemsBulkAsync(int seccionId, List<(string Tipo, int RefId)> items)
    {
        var payload = new { items = items.Select(i => new { tipoItem = i.Tipo, refId = i.RefId }).ToList() };
        var r = await PostAsync<JsonElement>($"/api/cafe/listas-custom/secciones/{seccionId}/items-bulk", payload);
        if (r.ValueKind != JsonValueKind.Object) return null;
        var agregados = r.TryGetProperty("agregados", out var a) ? a.GetInt32() : 0;
        var salteados = r.TryGetProperty("salteados", out var s) ? s.GetInt32() : 0;
        return new AgregarItemsBulkResult(agregados, salteados);
    }

    public async Task<int?> CrearSeccionAsync(int listaId, string titulo)
    {
        var r = await PostAsync<JsonElement>($"/api/cafe/listas-custom/{listaId}/secciones", new { titulo });
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("id", out var idEl)) return idEl.GetInt32();
        return null;
    }

    public async Task<bool> RenombrarSeccionAsync(int seccionId, string titulo)
        => (await PutAsync<object>($"/api/cafe/listas-custom/secciones/{seccionId}", new { titulo })) is not null;

    public async Task<bool> BorrarSeccionAsync(int seccionId)
        => await DeleteAsync($"/api/cafe/listas-custom/secciones/{seccionId}");

    public async Task<bool> MoverSeccionAsync(int seccionId, string direccion)
        => (await PostAsync<object>($"/api/cafe/listas-custom/secciones/{seccionId}/mover?direccion={direccion}", new { })) is not null;

    public async Task<int?> AgregarItemAsync(int seccionId, string tipo, int refId, bool esNovedad = false, string? notas = null)
    {
        var r = await PostAsync<JsonElement>($"/api/cafe/listas-custom/secciones/{seccionId}/items",
            new { tipoItem = tipo, refId, esNovedad, notas });
        if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("id", out var idEl)) return idEl.GetInt32();
        return null;
    }

    public async Task<bool> BorrarItemListaCustomAsync(int itemId)
        => await DeleteAsync($"/api/cafe/listas-custom/items/{itemId}");

    public async Task<bool> ToggleNovedadAsync(int itemId)
        => (await PutAsync<object>($"/api/cafe/listas-custom/items/{itemId}/novedad", new { })) is not null;

    public async Task<bool> MoverItemListaCustomAsync(int itemId, string direccion)
        => (await PostAsync<object>($"/api/cafe/listas-custom/items/{itemId}/mover?direccion={direccion}", new { })) is not null;

    public async Task<bool> ReorderItemsAsync(int seccionId, List<int> itemIds)
        => (await PostAsync<object>($"/api/cafe/listas-custom/secciones/{seccionId}/reorder-items", new { itemIds })) is not null;

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

    public async Task<byte[]?> ExportCafeListaPreciosPdfAsync(CafeListaPreciosFiltroRequest req)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync("/api/cafe/listas-precios/export-pdf", req);
        if (response.IsSuccessStatusCode) return await response.Content.ReadAsByteArrayAsync();
        await ThrowIfErrorAsync(response);
        return null;
    }

    /// <summary>Excel con los productos vendidos en el rango de fechas, agrupados + hoja con detalle.</summary>
    public async Task<byte[]?> ExportCafeProductosVendidosAsync(DateTime desde, DateTime hasta)
    {
        await SetAuthHeaderAsync();
        var url = $"/api/cafe/ventas/export/productos-vendidos?desde={desde:yyyy-MM-dd}&hasta={hasta:yyyy-MM-dd}";
        var response = await _http.GetAsync(url);
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

    public async Task<bool> RecuperarCafeVentaAsync(int id, string password)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsJsonAsync($"/api/cafe/ventas/{id}/recuperar", new { password });
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

    // 2026-06-18: toggle individual de EsCompuesto (Compuesto <-> Combo MeLi) sin reenviar items.
    public async Task<bool> SetEsCompuestoAsync(int comboId, bool esCompuesto)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PatchAsJsonAsync($"/api/cafe/combos/{comboId}/es-compuesto", new { EsCompuesto = esCompuesto });
        return resp.IsSuccessStatusCode;
    }

    public record ReclasificarComboResult(int Total, int QuedaronCompuestos, int PasaronACombo, List<string> SkusDegradados);
    public async Task<ReclasificarComboResult?> ReclasificarCombosAutomaticoAsync()
        => await PostAsync<ReclasificarComboResult>("/api/cafe/combos/reclasificar-automatico", new { });

    public async Task<CafeComboDto?> GetCafeComboAsync(int id)
        => await GetAsync<CafeComboDto>($"/api/cafe/combos/{id}");

    public async Task<CafeComboDto?> CreateCafeComboAsync(CreateCafeComboRequest req)
        => await PostAsync<CafeComboDto>("/api/cafe/combos", req);

    public async Task<CafeComboDto?> UpdateCafeComboAsync(int id, UpdateCafeComboRequest req)
        => await PutAsync<CafeComboDto>($"/api/cafe/combos/{id}", req);

    public async Task<bool> DeleteCafeComboAsync(int id)
        => await DeleteAsync($"/api/cafe/combos/{id}");

    // 2026-06-18: sugeridor masivo OEM para compuestos
    public async Task<SugerirOemsResponse?> SugerirOemsAsync()
        => await GetAsync<SugerirOemsResponse>("/api/cafe/combos/sugerir-oems");

    public async Task<AplicarSugerenciasOemResponse?> AplicarSugerenciasOemAsync(AplicarSugerenciasOemRequest req)
        => await PostAsync<AplicarSugerenciasOemResponse>("/api/cafe/combos/aplicar-sugerencias-oem", req);

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

    /// <summary>2026-06-11: scrapea la web del proveedor del OEM y actualiza imagen/descripcion/ficha.</summary>
    public async Task<HttpResponseMessage> ScrapeCafeOemWebAsync(int id)
    {
        await SetAuthHeaderAsync();
        return await _http.PostAsJsonAsync($"/api/cafe/oems/{id}/scrape-web", new { });
    }

    // ==========================================
    // 2026-06-11: Precio independiente por MLA (familias con cuotas)
    // ==========================================

    public async Task<HttpResponseMessage> MarcarPrecioIndependienteAsync(string mlaId)
    {
        await SetAuthHeaderAsync();
        return await _http.PostAsJsonAsync($"/api/meli/items/mla/{mlaId}/marcar-precio-independiente", new { });
    }

    public async Task<HttpResponseMessage> DesmarcarPrecioIndependienteAsync(string mlaId)
    {
        await SetAuthHeaderAsync();
        return await _http.PostAsJsonAsync($"/api/meli/items/mla/{mlaId}/desmarcar-precio-independiente", new { });
    }

    public async Task<HttpResponseMessage> RecalcularFactorAsync(string mlaId)
    {
        await SetAuthHeaderAsync();
        return await _http.PostAsJsonAsync($"/api/meli/items/mla/{mlaId}/recalcular-factor", new { });
    }

    public async Task<HttpResponseMessage> MarcarFamiliaPrecioIndependienteAsync(string familyId, bool marcar = true)
    {
        await SetAuthHeaderAsync();
        return await _http.PostAsJsonAsync($"/api/meli/family/{familyId}/marcar-precio-independiente?marcar={(marcar ? "true" : "false")}", new { });
    }

    public async Task<HttpResponseMessage> PreviewPropagacionAsync(int cafeProductoId, decimal nuevoPrecioBase)
    {
        await SetAuthHeaderAsync();
        return await _http.GetAsync($"/api/meli/preview-propagacion/{cafeProductoId}?nuevoPrecioBase={nuevoPrecioBase}");
    }

    public async Task<HttpResponseMessage> PushMasivoFamiliaAsync(string familyId)
    {
        await SetAuthHeaderAsync();
        return await _http.PostAsJsonAsync($"/api/meli/family/{familyId}/push-masivo", new { });
    }

    /// <summary>2026-06-11: análisis de margen por familia (neto que queda en cada MLA).</summary>
    public async Task<HttpResponseMessage> GetAnalisisMargenFamiliaAsync(string familyId, decimal? comisionCategoriaPct = null, decimal? envioGratisCostoEstimado = null)
    {
        await SetAuthHeaderAsync();
        var qs = new List<string>();
        if (comisionCategoriaPct.HasValue) qs.Add($"comisionCategoriaPct={comisionCategoriaPct.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        if (envioGratisCostoEstimado.HasValue) qs.Add($"envioGratisCostoEstimado={envioGratisCostoEstimado.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        var query = qs.Count > 0 ? "?" + string.Join("&", qs) : "";
        return await _http.GetAsync($"/api/meli/family/{familyId}/analisis-margen{query}");
    }

    /// <summary>2026-06-11: override del tag de cuotas para una MLA específica.</summary>
    public async Task<HttpResponseMessage> OverrideCuotasMlaAsync(string mlaId, string? tag)
    {
        await SetAuthHeaderAsync();
        return await _http.PostAsJsonAsync($"/api/meli/items/mla/{mlaId}/override-cuotas", new { tag });
    }

    /// <summary>2026-06-11: arranca el job masivo de scraping para todos los OEMs.</summary>
    public async Task<HttpResponseMessage> StartCafeOemScrapeMasivoAsync(string? proveedor = null, bool soloFaltantes = true)
    {
        await SetAuthHeaderAsync();
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(proveedor)) qs.Add($"proveedor={Uri.EscapeDataString(proveedor)}");
        qs.Add($"soloFaltantes={soloFaltantes.ToString().ToLower()}");
        var url = "/api/cafe/oems/scrape-web/masivo?" + string.Join("&", qs);
        return await _http.PostAsJsonAsync(url, new { });
    }

    /// <summary>2026-06-11: status del job masivo de scraping.</summary>
    public async Task<CafeOemScrapeMasivoStatusDto?> GetCafeOemScrapeMasivoStatusAsync()
        => await GetAsync<CafeOemScrapeMasivoStatusDto>("/api/cafe/oems/scrape-web/masivo/status");

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

    public record CoffeeMonthlyHistoricoRow(int Year, int Month, DateTime PeriodStart, decimal KgTotal, int Items);
    public async Task<List<CoffeeMonthlyHistoricoRow>?> GetCoffeeMonthlyKgHistoricoAsync(int meses = 12)
        => await GetAsync<List<CoffeeMonthlyHistoricoRow>>($"/api/dashboard/coffee-monthly-kg-historico?meses={meses}");

    public async Task<CoffeeStockKgDto?> GetCoffeeStockKgAsync()
        => await GetAsync<CoffeeStockKgDto>("/api/dashboard/coffee-stock-kg");

    /// <summary>2026-07-08: espacio en disco del servidor (lo registra el robot de las 2 AM).</summary>
    public async Task<DiskUsageDto?> GetDiskUsageAsync()
        => await GetAsync<DiskUsageDto>("/api/dashboard/disk-usage");

    /// <summary>Resumen financiero (ventas del mes + saldos a cobrar de clientes).</summary>
    public async Task<SalesSummaryDto?> GetSalesSummaryAsync()
        => await GetAsync<SalesSummaryDto>("/api/dashboard/sales-summary");

    // ════════════════════════════════════════════════════════════════════════════════
    // 2026-06-25: Dashboard nuevo — equipo trabajando ahora + resumen del día
    // ════════════════════════════════════════════════════════════════════════════════
    public record DashboardEquipoItem(
        int NomEmpleadoId, string Nombre, string? ApodoKiosko, string? ApodoRepartidor,
        string Estado, string? HoraEntrada, string? HoraSalida, string? Trabajado,
        decimal PorRendir, decimal Pagado, decimal LeDebo, bool TieneRepartidor,
        decimal PorRendirVentas = 0, decimal PorRendirAlquiler = 0,
        // 2026-07-04: carga del repartidor para la card del dashboard.
        // Ventas asignadas via QR y aun sin entregar / ya entregadas hoy por este repartidor.
        int VentasPendientes = 0, int VentasEntregadasHoy = 0,
        // Alquileres entregados/retirados HOY por este repartidor.
        int AlqEntregadosHoy = 0, int AlqRetiradosHoy = 0);
    public record DashboardEquipoResumen(int Trabajando, int Salio, int SinFichar, int NoFicha);
    public record DashboardEquipoResponse(List<DashboardEquipoItem> Items, DashboardEquipoResumen Resumen, DateTime Fecha);

    public async Task<DashboardEquipoResponse?> GetDashboardEquipoAsync()
        => await GetAsync<DashboardEquipoResponse>("/api/dashboard/equipo-dia");

    // 2026-07-04: historial de ventas por mes para el mini gráfico en la card "Ventas del mes".
    public record MonthlySalesPointDto(
        int Year, int Month, string MonthLabel,
        decimal TotalGeneral, int TotalCount,
        decimal CotizacionesTotal, int CotizacionesCount,
        decimal FacturasConIva, decimal FacturasSinIva, int FacturasCount);

    public async Task<List<MonthlySalesPointDto>?> GetMonthlySalesHistoryAsync(int months = 12)
        => await GetAsync<List<MonthlySalesPointDto>>($"/api/dashboard/monthly-sales-history?months={months}");

    public record DashboardResumenDiaDto(
        int ChequesHoyCantidad, decimal ChequesHoyImporte,
        int ChequesProxima7DiasCantidad, decimal ChequesProxima7DiasImporte,
        int PreguntasMeliPendientes, int PreguntasMeliNoVistas);

    public async Task<DashboardResumenDiaDto?> GetDashboardResumenDiaAsync()
        => await GetAsync<DashboardResumenDiaDto>("/api/dashboard/resumen-dia");

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

    /// <summary>2026-07-03: borra una imagen de branding (usada por el overlay de personalizacion de cards del dashboard).</summary>
    public async Task DeleteBrandingImageAsync(string key)
    {
        await SetAuthHeaderAsync();
        var response = await _http.DeleteAsync($"/api/branding/{Uri.EscapeDataString(key)}");
        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 404)
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

    public async Task<string?> TestGoogleDriveAsync()
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsync("/api/integrations/google-drive/test", null);

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

    /// <summary>Pide la URL de Google Auth al backend para abrir el popup/redirect del OAuth.</summary>
    public async Task<string?> GetGoogleDriveOAuthUrlAsync(string redirectUri)
    {
        await SetAuthHeaderAsync();
        var response = await _http.GetAsync($"/api/integrations/google-drive/oauth-start?redirectUri={Uri.EscapeDataString(redirectUri)}");

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
            if (doc.RootElement.TryGetProperty("url", out var u))
                return u.GetString();
            if (doc.RootElement.TryGetProperty("error", out var err))
                throw new Exception(err.GetString());
        }
        catch (System.Text.Json.JsonException) { }

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Error del servidor ({response.StatusCode})");

        return null;
    }

    /// <summary>Canjea el code que devolvió Google por el refresh_token y lo guarda en el backend.</summary>
    public async Task<string?> ExchangeGoogleDriveOAuthCodeAsync(string code, string redirectUri)
    {
        await SetAuthHeaderAsync();
        var payload = new { code, redirectUri };
        var json = System.Text.Json.JsonSerializer.Serialize(payload);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await _http.PostAsync("/api/integrations/google-drive/oauth-exchange", content);

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

    /// <summary>Sube el PDF de una venta a Google Drive. Devuelve (fileId, link a Drive, fecha de subida).</summary>
    public async Task<(string fileId, string link, DateTime? subidoAt)> SubirVentaADriveAsync(int ventaId)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsync($"/api/cafe/ventas/{ventaId}/drive-upload", null);

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            await _authService.LogoutAsync();
            _navigation.NavigateTo("/login", forceLoad: true);
            return ("", "", null);
        }

        var body = await response.Content.ReadAsStringAsync();
        System.Text.Json.JsonDocument? doc = null;
        try { doc = System.Text.Json.JsonDocument.Parse(body); } catch { }

        if (!response.IsSuccessStatusCode)
        {
            var msg = "Error desconocido";
            if (doc is not null && doc.RootElement.TryGetProperty("error", out var err))
                msg = err.GetString() ?? msg;
            throw new Exception(msg);
        }

        var root = doc!.RootElement;
        var fileId = root.TryGetProperty("fileId", out var f) ? (f.GetString() ?? "") : "";
        var link = root.TryGetProperty("link", out var l) ? (l.GetString() ?? "") : "";
        DateTime? subidoAt = null;
        if (root.TryGetProperty("subidoAt", out var sa) && sa.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            if (DateTime.TryParse(sa.GetString(), out var dt)) subidoAt = dt;
        }
        return (fileId, link, subidoAt);
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
        string cuit, string? alias, string? password, string environment, Stream fileStream, string fileName, int? ptoVta = null)
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
        if (ptoVta.HasValue && ptoVta.Value > 0) content.Add(new StringContent(ptoVta.Value.ToString()), "ptoVta");

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

    public async Task<ConsultaComprobanteResultDto?> ConsultarArcaComprobanteAsync(int accountId, int ptoVta, int cbteTipo, int cbteNro)
        => await PostAsync<ConsultaComprobanteResultDto>($"/api/arca-webservice/accounts/{accountId}/consultar-comprobante",
            new { ptoVta, cbteTipo, cbteNro });

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

    // === Snapshot Contabilium pre-corte ===
    public class SnapshotTriggerResult { public bool Ok { get; set; } public DateTime Fecha { get; set; } public int Skus { get; set; } public int DurationSec { get; set; } }

    public async Task<SnapshotTriggerResult?> TriggerContabiliumSnapshotAsync()
    {
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(10));
        var resp = await _http.PostAsync("/api/contabilium/snapshot/trigger", null, cts.Token);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<SnapshotTriggerResult>();
    }

    public string GetContabiliumSnapshotDownloadUrl(DateTime? fecha = null)
        => "/api/contabilium/snapshot/download" + (fecha.HasValue ? $"?fecha={fecha.Value:yyyy-MM-dd}" : "");

    /// <summary>Llama /api/meli/items/audit. Compara MLAs de MeLi vs sistema. No modifica nada.
    // === Reactivación de publicaciones MeLi pausadas por push erróneo ===
    public class ReactivacionCandidato
    {
        public string MeliItemId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Sku { get; set; } = "";
        public int AccountId { get; set; }
        public string Nickname { get; set; } = "";
        public int CafeProductoId { get; set; }
        public string CafeProductoSku { get; set; } = "";
        public string CafeProductoNombre { get; set; } = "";
        public DateTime LastPushedToMeli { get; set; }
    }
    public class ReactivacionCandidatosResp { public int Count { get; set; } public List<ReactivacionCandidato> Items { get; set; } = new(); }
    public class ReactivacionResult { public int Procesadas { get; set; } public int Reactivadas { get; set; } public int YaActivas { get; set; } public int Errores { get; set; } public List<string> Detalles { get; set; } = new(); }

    // === Cambios detectados (precio + status MeLi) ===
    public class MeliCambioDto
    {
        public int Id { get; set; }
        public string MeliItemId { get; set; } = "";
        public string? Sku { get; set; }
        public string? Title { get; set; }
        public string Tipo { get; set; } = "";
        public string? ValorAnterior { get; set; }
        public string? ValorNuevo { get; set; }
        public decimal? Delta { get; set; }
        public decimal? DeltaPct { get; set; }
        public string Source { get; set; } = "";
        public DateTime DetectedAt { get; set; }
        public DateTime? SeenAt { get; set; }
        public string? AccountNickname { get; set; }
    }

    public async Task<List<MeliCambioDto>?> GetMeliCambiosAsync(bool soloSinVer = false, string? tipo = null, int limit = 200)
    {
        var qs = new List<string>();
        if (soloSinVer) qs.Add("soloSinVer=true");
        if (!string.IsNullOrWhiteSpace(tipo)) qs.Add($"tipo={Uri.EscapeDataString(tipo)}");
        qs.Add($"limit={limit}");
        return await GetAsync<List<MeliCambioDto>>("/api/meli/cambios?" + string.Join("&", qs));
    }

    public class CountResp { public int Count { get; set; } }
    public async Task<int> GetMeliCambiosCountPendientesAsync()
    {
        var r = await GetAsync<CountResp>("/api/meli/cambios/count-pending");
        return r?.Count ?? 0;
    }

    public async Task MarkCambioSeenAsync(int id)
        => await _http.PostAsync($"/api/meli/cambios/{id}/mark-seen", null);

    public async Task MarkAllCambiosSeenAsync()
        => await _http.PostAsync("/api/meli/cambios/mark-all-seen", null);

    // === WhatsApp Pedidos ===
    public class WhatsAppPedidoDto
    {
        public int Id { get; set; }
        public string Telefono { get; set; } = "";
        public string TextoCrudo { get; set; } = "";
        public int? ClienteId { get; set; }
        public string? ClienteNombre { get; set; }
        public string? ProductosParseados { get; set; }
        public string? ParseError { get; set; }
        public string Estado { get; set; } = "";
        public int? VentaIdGenerada { get; set; }
        public DateTime RecibidoAt { get; set; }
        public string Source { get; set; } = "";
        public DateTime? SeenAt { get; set; }
    }

    public class WhatsAppPedidoConfig
    {
        public string Trigger { get; set; } = "#PED";
        public bool PollEnabled { get; set; }
        public bool AutoResponderEnabled { get; set; } = true;
        public List<WhatsAppPedidoTelefonoDto> Telefonos { get; set; } = new();
    }

    public class WhatsAppPedidoTelefonoDto
    {
        public int Id { get; set; }
        public string Telefono { get; set; } = "";
        public string? Etiqueta { get; set; }
        public bool Activo { get; set; } = true;
        public string? LastMessageId { get; set; }
        public DateTime? LastReadAt { get; set; }
    }

    public async Task<int> GetWhatsAppPedidosCountPendientesAsync()
    {
        var r = await GetAsync<CountResp>("/api/whatsapp/pedidos/count-pending");
        return r?.Count ?? 0;
    }

    public async Task<List<WhatsAppPedidoDto>?> ListarWhatsAppPedidosAsync(string? estado = null, bool soloSinVer = false, int limit = 100)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(estado)) qs.Add($"estado={Uri.EscapeDataString(estado)}");
        if (soloSinVer) qs.Add("soloSinVer=true");
        qs.Add($"limit={limit}");
        return await GetAsync<List<WhatsAppPedidoDto>>("/api/whatsapp/pedidos?" + string.Join("&", qs));
    }

    public class RecibirPedidoResp { public int Id { get; set; } public string Estado { get; set; } = ""; public string? Error { get; set; } }
    public async Task<RecibirPedidoResp?> RecibirWhatsAppPedidoAsync(string telefono, string texto)
    {
        var resp = await _http.PostAsJsonAsync("/api/whatsapp/pedidos/recibir", new { telefono, texto });
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<RecibirPedidoResp>();
    }

    public async Task<bool> ReParsearWhatsAppPedidoAsync(int id)
    {
        var resp = await _http.PostAsync($"/api/whatsapp/pedidos/{id}/re-parsear", null);
        return resp.IsSuccessStatusCode;
    }

    public async Task MarkWhatsAppPedidoSeenAsync(int id)
        => await _http.PostAsync($"/api/whatsapp/pedidos/{id}/mark-seen", null);

    public async Task DescartarWhatsAppPedidoAsync(int id)
        => await _http.PostAsync($"/api/whatsapp/pedidos/{id}/descartar", null);

    public async Task<bool> EliminarWhatsAppPedidoAsync(int id)
    {
        var r = await _http.DeleteAsync($"/api/whatsapp/pedidos/{id}");
        return r.IsSuccessStatusCode;
    }

    public async Task<WhatsAppPedidoConfig?> GetWhatsAppPedidosConfigAsync()
        => await GetAsync<WhatsAppPedidoConfig>("/api/whatsapp/pedidos/config");

    public async Task<bool> SetWhatsAppPedidosConfigAsync(string trigger, bool? pollEnabled = null, bool? autoResponderEnabled = null)
    {
        var resp = await _http.PostAsJsonAsync("/api/whatsapp/pedidos/config", new { trigger, pollEnabled, autoResponderEnabled });
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> ResetWhatsAppPedidosCursorAsync()
    {
        var resp = await _http.PostAsync("/api/whatsapp/pedidos/reset-cursor", null);
        return resp.IsSuccessStatusCode;
    }

    // === Teléfonos autorizados ===
    public async Task<WhatsAppPedidoTelefonoDto?> AgregarTelefonoAutorizadoAsync(string telefono, string? etiqueta)
    {
        var resp = await _http.PostAsJsonAsync("/api/whatsapp/pedidos/telefonos", new { telefono, etiqueta, activo = true });
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<WhatsAppPedidoTelefonoDto>();
    }

    public async Task<bool> EditarTelefonoAutorizadoAsync(int id, string? telefono, string? etiqueta, bool? activo)
    {
        var resp = await _http.PostAsJsonAsync($"/api/whatsapp/pedidos/telefonos/{id}", new { telefono, etiqueta, activo });
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> BorrarTelefonoAutorizadoAsync(int id)
    {
        var resp = await _http.DeleteAsync($"/api/whatsapp/pedidos/telefonos/{id}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<bool> ResetCursorTelefonoAsync(int id)
    {
        var resp = await _http.PostAsync($"/api/whatsapp/pedidos/telefonos/{id}/reset-cursor", null);
        return resp.IsSuccessStatusCode;
    }

    // Para "traer pedido" en Nueva Venta
    public async Task<List<WhatsAppPedidoDto>?> ListarPedidosDisponiblesParaVentaAsync(int limit = 50)
        => await GetAsync<List<WhatsAppPedidoDto>>($"/api/whatsapp/pedidos/disponibles-para-venta?limit={limit}");

    public async Task<bool> VincularPedidoAVentaAsync(int pedidoId, int ventaId)
    {
        var resp = await _http.PostAsJsonAsync($"/api/whatsapp/pedidos/{pedidoId}/vincular-venta", new { ventaId });
        return resp.IsSuccessStatusCode;
    }

    public async Task<ReactivacionCandidatosResp?> GetReactivacionCandidatosAsync()
        => await GetAsync<ReactivacionCandidatosResp>("/api/meli/items/reactivar-pausadas/candidatos");

    public async Task<ReactivacionResult?> ReactivarPausadasAsync(List<string>? meliItemIds = null, int stockSafeDefault = 1)
    {
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(20));
        var resp = await _http.PostAsJsonAsync("/api/meli/items/reactivar-pausadas",
            new { meliItemIds, stockSafeDefault }, cts.Token);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<ReactivacionResult>();
    }

    /// Demora ~1-2 minutos por cuenta (paginado scroll_id de MeLi).</summary>
    public async Task<MeliAuditResult?> AuditMeliItemsAsync(int? accountId = null)
    {
        var url = "/api/meli/items/audit";
        if (accountId.HasValue) url += $"?accountId={accountId.Value}";
        // Timeout amplio porque puede demorar varios minutos
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(15));
        var response = await _http.PostAsync(url, null, cts.Token);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync();
        return System.Text.Json.JsonSerializer.Deserialize<MeliAuditResult>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    // ===== Historial de movimientos de stock (/cafe/historial-stock) =====
    public async Task<List<StockHistorialItem>?> GetStockHistorialAsync(DateTime? desde = null, DateTime? hasta = null,
        int? operadorId = null, int? productoId = null, string? tipoMov = null, string? texto = null, int limit = 500)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        if (operadorId.HasValue) qs.Add($"operadorId={operadorId.Value}");
        if (productoId.HasValue) qs.Add($"productoId={productoId.Value}");
        if (!string.IsNullOrWhiteSpace(tipoMov)) qs.Add($"tipoMov={Uri.EscapeDataString(tipoMov)}");
        if (!string.IsNullOrWhiteSpace(texto)) qs.Add($"texto={Uri.EscapeDataString(texto)}");
        qs.Add($"limit={limit}");
        return await GetAsync<List<StockHistorialItem>>("/api/stock/admin/movimientos?" + string.Join("&", qs));
    }

    public async Task<StockHistorialStats?> GetStockHistorialStatsAsync(DateTime? desde = null, DateTime? hasta = null)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        var url = "/api/stock/admin/movimientos/stats" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<StockHistorialStats>(url);
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

    // 2026-06-01: costo del producto/combo en sistema (para calcular margen)
    public record ProductCostComp(string Sku, string Nombre, decimal CostoUnit, decimal Cantidad, decimal CostoTotal);
    public record ProductCostResp(decimal TotalCost, List<ProductCostComp> Components, string Source);
    public async Task<ProductCostResp?> GetProductCostAsync(string meliItemId)
    {
        try { return await _http.GetFromJsonAsync<ProductCostResp>($"api/meli/items/{meliItemId}/product-cost"); }
        catch { return null; }
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

    public async Task<MeliPushResultDto?> PushMeliItemFromProductAsync(int itemId, bool pushPrice = true, bool pushStock = true, decimal? overridePrice = null)
    {
        await SetAuthHeaderAsync();
        var body = new { pushPrice, pushStock, overridePrice };
        var response = await _http.PostAsJsonAsync($"/api/meli/items/{itemId}/push-from-product", body);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<MeliPushResultDto>();
        await ThrowIfErrorAsync(response);
        return null;
    }

    /// <summary>2026-05-29: persiste el ajuste de precio (% / $ / redondeo) de una publicación en DB.
    /// LEGACY: queda hasta el paso 5 del refactor, no se usa más desde el frontend.</summary>
    public async Task SetMeliItemAjustePrecioAsync(int itemId, decimal? ajustePct, decimal? ajustePesos, string? ajusteRedondeo)
    {
        await SetAuthHeaderAsync();
        var body = new { ajustePctOverride = ajustePct, ajustePesosOverride = ajustePesos, ajusteRedondeoOverride = ajusteRedondeo };
        var response = await _http.PutAsJsonAsync($"/api/meli/items/{itemId}/ajuste-precio", body);
        if (!response.IsSuccessStatusCode) await ThrowIfErrorAsync(response);
    }

    /// <summary>2026-06-03: copia el ajuste del item id a todas las MLAs con el mismo FamilyId.
    /// Devuelve la cantidad de items hermanos a los que se propago.</summary>
    public async Task<int> PropagarAjusteAFamiliaAsync(int itemId)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsync($"/api/meli/items/{itemId}/propagar-ajuste-a-familia", null);
        if (!resp.IsSuccessStatusCode) { await ThrowIfErrorAsync(resp); return 0; }
        var body = await resp.Content.ReadFromJsonAsync<PropagarResp>();
        return body?.Propagados ?? 0;
    }
    private record PropagarResp(bool Ok, string FamilyId, int Propagados);

    // 2026-07-01: ajuste masivo (Fase B). Solo guarda en MeliItem_SyncConfig — NO pushea a MeLi.
    public record BulkAjusteRequest(
        List<int> ItemIds,
        decimal? AjustePct,
        decimal? AjusteFijo,
        string? AjusteRedondeo,
        bool RedondeoTocado,
        bool ModoBorrar,
        bool IncluirPrecioIndependiente);
    public record BulkAjusteResponse(int Modificados, int SaltadosPrecioIndependiente, int NoEncontrados);

    // 2026-07-02: refresh de comisiones SELECTIVO — solo las publis tildadas.
    public record RefreshSaleFeeSelectedResult(int Ok, int Fail, List<string> Errores);
    public async Task<(RefreshSaleFeeSelectedResult? resp, string? error)> RefreshSaleFeeSelectedAsync(List<int> itemIds)
    {
        await SetAuthHeaderAsync();
        try
        {
            var http = await _http.PostAsJsonAsync("/api/meli/items/refresh-salefee-selected", new { itemIds });
            if (http.IsSuccessStatusCode)
                return (await http.Content.ReadFromJsonAsync<RefreshSaleFeeSelectedResult>(), null);
            string err = "Error al refrescar";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await http.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err;
            }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // 2026-07-01: masivo por ganancia — mismo motor que la ficha individual "¿Qué querés ganar?".
    public record BulkPrecioPorGananciaRequest(
        List<int> ItemIds, decimal GananciaPct, string? Redondeo,
        bool IncluirPrecioIndependiente, bool PublicarEnMeli);
    public record BulkPrecioPorGananciaDetail(
        int ItemId, string MeliItemId, string Titulo,
        decimal? Costo, decimal? PrecioBase, decimal? PrecioActual, decimal? PrecioNuevo,
        decimal? GananciaEstimada, decimal? MargenPct,
        bool Guardado, bool PusheadoOk, string? Mensaje);
    public record BulkPrecioPorGananciaResponse(
        int Total, int Guardados, int Pusheados, int SinCosto, int SaltadosPrecioIndep, int Errores,
        List<BulkPrecioPorGananciaDetail> Detalles);

    public async Task<(BulkPrecioPorGananciaResponse? resp, string? error)> BulkPrecioPorGananciaAsync(BulkPrecioPorGananciaRequest req)
    {
        await SetAuthHeaderAsync();
        try
        {
            var http = await _http.PostAsJsonAsync("/api/meli/items/bulk-precio-por-ganancia", req);
            if (http.IsSuccessStatusCode)
                return (await http.Content.ReadFromJsonAsync<BulkPrecioPorGananciaResponse>(), null);
            string err = "Error al aplicar";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await http.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err;
            }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // 2026-07-01: Fase C — push masivo de precios cargados a MeLi.
    public record BulkPushPrecioDetail(int ItemId, string MeliItemId, bool Ok, string Message, decimal? PushedPrice);
    public record BulkPushPrecioResponse(int Total, int Pusheados, int SinAjuste, int Errores, List<BulkPushPrecioDetail> Detalles);

    public async Task<(BulkPushPrecioResponse? resp, string? error)> BulkPushPrecioAsync(List<int> itemIds)
    {
        await SetAuthHeaderAsync();
        try
        {
            var http = await _http.PostAsJsonAsync("/api/meli/items/bulk-push-precio", new { itemIds });
            if (http.IsSuccessStatusCode)
                return (await http.Content.ReadFromJsonAsync<BulkPushPrecioResponse>(), null);
            string err = "Error al pushear";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await http.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err;
            }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<(BulkAjusteResponse? resp, string? error)> BulkAjustePrecioAsync(BulkAjusteRequest req)
    {
        await SetAuthHeaderAsync();
        try
        {
            var http = await _http.PostAsJsonAsync("/api/meli/items/bulk-ajuste-precio", req);
            if (http.IsSuccessStatusCode)
                return (await http.Content.ReadFromJsonAsync<BulkAjusteResponse>(), null);
            string err = "Error al guardar";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await http.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err;
            }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public record PushPrecioAjustadoResultDto(bool Success, string Message, decimal? PushedPrice, decimal? PrecioBaseSistema);

    /// <summary>2026-05-29: pushea precio a MeLi calculado desde PrecioOtro del sistema + ajuste
    /// configurado en MeliItem_SyncConfig. Funciona para CUALQUIER publicación linkeada.</summary>
    public async Task<PushPrecioAjustadoResultDto?> PushPrecioAjustadoAsync(int itemId)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsync($"/api/meli/items/{itemId}/push-precio-ajustado", null);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<PushPrecioAjustadoResultDto>();
        await ThrowIfErrorAsync(response);
        return null;
    }

    public record PushStockMeliItemResultDto(bool Success, string Message, int OkCount, int Skipped, int Errores);

    /// <summary>2026-05-29: push de stock para UNA publicación MeLi (legacy o componentes).
    /// Reemplaza el botón 📦 que antes usaba push-from-product (no soportaba componentes).</summary>
    public async Task<PushStockMeliItemResultDto?> PushStockMeliItemAsync(int itemId)
    {
        await SetAuthHeaderAsync();
        var response = await _http.PostAsync($"/api/meli/items/{itemId}/push-stock-meliitem", null);
        if (response.IsSuccessStatusCode)
            return await response.Content.ReadFromJsonAsync<PushStockMeliItemResultDto>();
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

    /// <summary>2026-07-06: lee el valor de un setting por su key (null si no existe).</summary>
    public async Task<string?> GetSettingAsync(string key)
    {
        try
        {
            var r = await GetAsync<SettingKvDto>($"/api/settings/{Uri.EscapeDataString(key)}");
            return r?.Value;
        }
        catch { return null; }
    }
    private class SettingKvDto { public string? Key { get; set; } public string? Value { get; set; } }

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

    /// <summary>2026-06-23: Lista los chats del WhatsApp Web vinculado (scraping del sidebar).
    /// Si no hay sesion activa o falla, devuelve lista vacia.</summary>
    public async Task<List<WhatsAppChatDto>> GetWhatsAppChatsAsync(int limit = 50)
    {
        try
        {
            var dto = await GetAsync<ChatsResponse>($"/api/whatsapp/chats?limit={limit}");
            return dto?.Chats ?? new List<WhatsAppChatDto>();
        }
        catch { return new List<WhatsAppChatDto>(); }
    }
    private class ChatsResponse { public List<WhatsAppChatDto>? Chats { get; set; } }

    /// <summary>2026-06-23: Abre un chat por nombre (click en el sidebar) y devuelve los mensajes.</summary>
    public async Task<WhatsAppChatMessagesDto> OpenWhatsAppChatByNameAsync(string name)
    {
        await SetAuthHeaderAsync();
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/whatsapp/chats/open", new { name });
            if (!resp.IsSuccessStatusCode) return new WhatsAppChatMessagesDto { Name = name };
            var dto = await resp.Content.ReadFromJsonAsync<WhatsAppChatMessagesDto>();
            return dto ?? new WhatsAppChatMessagesDto { Name = name };
        }
        catch { return new WhatsAppChatMessagesDto { Name = name }; }
    }

    /// <summary>2026-06-23: Abre el chat ubicado en `index` del sidebar (mas robusto que por nombre).</summary>
    public async Task<WhatsAppChatMessagesDto> OpenWhatsAppChatByIndexAsync(int index, string name)
    {
        await SetAuthHeaderAsync();
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/whatsapp/chats/open-by-index", new { index, name });
            if (!resp.IsSuccessStatusCode) return new WhatsAppChatMessagesDto { Name = name };
            var dto = await resp.Content.ReadFromJsonAsync<WhatsAppChatMessagesDto>();
            return dto ?? new WhatsAppChatMessagesDto { Name = name };
        }
        catch { return new WhatsAppChatMessagesDto { Name = name }; }
    }

    /// <summary>2026-06-23: Manda mensaje al chat actualmente abierto.</summary>
    public async Task<bool> SendToWhatsAppOpenChatAsync(string text)
    {
        await SetAuthHeaderAsync();
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/whatsapp/chats/send", new { text });
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
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

    public async Task<FilesStatsDto?> GetFilesStatsAsync(string path)
    {
        var q = Uri.EscapeDataString(path ?? "");
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        return await GetAsync<FilesStatsDto>($"/api/files/stats?path={q}&_={ts}");
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

    public async Task<bool> SetFileMetaAsync(string path, string? color, string? iconEmoji)
    {
        var result = await PostAsync<object>("/api/files/meta", new { path, color, iconEmoji });
        return result is not null;
    }

    // ── Contabilium ──
    public record ContabiliumStatusDto(bool Connected, string? Email, DateTime? LastSyncAt, int? LastSyncCount, string? LastSyncError);
    public async Task<ContabiliumStatusDto?> GetContabiliumStatusAsync()
        => await GetAsync<ContabiliumStatusDto>("/api/contabilium/status");
    public async Task<(bool ok, string? error)> ConnectContabiliumAsync(string email, string apiKey)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("/api/contabilium/connect", new { email, apiKey });
        if (resp.IsSuccessStatusCode) return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) return (false, e.GetString());
        }
        catch { }
        return (false, body);
    }
    public async Task<object?> PingContabiliumAsync() => await GetAsync<object>("/api/contabilium/ping");

    public record StockComparadoRowDto(string Sku, string? Nombre, decimal StockSistema, decimal? StockContabilium, decimal? Diferencia, DateTime? FechaSnapshot);
    public record StockComparadoResponse(List<StockComparadoRowDto> Rows, int Total, DateTime? LastSnapshotAt);
    public async Task<StockComparadoResponse?> GetStockComparadoAsync(string? filtro, bool soloDiferencias)
    {
        var f = string.IsNullOrWhiteSpace(filtro) ? "" : "filtro=" + Uri.EscapeDataString(filtro) + "&";
        return await GetAsync<StockComparadoResponse>($"/api/contabilium/stock-comparado?{f}soloDiferencias={(soloDiferencias ? "true" : "false")}");
    }
    public async Task<object?> RunContabiliumImportAsync() => await PostAsync<object>("/api/contabilium/import", new { });
    public record ContabiliumImportStatusDto(bool Running, DateTime? StartedAt, DateTime? FinishedAt, string? LastError, object? Result);
    public async Task<ContabiliumImportStatusDto?> GetContabiliumImportStatusAsync()
        => await GetAsync<ContabiliumImportStatusDto>("/api/contabilium/import/status");

    // ── MeLi cafe push ──
    public record CafePushPreviewRowDto(string MeliSku, string MeliItemId, string ProductoSku, string ProductoNombre,
        string Formato, decimal PrecioActualMeLi, decimal PrecioNuevoMeLi, decimal PrecioNetoSistema,
        decimal Ratio, int StockActualMeLi, int StockNuevoMeLi);
    public record CafePushPreviewResponse(List<CafePushPreviewRowDto> Rows, int Count);
    public async Task<CafePushPreviewResponse?> GetCafePushPreviewAsync()
        => await GetAsync<CafePushPreviewResponse>("/api/meli/cafe/push-preview");
    public async Task<object?> RunCafePushAsync() => await PostAsync<object>("/api/meli/cafe/push", new { });
    // 2026-06-01: push masivo agresivo de stock a TODAS las publicaciones MeLi con SyncStock=ON
    public async Task<Dictionary<string, object>?> DispararPushMasivoAgresivoAsync()
        => await PostAsync<Dictionary<string, object>>("/api/meli/items/push-stock-masivo-agresivo", new { });
    public record CafePushOneResult(int Procesadas, int Ok, int Errores, List<string> Mensajes);
    public async Task<CafePushOneResult?> RunCafePushOneAsync(string meliItemId)
        => await PostAsync<CafePushOneResult>($"/api/meli/cafe/push-one/{meliItemId}", new { });
    public record CafePushStatusDto(bool Running, DateTime? StartedAt, DateTime? FinishedAt, string? Error, object? Result);
    public async Task<CafePushStatusDto?> GetCafePushStatusAsync()
        => await GetAsync<CafePushStatusDto>("/api/meli/cafe/push/status");

    // ─── Push de QUIETOS (sistema → MeLi, modo safeBulk) ───
    public record QuietoPreviewRow(int ProductoId, string? Sku, string Nombre, string? Categoria,
        decimal StockSistema, decimal? StockContab, string MeliItemId, string? VariationId,
        string TitulMeLi, int StockMeLiActual, string StatusMeLi, int Reserva, int APushear,
        decimal? Cantidad, string Diagnostico);
    public record QuietosPreviewResponse(List<QuietoPreviewRow> Rows, int Total);
    public async Task<QuietosPreviewResponse?> GetQuietosPreviewAsync(string fuente = "sistema", bool incluirCafe = false)
        => await GetAsync<QuietosPreviewResponse>($"/api/meli/quietos-preview?fuente={Uri.EscapeDataString(fuente)}&incluirCafe={incluirCafe}");

    public record QuietosPushResult(int Procesadas, int Ok, int Skipped, int Errores, List<string> Mensajes);
    public async Task<QuietosPushResult?> RunQuietosPushAsync(List<int> productoIds, string fuente = "sistema")
        => await PostAsync<QuietosPushResult>("/api/meli/quietos-push", new { ProductoIds = productoIds, Fuente = fuente });

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

    // --- Banco Galicia (scraping Office Banking empresas) ---
    public async Task<Web.Models.GaliciaAccountDto?> GetGaliciaAccountAsync()
        => await GetAsync<Web.Models.GaliciaAccountDto>("/api/galicia/account");

    public async Task<Web.Models.GaliciaAccountDto?> SaveGaliciaAccountAsync(Web.Models.SaveGaliciaAccountRequest req)
        => await PutAsync<Web.Models.GaliciaAccountDto>("/api/galicia/account", req);

    /// <summary>Dispara la prueba de login. submit=false abre el form sin enviar. Lanza excepción con el error si falla.</summary>
    public async Task StartGaliciaTestAsync(bool submit)
        => await PostAsync<GaliciaOkResp>("/api/galicia/test", new { submit });

    public async Task<Web.Models.GaliciaTestStatusDto?> GetGaliciaTestStatusAsync()
        => await GetAsync<Web.Models.GaliciaTestStatusDto>("/api/galicia/test/status");

    /// <summary>Sincroniza movimientos (robot baja CSV + importa). Puede tardar ~1 min.</summary>
    public async Task<Web.Models.GaliciaSincronizarResultDto?> SincronizarGaliciaAsync()
        => await PostAsync<Web.Models.GaliciaSincronizarResultDto>("/api/galicia/sincronizar", new { });

    private class GaliciaOkResp { public bool Ok { get; set; } }

    // --- Shell Flota (saldo disponible) ---
    public async Task<Web.Models.ShellAccountDto?> GetShellAccountAsync()
        => await GetAsync<Web.Models.ShellAccountDto>("/api/shell/account");

    public async Task<Web.Models.ShellAccountDto?> SaveShellAccountAsync(Web.Models.SaveShellAccountRequest req)
        => await PutAsync<Web.Models.ShellAccountDto>("/api/shell/account", req);

    public async Task<Web.Models.ShellTestStatusDto?> GetShellTestStatusAsync()
        => await GetAsync<Web.Models.ShellTestStatusDto>("/api/shell/test/status");

    /// <summary>Lee el saldo ahora. Usa el cliente de timeout largo (el mail tarda). La cookie de auth viaja sola.</summary>
    public async Task<Web.Models.ShellSincronizarResultDto?> SincronizarShellAsync()
    {
        var resp = await _httpLong.PostAsJsonAsync("/api/shell/sincronizar", new { });
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        await ThrowIfErrorAsync(resp);
        return await resp.Content.ReadFromJsonAsync<Web.Models.ShellSincronizarResultDto>();
    }

    // --- Mercado Pago (API oficial — saldo) ---
    public async Task<Web.Models.MpAccountDto?> GetMpAccountAsync()
        => await GetAsync<Web.Models.MpAccountDto>("/api/mercadopago/account");

    public async Task<Web.Models.MpAccountDto?> SaveMpAccountAsync(Web.Models.SaveMpAccountRequest req)
        => await PutAsync<Web.Models.MpAccountDto>("/api/mercadopago/account", req);

    /// <summary>Lee el saldo de Mercado Pago ahora (llamada HTTP directa a la API oficial, rapida).</summary>
    public async Task<Web.Models.MpSincronizarResultDto?> SincronizarMpAsync()
    {
        var resp = await _http.PostAsJsonAsync("/api/mercadopago/sincronizar", new { });
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        await ThrowIfErrorAsync(resp);
        return await resp.Content.ReadFromJsonAsync<Web.Models.MpSincronizarResultDto>();
    }

    /// <summary>Trae los cobros de MP de los últimos N días y los guarda. Usa timeout largo (varias páginas).</summary>
    public async Task<Web.Models.MpSyncPagosResultDto?> SincronizarMpPagosAsync(int dias = 30)
    {
        var resp = await _httpLong.PostAsync($"/api/mercadopago/pagos/sincronizar?dias={dias}", null);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        await ThrowIfErrorAsync(resp);
        return await resp.Content.ReadFromJsonAsync<Web.Models.MpSyncPagosResultDto>();
    }

    public async Task<List<Web.Models.MpPagoDto>?> GetMpPagosAsync(DateTime? desde = null, DateTime? hasta = null, string? estado = "approved")
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        if (!string.IsNullOrEmpty(estado)) qs.Add($"estado={estado}");
        var url = "/api/mercadopago/pagos" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<Web.Models.MpPagoDto>>(url);
    }

    public async Task<Web.Models.MpPagosResumenDto?> GetMpPagosResumenAsync(DateTime? desde = null, DateTime? hasta = null)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        var url = "/api/mercadopago/pagos/resumen" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<Web.Models.MpPagosResumenDto>(url);
    }

    /// <summary>Resumen de MP (últimos 30 días) para la tarjeta del dashboard. Lee lo guardado, rápido.</summary>
    public async Task<Web.Models.MpDashboardDto?> GetMpDashboardAsync()
        => await GetAsync<Web.Models.MpDashboardDto>("/api/mercadopago/dashboard");

    /// <summary>Guarda el disponible real (copiado de la app de MP) como punto de partida del estimado.</summary>
    public async Task<bool> SetMpSaldoInicialAsync(decimal monto)
    {
        var resp = await _http.PutAsJsonAsync("/api/mercadopago/saldo-inicial", new { monto });
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return false; }
        return resp.IsSuccessStatusCode;
    }

    // --- Movimientos por reportes (Parte B) ---
    /// <summary>Pide el reporte de movimientos a MP y lo procesa. Timeout largo (asincrónico, MP tarda).</summary>
    public async Task<Web.Models.MpSyncMovResultDto?> SincronizarMpMovimientosAsync(int dias = 30)
    {
        var resp = await _httpLong.PostAsync($"/api/mercadopago/movimientos/sincronizar?dias={dias}", null);
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        await ThrowIfErrorAsync(resp);
        return await resp.Content.ReadFromJsonAsync<Web.Models.MpSyncMovResultDto>();
    }

    public async Task<List<Web.Models.MpMovimientoDto>?> GetMpMovimientosAsync(DateTime? desde = null, DateTime? hasta = null)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        var url = "/api/mercadopago/movimientos" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<Web.Models.MpMovimientoDto>>(url);
    }

    public async Task<Web.Models.MpMovResumenDto?> GetMpMovimientosResumenAsync(DateTime? desde = null, DateTime? hasta = null)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        var url = "/api/mercadopago/movimientos/resumen" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<Web.Models.MpMovResumenDto>(url);
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
    // actual seleccionado en la UI) en cada request, y para refrescar el timestamp
    // de actividad del operador (asi solo se bloquea por inactividad REAL en la app).
    private async Task SetAuthHeaderAsync()
    {
        EnsureOperatorHeader();
        await MaybeTouchOperatorActivityAsync();
    }

    // 2026-06-24: cada llamada al backend cuenta como "actividad en la app" y refresca
    // el timestamp de inactividad. Con throttle para no escribir a localStorage cada vez:
    // basta con tocar 1 vez por minuto. Si no hay operador activo, no hace nada.
    private async Task MaybeTouchOperatorActivityAsync()
    {
        if (!_operator.HasOperator) return;
        var now = DateTime.UtcNow;
        if ((now - _lastOperatorTouchUtc).TotalSeconds < OperatorTouchThrottleSeconds) return;
        _lastOperatorTouchUtc = now;
        try { await _operator.TouchAsync(); } catch { /* no romper el request si falla el touch */ }
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

    // ===== Tesoreria Cafe: Cajas =====
    public async Task<List<CafeCajaDto>?> GetCafeCajasAsync(bool incluirInactivas = false)
        => await GetAsync<List<CafeCajaDto>>($"/api/cafe/cajas?incluirInactivas={(incluirInactivas ? "true" : "false")}");
    public async Task<CafeCajaDto?> CrearCafeCajaAsync(string nombre, string tipo, decimal saldoInicial, int? orden = null, string? notas = null)
        => await PostAsync<CafeCajaDto>("/api/cafe/cajas", new { nombre, tipo, saldoInicial, orden, notas });
    public async Task<CafeCajaDto?> EditarCafeCajaAsync(int id, string nombre, string tipo, decimal saldoInicial, int? orden, bool isActive, string? notas)
        => await PutAsync<CafeCajaDto>($"/api/cafe/cajas/{id}", new { nombre, tipo, saldoInicial, orden, isActive, notas });
    public async Task<bool> EliminarCafeCajaAsync(int id)
        => await DeleteAsync($"/api/cafe/cajas/{id}");

    // ===== Tesoreria Cafe: Cobranzas =====
    public async Task<List<ComprobantePendienteDto>?> GetComprobantesPendientesAsync(int clienteId, bool incluirMismoCuit = false)
        => await GetAsync<List<ComprobantePendienteDto>>($"/api/cafe/cobranzas/comprobantes-pendientes/{clienteId}?incluirMismoCuit={incluirMismoCuit.ToString().ToLowerInvariant()}");

    /// <summary>Lista de otras sucursales que comparten el mismo CUIT con el cliente dado.</summary>
    public async Task<List<SucursalMismoCuitDto>?> GetSucursalesMismoCuitAsync(int clienteId)
        => await GetAsync<List<SucursalMismoCuitDto>>($"/api/cafe/cobranzas/sucursales-mismo-cuit/{clienteId}");
    // 2026-06-06: agregado parámetro `search` para que el buscador de la pantalla pegue al servidor
    // y encuentre cobranzas viejas que quedaron afuera del corte de 200.
    public async Task<List<CobranzaListDto>?> GetCafeCobranzasAsync(int? clienteId = null, DateTime? desde = null, DateTime? hasta = null, string? search = null)
    {
        var qs = new List<string>();
        if (clienteId.HasValue) qs.Add($"clienteId={clienteId.Value}");
        if (desde.HasValue) qs.Add($"desde={desde.Value:o}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:o}");
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        var url = "/api/cafe/cobranzas" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<CobranzaListDto>>(url);
    }
    public async Task<CobranzaDetalleDto?> GetCafeCobranzaAsync(int id)
        => await GetAsync<CobranzaDetalleDto>($"/api/cafe/cobranzas/{id}");

    public record CrearComprobanteItemRequest(int? VentaId, decimal Importe);
    public record CrearChequeItemRequest(string Numero, string Banco, string? Emisor, decimal Importe, DateTime? FechaCobro, DateTime? FechaVencimiento, string? Observaciones);
    public record CrearMedioItemRequest(int CajaId, decimal Importe, string? Referencia, CrearChequeItemRequest? Cheque);
    public record CrearCobranzaResultDto(int Id, string Numero);

    // 2026-06-06: clienteId nullable para permitir cobrar "ventas ocasionales" (sin cliente
    // del catálogo). En ese caso el backend exige al menos un comprobante con VentaId.
    public async Task<CrearCobranzaResultDto?> CrearCafeCobranzaAsync(
        int? clienteId, decimal retenciones, string? operador, string? observaciones,
        List<CrearComprobanteItemRequest> comprobantes, List<CrearMedioItemRequest> medios)
        => await PostAsync<CrearCobranzaResultDto>("/api/cafe/cobranzas",
            new { clienteId, retenciones, operador, observaciones, comprobantes, medios });
    public async Task<bool> AnularCafeCobranzaAsync(int id)
    {
        var r = await PostAsync<object>($"/api/cafe/cobranzas/{id}/anular", new { });
        return r is not null;
    }

    // 2026-06-25: Adjuntos de cobranza (comprobante de retencion, transferencia, etc.)
    public record CobranzaAdjuntoDto(int Id, int CobranzaId, string Tipo, string NombreOriginal,
        string? MimeType, long Tamano, DateTime CreatedAt);

    public async Task<List<CobranzaAdjuntoDto>?> GetCobranzaAdjuntosAsync(int cobranzaId)
        => await GetAsync<List<CobranzaAdjuntoDto>>($"/api/cafe/cobranzas/{cobranzaId}/adjuntos");

    /// <summary>Sube un archivo adjunto. Devuelve el DTO del adjunto creado o null si falla.</summary>
    public async Task<CobranzaAdjuntoDto?> SubirCobranzaAdjuntoAsync(int cobranzaId, string tipo,
        Stream stream, string fileName, string? mimeType)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        var streamContent = new StreamContent(stream);
        if (!string.IsNullOrEmpty(mimeType))
            streamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType);
        content.Add(streamContent, "file", string.IsNullOrEmpty(fileName) ? "archivo" : fileName);
        content.Add(new StringContent(tipo), "tipo");
        var resp = await _http.PostAsync($"/api/cafe/cobranzas/{cobranzaId}/adjuntos", content);
        if (!resp.IsSuccessStatusCode) return null;
        return await resp.Content.ReadFromJsonAsync<CobranzaAdjuntoDto>();
    }

    /// <summary>URL absoluta para descargar el adjunto (la cookie httpOnly viaja sola).</summary>
    public string BuildCobranzaAdjuntoDownloadUrl(int adjuntoId)
        => $"/api/cafe/cobranzas/adjuntos/{adjuntoId}/download";

    public async Task<bool> BorrarCobranzaAdjuntoAsync(int adjuntoId)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.DeleteAsync($"/api/cafe/cobranzas/adjuntos/{adjuntoId}");
        return resp.IsSuccessStatusCode;
    }

    /// <summary>Re-imputa una cobranza VIGENTE: reemplaza la lista de comprobantes (VentaId + Importe)
    /// manteniendo el mismo total. NO toca medios de cobro ni cheques. Devuelve (ok, error).</summary>
    public async Task<(bool ok, string? error)> EditarImputacionesCafeCobranzaAsync(int id, List<CrearComprobanteItemRequest> comprobantes)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PutAsJsonAsync($"/api/cafe/cobranzas/{id}/imputaciones", new { comprobantes });
        if (resp.IsSuccessStatusCode) return (true, null);
        var body = await resp.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e)) return (false, e.GetString());
        }
        catch { }
        return (false, "Error al editar imputaciones");
    }
    /// <summary>Eliminacion fisica de una cobranza ANULADA. Requiere la clave del usuario.
    /// Devuelve null en caso de error (clave incorrecta o cobranza no anulada).</summary>
    public async Task<(bool ok, string? error)> EliminarCafeCobranzaAsync(int id, string password)
    {
        try
        {
            using var msg = new HttpRequestMessage(HttpMethod.Delete, $"/api/cafe/cobranzas/{id}")
            {
                Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { password }), System.Text.Encoding.UTF8, "application/json")
            };
            var resp = await _http.SendAsync(msg);
            if (resp.IsSuccessStatusCode) return (true, null);
            var body = await resp.Content.ReadAsStringAsync();
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e)) return (false, e.GetString());
            }
            catch { }
            return (false, "Error al eliminar");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ===== Sincronizacion MeLi (publicaciones extendidas) =====
    public async Task<List<PublicacionExtendidaDto>?> GetPublicacionesSincroAsync(string? q = null, string? categoria = null, string? sortBy = null)
    {
        var qs = new List<string>();
        if (!string.IsNullOrWhiteSpace(q)) qs.Add("q=" + Uri.EscapeDataString(q));
        if (!string.IsNullOrWhiteSpace(categoria)) qs.Add("categoria=" + Uri.EscapeDataString(categoria));
        if (!string.IsNullOrWhiteSpace(sortBy)) qs.Add("sortBy=" + Uri.EscapeDataString(sortBy));
        var url = "/api/cafe/sincronizacion-meli/publicaciones" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<PublicacionExtendidaDto>>(url);
    }

    public async Task<(SyncConfigDto? config, string? error)> ActualizarSyncConfigAsync(string meliItemId, UpdateSyncConfigRequest req)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync($"/api/cafe/sincronizacion-meli/{meliItemId}/config", req);
            if (resp.IsSuccessStatusCode) return (await resp.Content.ReadFromJsonAsync<SyncConfigDto>(), null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ── 2026-06-12: precios mayoristas (PxQ) + límites por compra ──
    public record MayoristaTierDto(int MinQty, decimal Amount);
    public record MayoristaInfoDto(List<MayoristaTierDto> Tiers, int? MinPorCompra, int? MaxPorCompra,
        decimal PrecioStandard, string? StandardPriceId, bool EsAlimentos = false);

    public async Task<(MayoristaInfoDto? info, string? error)> GetMayoristaAsync(string meliItemId)
    {
        try
        {
            var resp = await _http.GetAsync($"/api/meli/items/{meliItemId}/mayorista");
            if (resp.IsSuccessStatusCode) return (await resp.Content.ReadFromJsonAsync<MayoristaInfoDto>(), null);
            return (null, await LeerErrorMayoristaAsync(resp));
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<(MayoristaInfoDto? info, string? error)> SaveMayoristaAsync(string meliItemId,
        List<MayoristaTierDto> tiers, int? minPorCompra, int? maxPorCompra)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync($"/api/meli/items/{meliItemId}/mayorista",
                new { Tiers = tiers, MinPorCompra = minPorCompra, MaxPorCompra = maxPorCompra });
            if (resp.IsSuccessStatusCode) return (await resp.Content.ReadFromJsonAsync<MayoristaInfoDto>(), null);
            return (null, await LeerErrorMayoristaAsync(resp));
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    private static async Task<string> LeerErrorMayoristaAsync(HttpResponseMessage resp)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.TryGetProperty("error", out var e)) return e.GetString() ?? "Error";
        }
        catch { }
        return $"Error {(int)resp.StatusCode}";
    }

    public record StockFullResp(int? StockFull);

    /// <summary>2026-06-12: stock en bodega Full MeLi de los productos linkeados a la MLA (informativo).</summary>
    public async Task<int?> GetStockFullAsync(string meliItemId)
    {
        try
        {
            var r = await _http.GetFromJsonAsync<StockFullResp>($"/api/meli/items/{meliItemId}/stock-full");
            return r?.StockFull;
        }
        catch { return null; }
    }

    public async Task<(UpdatePrecioResultDto? result, string? error)> PushPrecioAMeliAsync(string meliItemId, decimal precio, decimal? gananciaObjetivoPct = null)
    {
        try
        {
            var body = new { precio, gananciaObjetivoPct };
            var resp = await _http.PutAsJsonAsync($"/api/cafe/sincronizacion-meli/{meliItemId}/precio", body);
            if (resp.IsSuccessStatusCode) return (await resp.Content.ReadFromJsonAsync<UpdatePrecioResultDto>(), null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ===== Tesoreria Cafe: Bancos (catalogo) =====
    public async Task<List<CafeBancoDto>?> GetBancosAsync(bool incluirInactivos = false)
        => await GetAsync<List<CafeBancoDto>>($"/api/cafe/bancos?incluirInactivos={(incluirInactivos ? "true" : "false")}");

    public async Task<(CafeBancoDto? banco, string? error)> CrearBancoAsync(CreateBancoRequest req)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/cafe/bancos", req);
            if (resp.IsSuccessStatusCode)
                return (await resp.Content.ReadFromJsonAsync<CafeBancoDto>(), null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<(CafeBancoDto? banco, string? error)> ActualizarBancoAsync(int id, UpdateBancoRequest req)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync($"/api/cafe/bancos/{id}", req);
            if (resp.IsSuccessStatusCode)
                return (await resp.Content.ReadFromJsonAsync<CafeBancoDto>(), null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<(bool ok, string? error)> EliminarBancoAsync(int id)
    {
        try
        {
            var resp = await _http.DeleteAsync($"/api/cafe/bancos/{id}");
            if (resp.IsSuccessStatusCode) return (true, null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (false, err);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ===== 2026-06-15: PIN por operador =====
    /// <summary>Valida el PIN del operador. Devuelve (ok, mensajeError).</summary>
    public async Task<(bool ok, string? error)> ValidarOperadorPinAsync(string nombre, string pin)
    {
        try
        {
            await SetAuthHeaderAsync();
            var resp = await _http.PostAsJsonAsync("/api/operadores/pin/validar", new { Nombre = nombre, Pin = pin });
            if (resp.IsSuccessStatusCode) return (true, null);
            string msg = "PIN incorrecto";
            try
            {
                var body = await resp.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e)) msg = e.GetString() ?? msg;
            }
            catch { }
            return (false, msg);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>El operador cambia su propio PIN — pide el actual + el nuevo.</summary>
    public async Task<(bool ok, string? error)> CambiarOperadorPinAsync(string nombre, string pinActual, string pinNuevo)
    {
        try
        {
            await SetAuthHeaderAsync();
            var resp = await _http.PostAsJsonAsync("/api/operadores/pin/cambiar",
                new { Nombre = nombre, PinActual = pinActual, PinNuevo = pinNuevo });
            if (resp.IsSuccessStatusCode) return (true, null);
            string msg = "No se pudo cambiar";
            try
            {
                var body = await resp.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e)) msg = e.GetString() ?? msg;
            }
            catch { }
            return (false, msg);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public record OperadorPinInfoDto(string Nombre, bool TienePin, DateTime? UpdatedAt, string? UpdatedBy);

    public async Task<List<OperadorPinInfoDto>?> GetOperadoresPinListaAsync()
        => await GetAsync<List<OperadorPinInfoDto>>("/api/operadores/admin/lista");

    public async Task<(bool ok, string? error)> ResetOperadorPinAsync(string nombre, string pinNuevo)
    {
        try
        {
            await SetAuthHeaderAsync();
            var resp = await _http.PostAsJsonAsync("/api/operadores/admin/reset",
                new { Nombre = nombre, PinNuevo = pinNuevo });
            if (resp.IsSuccessStatusCode) return (true, null);
            string msg = "No se pudo resetear";
            try
            {
                var body = await resp.Content.ReadAsStringAsync();
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var e)) msg = e.GetString() ?? msg;
            }
            catch { }
            return (false, msg);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ===== 2026-06-15: Reportes de stock para reposición y ranking =====
    public record ReposicionRow(
        int ProductoId, string? Sku, string Nombre, string? Marca, string? OemCodigo, string? Categoria,
        int StockActual, int? StockMinimo,
        decimal Entradas, decimal Salidas, decimal Ajustes, decimal Neto,
        int VendidoUnidades, decimal FacturadoTotal,
        int SugeridoReponer);
    public record ReposicionResult(int Total, List<ReposicionRow> Filas);

    public record RankingRow(
        int ProductoId, string? Sku, string Nombre, string? Marca, string? OemCodigo, string? Categoria,
        int StockActual,
        int VendidoUnidades, decimal FacturadoTotal, decimal MargenTotal,
        int ClientesUnicos, int CantidadVentas);
    public record RankingResult(int Total, List<RankingRow> Filas);

    private static string BuildReporteQuery(DateTime? desde, DateTime? hasta, int? marcaId, string? marcaTexto,
        int? oemId, string? oemCodigo, string? sku, string? categoria, int? clienteId, string? tiposMov)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={Uri.EscapeDataString(desde.Value.ToString("o"))}");
        if (hasta.HasValue) qs.Add($"hasta={Uri.EscapeDataString(hasta.Value.ToString("o"))}");
        if (marcaId.HasValue) qs.Add($"marcaId={marcaId}");
        if (!string.IsNullOrWhiteSpace(marcaTexto)) qs.Add($"marcaTexto={Uri.EscapeDataString(marcaTexto)}");
        if (oemId.HasValue) qs.Add($"oemId={oemId}");
        if (!string.IsNullOrWhiteSpace(oemCodigo)) qs.Add($"oemCodigo={Uri.EscapeDataString(oemCodigo)}");
        if (!string.IsNullOrWhiteSpace(sku)) qs.Add($"sku={Uri.EscapeDataString(sku)}");
        if (!string.IsNullOrWhiteSpace(categoria)) qs.Add($"categoria={Uri.EscapeDataString(categoria)}");
        if (clienteId.HasValue) qs.Add($"clienteId={clienteId}");
        if (!string.IsNullOrWhiteSpace(tiposMov)) qs.Add($"tiposMov={Uri.EscapeDataString(tiposMov)}");
        return qs.Count > 0 ? "?" + string.Join("&", qs) : "";
    }

    public async Task<ReposicionResult?> GetReposicionAsync(DateTime? desde = null, DateTime? hasta = null,
        int? marcaId = null, string? marcaTexto = null, int? oemId = null, string? oemCodigo = null,
        string? sku = null, string? categoria = null, int? clienteId = null, string? tiposMov = null)
    {
        var qs = BuildReporteQuery(desde, hasta, marcaId, marcaTexto, oemId, oemCodigo, sku, categoria, clienteId, tiposMov);
        return await GetAsync<ReposicionResult>("/api/stock/reportes/reposicion" + qs);
    }

    public async Task<RankingResult?> GetRankingAsync(DateTime? desde = null, DateTime? hasta = null,
        int? marcaId = null, string? marcaTexto = null, int? oemId = null, string? oemCodigo = null,
        string? sku = null, string? categoria = null, int? clienteId = null,
        string? orderBy = "unidades", int top = 50)
    {
        var qs = BuildReporteQuery(desde, hasta, marcaId, marcaTexto, oemId, oemCodigo, sku, categoria, clienteId, null);
        var sep = string.IsNullOrEmpty(qs) ? "?" : "&";
        qs += $"{sep}orderBy={Uri.EscapeDataString(orderBy ?? "unidades")}&top={top}";
        return await GetAsync<RankingResult>("/api/stock/reportes/ranking" + qs);
    }

    // ===== 2026-06-05: Validar clave para operador protegido (OSMAR) =====
    /// <summary>Devuelve true si la clave coincide con la de eliminar ventas (la misma se usa
    /// para activar OSMAR como operador). Si la clave es invalida o no esta configurada, false.</summary>
    public async Task<bool> ValidateProtectedOperatorAsync(string password)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/cafe/ventas/operador-protegido/validar",
                new { Password = password });
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ===== 2026-06-05: Catalogo de Servicios (envio, mano de obra, etc) =====
    public async Task<List<CafeServicioDto>?> GetCafeServiciosAsync(bool incluirInactivos = false)
        => await GetAsync<List<CafeServicioDto>>($"/api/cafe/servicios?incluirInactivos={(incluirInactivos ? "true" : "false")}");

    public async Task<(CafeServicioDto? servicio, string? error)> CrearCafeServicioAsync(CafeServicioUpsertRequest req)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/cafe/servicios", req);
            if (resp.IsSuccessStatusCode)
                return (await resp.Content.ReadFromJsonAsync<CafeServicioDto>(), null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<(CafeServicioDto? servicio, string? error)> UpdateCafeServicioAsync(int id, CafeServicioUpsertRequest req)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync($"/api/cafe/servicios/{id}", req);
            if (resp.IsSuccessStatusCode)
                return (await resp.Content.ReadFromJsonAsync<CafeServicioDto>(), null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<(bool ok, string? error)> EliminarCafeServicioAsync(int id)
    {
        try
        {
            var resp = await _http.DeleteAsync($"/api/cafe/servicios/{id}");
            if (resp.IsSuccessStatusCode) return (true, null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (false, err);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ===== Tesoreria Cafe: Cheques =====
    public async Task<List<CafeChequeDto>?> GetCafeChequesAsync(string? estado = null)
        => await GetAsync<List<CafeChequeDto>>("/api/cafe/cheques" + (string.IsNullOrWhiteSpace(estado) ? "" : $"?estado={Uri.EscapeDataString(estado)}"));
    /// <summary>Alta manual de cheque (papel) que entra a cartera. Devuelve (cheque, error).
    /// El error se llena cuando el backend responde con un duplicado (409) o validación (400).</summary>
    public async Task<(CafeChequeDto? cheque, string? error)> CrearChequeAsync(CreateChequeRequest req)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/cafe/cheques", req);
            if (resp.IsSuccessStatusCode)
            {
                var ch = await resp.Content.ReadFromJsonAsync<CafeChequeDto>();
                return (ch, null);
            }
            string err = "Error";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err;
            }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }
    public async Task<bool> DepositarChequeAsync(int id, string? observaciones = null, int? cajaDestinoId = null)
    {
        var r = await PostAsync<object>($"/api/cafe/cheques/{id}/depositar", new { observaciones, cajaDestinoId });
        return r is not null;
    }
    public async Task<bool> CobrarChequeVentanillaAsync(int id, string? observaciones = null)
    {
        var r = await PostAsync<object>($"/api/cafe/cheques/{id}/cobrar-ventanilla", new { observaciones });
        return r is not null;
    }
    public async Task<bool> RechazarChequeAsync(int id, string? observaciones = null)
    {
        var r = await PostAsync<object>($"/api/cafe/cheques/{id}/rechazar", new { observaciones });
        return r is not null;
    }

    // ===== Estado de cuenta del cliente =====
    public async Task<EstadoCuentaDto?> GetEstadoCuentaClienteAsync(int clienteId)
        => await GetAsync<EstadoCuentaDto>($"/api/cafe/clientes/{clienteId}/estado-cuenta");

    /// <summary>URL para descargar el PDF del recibo de una cobranza.</summary>
    public string GetCobranzaPdfUrl(int cobranzaId) => $"/api/cafe/cobranzas/{cobranzaId}/pdf";

    /// <summary>Saldos pendientes de cobro por cada venta (para mostrar en el listado de ventas).</summary>
    public async Task<List<VentaSaldoDto>?> GetVentasSaldosAsync(DateTime? from = null, DateTime? to = null)
    {
        var qs = new List<string>();
        if (from.HasValue) qs.Add($"from={from.Value:o}");
        if (to.HasValue) qs.Add($"to={to.Value:o}");
        var url = "/api/cafe/ventas/saldos" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<VentaSaldoDto>>(url);
    }

    // ===== Tesoreria Cafe: Pagos a proveedores =====
    public async Task<List<CompraPendienteDto>?> GetComprasPendientesAsync(int proveedorId)
        => await GetAsync<List<CompraPendienteDto>>($"/api/cafe/pagos-proveedor/comprobantes-pendientes/{proveedorId}");
    public async Task<List<PagoListDto>?> GetCafePagosProveedorAsync(int? proveedorId = null, DateTime? desde = null, DateTime? hasta = null)
    {
        var qs = new List<string>();
        if (proveedorId.HasValue) qs.Add($"proveedorId={proveedorId.Value}");
        if (desde.HasValue) qs.Add($"desde={desde.Value:o}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:o}");
        var url = "/api/cafe/pagos-proveedor" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<List<PagoListDto>>(url);
    }
    public record CrearMedioPagoRequest(int CajaId, decimal Importe, string? Referencia, int? ChequeExistenteId);
    public record CrearCompraItemRequest(int? CompraId, decimal Importe);
    public record CrearPagoResultDto(int Id, string Numero);
    public async Task<CrearPagoResultDto?> CrearCafePagoProveedorAsync(
        int proveedorId, decimal retenciones, string? operador, string? observaciones,
        List<CrearCompraItemRequest> comprobantes, List<CrearMedioPagoRequest> medios)
        => await PostAsync<CrearPagoResultDto>("/api/cafe/pagos-proveedor",
            new { proveedorId, retenciones, operador, observaciones, comprobantes, medios });
    public async Task<bool> AnularCafePagoProveedorAsync(int id)
    {
        var r = await PostAsync<object>($"/api/cafe/pagos-proveedor/{id}/anular", new { });
        return r is not null;
    }

    public async Task<EstadoCuentaProvDto?> GetEstadoCuentaProveedorAsync(int id)
        => await GetAsync<EstadoCuentaProvDto>($"/api/cafe/proveedores/{id}/estado-cuenta");

    // ===== Pagos Movil (precargar desde el celu, confirmar en la PC) =====
    public async Task<List<EmpleadoActivoDto>?> GetPagosMovilEmpleadosActivosAsync()
        => await GetAsync<List<EmpleadoActivoDto>>("/api/pagos-movil/empleados-activos");
    public async Task<List<ProveedorConDeudaDto>?> GetPagosMovilProveedoresConDeudaAsync()
        => await GetAsync<List<ProveedorConDeudaDto>>("/api/pagos-movil/proveedores-con-deuda");
    public async Task<List<CompraPendientePagoDto>?> GetPagosMovilComprasPendientesAsync(int proveedorId)
        => await GetAsync<List<CompraPendientePagoDto>>($"/api/pagos-movil/proveedor/{proveedorId}/compras-pendientes");
    public async Task<bool> PrecargarPagoEmpleadoAsync(int empleadoId, string concepto, decimal monto, string medioPago, string? notas)
        => await PostAsync<object>("/api/pagos-movil/empleado",
            new { empleadoId, concepto, monto, medioPago, notas }) is not null;
    public async Task<bool> PrecargarPagoFacturaAsync(int proveedorId, List<PrecargarFacturaItem> comprobantes, string medioPago, string? notas)
        => await PostAsync<object>("/api/pagos-movil/factura",
            new { proveedorId, comprobantes, medioPago, notas }) is not null;
    public async Task<List<PendienteListDto>?> GetPagosMovilPendientesAsync()
        => await GetAsync<List<PendienteListDto>>("/api/pagos-movil/pendientes");
    public async Task<int?> GetPagosMovilPendientesCountAsync()
    {
        var r = await GetAsync<CountResult>("/api/pagos-movil/pendientes/count");
        return r?.Count;
    }
    public async Task<PendienteDetalleDto?> GetPagosMovilPendienteDetalleAsync(int id)
        => await GetAsync<PendienteDetalleDto>($"/api/pagos-movil/pendientes/{id}");
    public async Task<bool> ConfirmarPagoMovilAsync(int id, int? cajaId, DateTime? fechaPago)
        => await PostAsync<object>($"/api/pagos-movil/pendientes/{id}/confirmar",
            new { cajaId, fechaPago }) is not null;
    public async Task<bool> RechazarPagoMovilAsync(int id, string? motivo)
        => await PostAsync<object>($"/api/pagos-movil/pendientes/{id}/rechazar",
            new { motivo }) is not null;
    public async Task<bool> EditarPagoMovilAsync(int id, EditarPagoMovilRequest req)
        => await PutAsync<object>($"/api/pagos-movil/pendientes/{id}", req) is not null;
    private record CountResult(int Count);

    // ===== Cafe: Depositos =====
    public async Task<List<CafeDepositoDto>?> GetCafeDepositosAsync(bool incluirInactivos = false)
        => await GetAsync<List<CafeDepositoDto>>($"/api/cafe/depositos?incluirInactivos={(incluirInactivos ? "true" : "false")}");
    public async Task<CafeDepositoDto?> CrearCafeDepositoAsync(string nombre, string? direccion, string? notas, int? orden)
        => await PostAsync<CafeDepositoDto>("/api/cafe/depositos", new { nombre, direccion, notas, orden });
    public async Task<CafeDepositoDto?> EditarCafeDepositoAsync(int id, string nombre, string? direccion, string? notas, int? orden, bool isActive)
        => await PutAsync<CafeDepositoDto>($"/api/cafe/depositos/{id}", new { nombre, direccion, notas, orden, isActive });
    public async Task<bool> EliminarCafeDepositoAsync(int id) => await DeleteAsync($"/api/cafe/depositos/{id}");

    // ===== Cafe: Stock masivo =====
    public async Task<List<StockProductoDto>?> GetStockEnDepositoAsync(int depositoId)
        => await GetAsync<List<StockProductoDto>>($"/api/cafe/stock-masivo/{depositoId}");
    public record StockMasivoItemReq(int ProductoId, decimal StockGramos, int StockUnidades);
    public async Task<bool> ActualizarStockMasivoAsync(int depositoId, List<StockMasivoItemReq> items)
    {
        var r = await PostAsync<object>("/api/cafe/stock-masivo", new { depositoId, items });
        return r is not null;
    }

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

    // ===== MeLi ME1 (envios manuales del vendedor) =====
    public async Task<List<MeliMe1ShipmentDto>?> GetMeliMe1ShipmentsAsync(string filter = "todos", int take = 500)
        => await GetAsync<List<MeliMe1ShipmentDto>>($"/api/meli/me1/shipments?filter={Uri.EscapeDataString(filter)}&take={take}");

    public async Task<MeliMe1SyncResultDto?> SyncMeliMe1ShipmentsAsync(int days = 30, int maxOrders = 300)
        => await PostAsync<MeliMe1SyncResultDto>("/api/meli/me1/sync", new { days, maxOrders });

    /// <summary>2026-06-17: asigna (o desasigna con null) un repartidor a un envio ME1.</summary>
    public async Task<bool> AsignarRepartidorMe1Async(int shipmentId, int? repartidorId)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/meli/me1/shipments/{shipmentId}/asignar-repartidor",
            new { repartidorId });
        return resp.IsSuccessStatusCode;
    }

    /// <summary>2026-06-08: importa un envío puntual por número de ORDEN MeLi.
    /// Útil cuando el sync masivo no la trae por filtros.</summary>
    public async Task<(bool ok, string mensaje)> ImportMeliMe1ByOrderIdAsync(string orderId)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/meli/me1/import-by-order", new { orderId });
            var bodyTxt = await resp.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(bodyTxt);
            var root = doc.RootElement;
            if (resp.IsSuccessStatusCode)
            {
                var msg = root.TryGetProperty("mensaje", out var m) ? m.GetString() : "OK";
                return (true, msg ?? "Importado");
            }
            else
            {
                var err = root.TryGetProperty("error", out var e) ? e.GetString() : "Error desconocido";
                return (false, err ?? "Error");
            }
        }
        catch (Exception ex) { return (false, $"Error de conexión: {ex.Message}"); }
    }

    public async Task<(bool ok, string? error)> SetMeliMe1ShipmentStatusAsync(int id, string status, string? substatus, string? trackingNumber = null, string? trackingUrl = null, string? comment = null)
    {
        try
        {
            var r = await PostAsync<object>($"/api/meli/me1/shipments/{id}/status", new { status, substatus, trackingNumber, trackingUrl, comment });
            return (r is not null, null);
        }
        catch (HttpRequestException ex) { return (false, ex.Message); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Cambia el estado de un envio ME1 usando el MeliShipmentId (long). Si no esta en la base local,
    /// el backend lo sincroniza desde MeLi antes de cambiar el estado.
    /// </summary>
    public async Task<(bool ok, string? error)> SetMeliMe1ShipmentStatusByMeliIdAsync(long meliShipmentId, string status, string? substatus, string? trackingNumber = null, string? trackingUrl = null, string? comment = null)
    {
        try
        {
            var r = await PostAsync<object>($"/api/meli/me1/by-meli-id/{meliShipmentId}/status", new { status, substatus, trackingNumber, trackingUrl, comment });
            return (r is not null, null);
        }
        catch (HttpRequestException ex) { return (false, ex.Message); }
        catch (Exception ex) { return (false, ex.Message); }
    }

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

    // ===== Sitios (marcas / landings) =====
    public async Task<List<SitioDto>?> GetSitiosAsync()
        => await GetAsync<List<SitioDto>>("/api/sitios");

    public async Task<(SitioDto? sitio, string? error)> CrearSitioAsync(SitioUpsertRequest req)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/sitios", req);
            if (resp.IsSuccessStatusCode) return (await resp.Content.ReadFromJsonAsync<SitioDto>(), null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<(SitioDto? sitio, string? error)> ActualizarSitioAsync(int id, SitioUpsertRequest req)
    {
        try
        {
            var resp = await _http.PutAsJsonAsync($"/api/sitios/{id}", req);
            if (resp.IsSuccessStatusCode) return (await resp.Content.ReadFromJsonAsync<SitioDto>(), null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<bool> EliminarSitioAsync(int id)
    {
        var resp = await _http.DeleteAsync($"/api/sitios/{id}");
        return resp.IsSuccessStatusCode;
    }

    public async Task<List<SitioUploadDto>?> GetSitiosUploadsAsync()
        => await GetAsync<List<SitioUploadDto>>("/api/sitios/uploads");

    public async Task<(string? url, string? error)> SubirImagenSitioAsync(System.IO.Stream contenido, string filename, string? slug)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var streamContent = new StreamContent(contenido);
            content.Add(streamContent, "file", filename);
            if (!string.IsNullOrWhiteSpace(slug))
                content.Add(new StringContent(slug), "slug");
            var resp = await _http.PostAsync("/api/sitios/upload", content);
            if (resp.IsSuccessStatusCode)
            {
                var dto = await resp.Content.ReadFromJsonAsync<SitioUploadResponse>();
                return (dto?.Url, null);
            }
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                  if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; }
            catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public async Task<bool> BorrarUploadSitioAsync(string filename)
    {
        var resp = await _http.DeleteAsync($"/api/sitios/uploads/{Uri.EscapeDataString(filename)}");
        return resp.IsSuccessStatusCode;
    }

    // ===== WhatsApp Twilio chat =====
    public record TwConvDto(string Numero, string? NombrePerfil, string? Rol, int? ClienteId, string? ClienteNombre, string? UltimoMensaje, string? UltimoDireccion, DateTime UltimoAt, int Total);
    public record TwReaccionDto(string Emoji, int Count);
    public record TwMsgDto(int Id, string Direccion, string Numero, string? NombrePerfil, string? Cuerpo, string? MediaUrl, int? NumMedia, bool Procesado, string? RespuestaEnviada, DateTime CreatedAt, List<TwReaccionDto>? Reacciones);
    public record TwRespRapidaDto(int Id, string Nombre, string Texto, int Orden, bool Activo);
    public record TwContactoDto(int Id, string Numero, string Nombre, string Rol, string? Notas, bool Activo, int? ClienteId, string? ClienteNombre, string? ClienteCodigo);
    public record TwRespUpsert(string Nombre, string Texto, int Orden, bool Activo);
    public record TwContactoUpsert(string Numero, string Nombre, string Rol, string? Notas, bool Activo, int? ClienteId);
    public record TwClienteBuscarDto(int Id, string Nombre, string? CodigoInterno, string? Telefono);
    public async Task<List<TwConvDto>> GetTwConversacionesAsync()
        => await _http.GetFromJsonAsync<List<TwConvDto>>("/api/whatsapp/twilio/conversaciones") ?? new();

    public async Task<List<TwMsgDto>> GetTwMensajesAsync(string numero)
        => await _http.GetFromJsonAsync<List<TwMsgDto>>($"/api/whatsapp/twilio/mensajes?numero={Uri.EscapeDataString(numero)}") ?? new();

    public async Task<(bool ok, string? error)> SendTwMensajeAsync(string numero, string mensaje)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/whatsapp/twilio/send", new { Numero = numero, Mensaje = mensaje });
            if (resp.IsSuccessStatusCode) return (true, null);
            string err = "Error enviando";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err;
            }
            catch { }
            return (false, err);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool ok, string? error)> SendTwMenuRolAsync(string numero)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/whatsapp/twilio/menu-rol", new { Numero = numero });
            if (resp.IsSuccessStatusCode) return (true, null);
            string err = "Error enviando menú";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err;
            }
            catch { }
            return (false, err);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<List<TwRespRapidaDto>> GetTwRespuestasAsync()
        => await _http.GetFromJsonAsync<List<TwRespRapidaDto>>("/api/whatsapp/twilio/respuestas-rapidas") ?? new();
    public async Task<bool> CreateTwRespuestaAsync(TwRespUpsert r)
        => (await _http.PostAsJsonAsync("/api/whatsapp/twilio/respuestas-rapidas", r)).IsSuccessStatusCode;
    public async Task<bool> UpdateTwRespuestaAsync(int id, TwRespUpsert r)
        => (await _http.PutAsJsonAsync($"/api/whatsapp/twilio/respuestas-rapidas/{id}", r)).IsSuccessStatusCode;
    public async Task<bool> DeleteTwRespuestaAsync(int id)
        => (await _http.DeleteAsync($"/api/whatsapp/twilio/respuestas-rapidas/{id}")).IsSuccessStatusCode;

    public async Task<List<TwContactoDto>> GetTwContactosAsync()
        => await _http.GetFromJsonAsync<List<TwContactoDto>>("/api/whatsapp/twilio/contactos") ?? new();
    public async Task<(bool ok, string? error)> CreateTwContactoAsync(TwContactoUpsert c)
    {
        var resp = await _http.PostAsJsonAsync("/api/whatsapp/twilio/contactos", c);
        if (resp.IsSuccessStatusCode) return (true, null);
        string err = "Error";
        try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()); if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; } catch { }
        return (false, err);
    }
    public async Task<bool> UpdateTwContactoAsync(int id, TwContactoUpsert c)
        => (await _http.PutAsJsonAsync($"/api/whatsapp/twilio/contactos/{id}", c)).IsSuccessStatusCode;
    public async Task<bool> DeleteTwContactoAsync(int id)
        => (await _http.DeleteAsync($"/api/whatsapp/twilio/contactos/{id}")).IsSuccessStatusCode;

    public async Task<List<TwClienteBuscarDto>> BuscarTwClientesAsync(string q)
        => await _http.GetFromJsonAsync<List<TwClienteBuscarDto>>($"/api/whatsapp/twilio/clientes-buscar?q={Uri.EscapeDataString(q ?? "")}") ?? new();

    public async Task<bool> ToggleReaccionAsync(int mensajeId, string emoji)
        => (await _http.PostAsJsonAsync("/api/whatsapp/twilio/reacciones", new { MensajeId = mensajeId, Emoji = emoji })).IsSuccessStatusCode;

    public record TwUploadResp(string Token, string Url, string OriginalFilename, long SizeBytes, string ContentType, DateTime ExpiresAt);
    public async Task<(TwUploadResp? resp, string? error)> UploadTwArchivoAsync(System.IO.Stream stream, string filename, string contentType)
    {
        try
        {
            using var content = new MultipartFormDataContent();
            var file = new StreamContent(stream);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
            content.Add(file, "file", filename);
            var resp = await _http.PostAsync("/api/whatsapp/twilio/upload", content);
            if (resp.IsSuccessStatusCode)
            {
                var dto = await resp.Content.ReadFromJsonAsync<TwUploadResp>();
                return (dto, null);
            }
            string err = "Error subiendo archivo";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()); if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; } catch { }
            return (null, err);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public record TwServerFileDto(string Tipo, int Id, string Label, string? SubLabel, string? Info, DateTime Fecha);
    public async Task<List<TwServerFileDto>> GetTwServerFilesAsync(string tipo, string? search = null)
    {
        try
        {
            var url = $"/api/whatsapp/twilio/server-files?tipo={Uri.EscapeDataString(tipo)}";
            if (!string.IsNullOrWhiteSpace(search)) url += $"&search={Uri.EscapeDataString(search)}";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return new();
            return await resp.Content.ReadFromJsonAsync<List<TwServerFileDto>>() ?? new();
        }
        catch { return new(); }
    }

    public async Task<(bool ok, string? error)> SendTwServerFileAsync(string numero, string tipo, int id, string? caption)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/whatsapp/twilio/send-server-file",
                new { Numero = numero, Tipo = tipo, Id = id, Caption = caption });
            if (resp.IsSuccessStatusCode) return (true, null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()); if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; } catch { }
            return (false, err);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<(bool ok, string? error)> SendTwMediaAsync(string numero, string mediaUrl, string? caption, string? originalFilename)
    {
        try
        {
            var resp = await _http.PostAsJsonAsync("/api/whatsapp/twilio/send-media",
                new { Numero = numero, MediaUrl = mediaUrl, Caption = caption, OriginalFilename = originalFilename });
            if (resp.IsSuccessStatusCode) return (true, null);
            string err = "Error";
            try { using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync()); if (doc.RootElement.TryGetProperty("error", out var e)) err = e.GetString() ?? err; } catch { }
            return (false, err);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ───────── Contadora: ventas por jurisdiccion (Ingresos Brutos) ─────────
    public async Task<ContadoraJurisdiccionDto?> GetVentasJurisdiccionAsync(DateTime? desde, DateTime? hasta)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        var url = "/api/contadora/jurisdiccion" + (qs.Count > 0 ? "?" + string.Join("&", qs) : "");
        return await GetAsync<ContadoraJurisdiccionDto>(url);
    }

    public async Task<ContadoraBackfillResultDto?> BackfillProvinciasAsync(int lote = 150)
        => await PostAsync<ContadoraBackfillResultDto>($"/api/contadora/backfill-provincias?lote={lote}", new { });

    // ───────── Contadora etapa 2: Libro IVA Ventas ─────────
    private static string ContadoraQs(DateTime? desde, DateTime? hasta, string? empresa, int? puntoVenta, string? letra, string? provincia, string? search)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(empresa)) qs.Add($"empresa={Uri.EscapeDataString(empresa)}");
        if (puntoVenta.HasValue) qs.Add($"puntoVenta={puntoVenta.Value}");
        if (!string.IsNullOrWhiteSpace(letra)) qs.Add($"letra={Uri.EscapeDataString(letra)}");
        if (!string.IsNullOrWhiteSpace(provincia)) qs.Add($"provincia={Uri.EscapeDataString(provincia)}");
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        return qs.Count > 0 ? "?" + string.Join("&", qs) : "";
    }

    public async Task<List<ContadoraEmpresaDto>?> GetContadoraEmpresasAsync()
        => await GetAsync<List<ContadoraEmpresaDto>>("/api/contadora/empresas");

    public async Task<ContadoraBackfillResultDto?> BackfillFacturasAsync(int lote = 120)
        => await PostAsync<ContadoraBackfillResultDto>($"/api/contadora/backfill-facturas?lote={lote}", new { });

    /// <summary>Dispara el robot en el servidor (provincias + facturas). Corre en segundo plano.</summary>
    public async Task<bool> RunContadoraRobotAsync()
    {
        try { await PostAsync<object>("/api/contadora/run-robot", new { }); return true; }
        catch { return false; }
    }

    public async Task<ContadoraLibroIvaDto?> GetLibroIvaAsync(DateTime? desde, DateTime? hasta, string? empresa, int? puntoVenta, string? letra, string? provincia, string? search)
        => await GetAsync<ContadoraLibroIvaDto>("/api/contadora/libro-iva" + ContadoraQs(desde, hasta, empresa, puntoVenta, letra, provincia, search));

    public async Task<ContadoraFacturasPageDto?> GetContadoraFacturasAsync(DateTime? desde, DateTime? hasta, string? empresa, int? puntoVenta, string? letra, string? provincia, string? search, int page = 1, int pageSize = 50)
    {
        var qs = ContadoraQs(desde, hasta, empresa, puntoVenta, letra, provincia, search);
        qs += (qs.Length > 0 ? "&" : "?") + $"page={page}&pageSize={pageSize}";
        return await GetAsync<ContadoraFacturasPageDto>("/api/contadora/facturas" + qs);
    }

    // ───────── Contadora etapa 3: importar reporte oficial de MeLi (con notas de credito) ─────────

    /// <summary>Importa los .xlsx que ya estan en la subcarpeta de la Carpeta Compartida.</summary>
    public async Task<ContadoraImportResultDto?> ImportarReporteCarpetaAsync(string? subcarpeta = null)
    {
        var url = "/api/contadora/importar-reporte-carpeta" + (string.IsNullOrWhiteSpace(subcarpeta) ? "" : "?subcarpeta=" + Uri.EscapeDataString(subcarpeta));
        return await PostAsync<ContadoraImportResultDto>(url, new { });
    }

    /// <summary>Importa archivos de reporte subidos por el usuario (multipart).</summary>
    public async Task<(ContadoraImportResultDto? result, string? error)> ImportarReporteArchivosAsync(IEnumerable<(string name, Stream stream)> archivos)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        foreach (var f in archivos)
        {
            var sc = new StreamContent(f.stream);
            sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            content.Add(sc, "archivos", f.name);
        }
        var resp = await _http.PostAsync("/api/contadora/importar-reporte", content);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            return (null, $"HTTP {(int)resp.StatusCode}: {body}");
        }
        var data = await resp.Content.ReadFromJsonAsync<ContadoraImportResultDto>();
        return (data, null);
    }

    private static string ConOrigen(string qs, string? origen)
        => string.IsNullOrWhiteSpace(origen) ? qs : qs + (qs.Length > 0 ? "&" : "?") + "origen=" + Uri.EscapeDataString(origen);

    public async Task<ContadoraReporteResumenDto?> GetContadoraReporteResumenAsync(DateTime? desde, DateTime? hasta, string? empresa, int? puntoVenta, string? letra, string? provincia, string? search, string? origen = null)
        => await GetAsync<ContadoraReporteResumenDto>("/api/contadora/reporte/resumen" + ConOrigen(ContadoraQs(desde, hasta, empresa, puntoVenta, letra, provincia, search), origen));

    public async Task<List<ContadoraCargaDto>?> GetContadoraReporteCargasAsync(string? empresa = null, string? origen = null)
    {
        var qs = string.IsNullOrWhiteSpace(empresa) ? "" : "?empresa=" + Uri.EscapeDataString(empresa);
        return await GetAsync<List<ContadoraCargaDto>>("/api/contadora/reporte/cargas" + ConOrigen(qs, origen));
    }

    public async Task<List<ContadoraEmpresaDto>?> GetContadoraReporteEmpresasAsync()
        => await GetAsync<List<ContadoraEmpresaDto>>("/api/contadora/reporte/empresas");

    public async Task<List<string>?> GetContadoraReporteProvinciasAsync()
        => await GetAsync<List<string>>("/api/contadora/reporte/provincias");

    /// <summary>Trae al Libro IVA las facturas propias del sistema (AFIP).</summary>
    public async Task<ContadoraImportResultDto?> SincronizarSistemaAsync()
        => await PostAsync<ContadoraImportResultDto>("/api/contadora/sincronizar-sistema", new { });

    public async Task<ContadoraComprobantesPageDto?> GetContadoraReporteComprobantesAsync(DateTime? desde, DateTime? hasta, string? empresa, int? puntoVenta, string? letra, string? provincia, string? search, int page = 1, int pageSize = 50, string? origen = null)
    {
        var qs = ContadoraQs(desde, hasta, empresa, puntoVenta, letra, provincia, search);
        qs += (qs.Length > 0 ? "&" : "?") + $"page={page}&pageSize={pageSize}";
        return await GetAsync<ContadoraComprobantesPageDto>("/api/contadora/reporte/comprobantes" + ConOrigen(qs, origen));
    }

    // ───────── Contadora: COMPRAS (AFIP recibidos) + BALANZA ─────────

    public async Task<ContadoraImportResultDto?> ImportarComprasCarpetaAsync(string? subcarpeta = null)
        => await PostAsync<ContadoraImportResultDto>("/api/contadora/importar-compras-carpeta" + (string.IsNullOrWhiteSpace(subcarpeta) ? "" : "?subcarpeta=" + Uri.EscapeDataString(subcarpeta)), new { });

    public async Task<(ContadoraImportResultDto? result, string? error)> ImportarComprasArchivosAsync(IEnumerable<(string name, Stream stream)> archivos)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        foreach (var f in archivos)
        {
            var sc = new StreamContent(f.stream);
            sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
            content.Add(sc, "archivos", f.name);
        }
        var resp = await _http.PostAsync("/api/contadora/importar-compras", content);
        if (!resp.IsSuccessStatusCode) return (null, $"HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        return (await resp.Content.ReadFromJsonAsync<ContadoraImportResultDto>(), null);
    }

    public async Task<ContadoraImportResultDto?> ImportarVentasAfipCarpetaAsync(string? subcarpeta = null)
        => await PostAsync<ContadoraImportResultDto>("/api/contadora/importar-ventas-afip-carpeta" + (string.IsNullOrWhiteSpace(subcarpeta) ? "" : "?subcarpeta=" + Uri.EscapeDataString(subcarpeta)), new { });

    public async Task<ContadoraImportResultDto?> SincronizarMeliApiAsync()
        => await PostAsync<ContadoraImportResultDto>("/api/contadora/sincronizar-meli-api", new { });

    public async Task<ContadoraImportResultDto?> ImportarScrapeAfipAsync()
        => await PostAsync<ContadoraImportResultDto>("/api/contadora/importar-scrape-afip", new { });

    public async Task<ContadoraPdfResultDto?> ProcesarFacturasPdfAsync(string? subcarpeta = null)
        => await PostAsync<ContadoraPdfResultDto>("/api/contadora/procesar-facturas-pdf" + (string.IsNullOrWhiteSpace(subcarpeta) ? "" : "?subcarpeta=" + Uri.EscapeDataString(subcarpeta)), new { });

    public async Task<(ContadoraImportResultDto? result, string? error)> ImportarVentasAfipArchivosAsync(IEnumerable<(string name, Stream stream)> archivos)
    {
        await SetAuthHeaderAsync();
        using var content = new MultipartFormDataContent();
        foreach (var f in archivos)
        {
            var sc = new StreamContent(f.stream);
            sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            content.Add(sc, "archivos", f.name);
        }
        var resp = await _http.PostAsync("/api/contadora/importar-ventas-afip", content);
        if (!resp.IsSuccessStatusCode) return (null, $"HTTP {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
        return (await resp.Content.ReadFromJsonAsync<ContadoraImportResultDto>(), null);
    }

    private static string ComprasQs(DateTime? desde, DateTime? hasta, string? empresa, string? search)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(empresa)) qs.Add($"empresa={Uri.EscapeDataString(empresa)}");
        if (!string.IsNullOrWhiteSpace(search)) qs.Add($"search={Uri.EscapeDataString(search)}");
        return qs.Count > 0 ? "?" + string.Join("&", qs) : "";
    }

    public async Task<ContadoraReporteResumenDto?> GetContadoraComprasResumenAsync(DateTime? desde, DateTime? hasta, string? empresa, string? search)
        => await GetAsync<ContadoraReporteResumenDto>("/api/contadora/compras/resumen" + ComprasQs(desde, hasta, empresa, search));

    public async Task<ContadoraComprobantesPageDto?> GetContadoraComprasComprobantesAsync(DateTime? desde, DateTime? hasta, string? empresa, string? search, int page = 1, int pageSize = 50)
    {
        var qs = ComprasQs(desde, hasta, empresa, search);
        qs += (qs.Length > 0 ? "&" : "?") + $"page={page}&pageSize={pageSize}";
        return await GetAsync<ContadoraComprobantesPageDto>("/api/contadora/compras/comprobantes" + qs);
    }

    public async Task<ContadoraBalanzaDto?> GetContadoraBalanzaAsync(DateTime? desde, DateTime? hasta, string? empresa)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(empresa)) qs.Add($"empresa={Uri.EscapeDataString(empresa)}");
        return await GetAsync<ContadoraBalanzaDto>("/api/contadora/balanza" + (qs.Count > 0 ? "?" + string.Join("&", qs) : ""));
    }

    public async Task<ContadoraControlDto?> GetContadoraControlAsync(DateTime? desde, DateTime? hasta, string? empresa = null)
    {
        var qs = new List<string>();
        if (desde.HasValue) qs.Add($"desde={desde.Value:yyyy-MM-dd}");
        if (hasta.HasValue) qs.Add($"hasta={hasta.Value:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(empresa)) qs.Add($"empresa={Uri.EscapeDataString(empresa)}");
        return await GetAsync<ContadoraControlDto>("/api/contadora/control" + (qs.Count > 0 ? "?" + string.Join("&", qs) : ""));
    }
}
