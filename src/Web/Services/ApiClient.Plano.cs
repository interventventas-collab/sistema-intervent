using System.Net.Http.Json;
using Web.Models;

namespace Web.Services;

// 2026-11-26: "Plano del depósito" (/deposito/plano y /deposito/estante/{codigo}).
// Los errores de la API vienen como { error: "..." } → HttpRequestException con ese texto
// (la pantalla lo muestra en un cartelito).
public partial class ApiClient
{
    public async Task<PlanoDepositoDto?> GetPlanoDepositoAsync(string planta)
        => await GetAsync<PlanoDepositoDto>($"/api/deposito/plano?planta={Uri.EscapeDataString(planta)}");

    public async Task<PlanoElementoDto?> CrearElementoPlanoAsync(PlanoElementoGuardarDto dto)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync("/api/deposito/plano/elementos", dto);
        await ThrowIfErrorAsync(resp);
        return await resp.Content.ReadFromJsonAsync<PlanoElementoDto>();
    }

    public async Task<PlanoElementoDto?> GuardarElementoPlanoAsync(int id, PlanoElementoGuardarDto dto)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PutAsJsonAsync($"/api/deposito/plano/elementos/{id}", dto);
        await ThrowIfErrorAsync(resp);
        return await resp.Content.ReadFromJsonAsync<PlanoElementoDto>();
    }

    public async Task BorrarElementoPlanoAsync(int id)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.DeleteAsync($"/api/deposito/plano/elementos/{id}");
        await ThrowIfErrorAsync(resp);
    }

    public async Task<PlanoRackDto?> GetRackPlanoAsync(int id)
        => await GetAsync<PlanoRackDto>($"/api/deposito/plano/racks/{id}");

    /// <summary>Un estante por su código (ej. B-3-2). null si no existe.</summary>
    public async Task<PlanoEstanteDetalleDto?> GetEstantePlanoAsync(string codigo)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.GetAsync($"/api/deposito/plano/estante/{Uri.EscapeDataString(codigo)}");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        await ThrowIfErrorAsync(resp);
        return await resp.Content.ReadFromJsonAsync<PlanoEstanteDetalleDto>();
    }

    public async Task<PlanoAsignacionDto?> GuardarProductoEnEstanteAsync(string codigo, int productoId, string? cantidad, string? nota)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.PostAsJsonAsync($"/api/deposito/plano/estante/{Uri.EscapeDataString(codigo)}/productos",
            new { productoId, cantidad, nota });
        await ThrowIfErrorAsync(resp);
        return await resp.Content.ReadFromJsonAsync<PlanoAsignacionDto>();
    }

    public async Task SacarProductoDeEstanteAsync(int asignacionId)
    {
        await SetAuthHeaderAsync();
        var resp = await _http.DeleteAsync($"/api/deposito/plano/asignaciones/{asignacionId}");
        await ThrowIfErrorAsync(resp);
    }

    public async Task<List<PlanoBuscarDto>> BuscarEnPlanoAsync(string q)
        => await GetAsync<List<PlanoBuscarDto>>($"/api/deposito/plano/buscar?q={Uri.EscapeDataString(q)}") ?? new();

    /// <summary>2026-11-26: productos con lugar viejo que no están en ningún estante (null = todas las plantas).</summary>
    public async Task<List<PlanoSinEstanteDto>> GetProductosSinEstanteAsync(string? planta)
        => await GetAsync<List<PlanoSinEstanteDto>>("/api/deposito/plano/sin-estante"
            + (string.IsNullOrWhiteSpace(planta) ? "" : "?planta=" + Uri.EscapeDataString(planta))) ?? new();

    /// <summary>2026-11-26: ¿la zona de un producto es un estante del plano? (ej. "H-1-2").</summary>
    public static bool EsCodigoEstante(string? zona)
        => !string.IsNullOrWhiteSpace(zona) && System.Text.RegularExpressions.Regex.IsMatch(zona.Trim(), @"^[A-Z0-9]+-\d+-\d+$");

    /// <summary>Dirección del ZPL con una etiqueta QR por estante (se la pasa a zebraImprimir).</summary>
    public static string UrlEtiquetasRackPlano(int rackId, string origen)
        => $"/api/deposito/plano/racks/{rackId}/etiquetas.zpl?origen={Uri.EscapeDataString(origen)}";
}
