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

    /// <summary>¿la zona de un producto es un estante del plano? 2026-11-27: "D2-3" (rack-nivel),
    /// "D2-3B" (nivel 3, posición B) o el formato viejo "D2-1-3".</summary>
    public static bool EsCodigoEstante(string? zona)
        => !string.IsNullOrWhiteSpace(zona) && System.Text.RegularExpressions.Regex.IsMatch(zona.Trim(), @"^[A-Z0-9]+-\d+([A-Z]|-\d+)?$");

    /// <summary>2026-11-27: código del estante, igual que en el servidor (DepositoPlanoController.CodigoEstante).
    /// Rack con una posición: "D2-3"; con varias: "D2-3A", "D2-3B" (la posición es la letra).</summary>
    public static string CodigoEstante(string? rack, int? posiciones, int pos, int nivel)
        => (posiciones ?? 1) <= 1 ? $"{rack}-{nivel}" : $"{rack}-{nivel}{LetraPosicion(pos)}";

    public static string LetraPosicion(int pos) => pos >= 1 && pos <= 26 ? ((char)('A' + pos - 1)).ToString() : pos.ToString();

    /// <summary>2026-11-27: parte el código del estante: "D2-3" → (D2, 1, 3); "D2-3B" → (D2, 2, 3, conLetra);
    /// el viejo "D2-1-3" → (D2, 1, 3). Si no es un estante, Niv = 0.</summary>
    public static (string Rack, int Pos, int Niv, bool ConLetra) PartirEstante(string? cod)
    {
        var c = (cod ?? "").Trim().ToUpperInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(c, @"^([A-Z0-9]+)-(\d{1,3})([A-Z])?$");
        if (m.Success)
            return (m.Groups[1].Value, m.Groups[3].Success ? m.Groups[3].Value[0] - 'A' + 1 : 1, int.Parse(m.Groups[2].Value), m.Groups[3].Success);
        m = System.Text.RegularExpressions.Regex.Match(c, @"^([A-Z0-9]+)-(\d{1,3})-(\d{1,3})$");
        if (m.Success) return (m.Groups[1].Value, int.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), false);
        return (c, 0, 0, false);
    }

    /// <summary>2026-11-27: "Rack D2 · nivel 3 (el piso)" y, si el rack tiene varias posiciones, "· lugar B".</summary>
    public static string TextoEstante(string? rack, int? posiciones, int pos, int nivel)
        => $"Rack {rack} · nivel {nivel}{(nivel == 1 ? " (el piso)" : "")}{((posiciones ?? 1) > 1 ? $" · lugar {LetraPosicion(pos)}" : "")}";

    /// <summary>Dirección del ZPL con una etiqueta QR por estante (se la pasa a zebraImprimir).</summary>
    public static string UrlEtiquetasRackPlano(int rackId, string origen)
        => $"/api/deposito/plano/racks/{rackId}/etiquetas.zpl?origen={Uri.EscapeDataString(origen)}";
}
