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
        => !string.IsNullOrWhiteSpace(zona) && (EsCodigoCalle(zona)
            || System.Text.RegularExpressions.Regex.IsMatch(zona.Trim(), @"^[A-Z0-9]+-\d+([A-Z]|-\d+)?$"));

    private static readonly System.Text.RegularExpressions.Regex ReCalle =
        new(@"^C(\d{1,2})-R(\d{1,2})-A(\d{1,2})(?:-P(\d{1,2}))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>2026-11-27: ¿es un código de calle (C3-R4-A3, o C3-R4-A3-P2)?</summary>
    public static bool EsCodigoCalle(string? cod) => !string.IsNullOrWhiteSpace(cod) && ReCalle.IsMatch(cod.Trim());

    /// <summary>2026-11-27: el código escrito para leerlo — "C3-R4-A3" → "Calle 3 · Rack 4 · Altura 3 (el piso
    /// si es 1)"; el viejo "D2-3" → "Rack D2 · Altura 3". Sirve para el cartelito al pasar el cursor.
    /// Si no es un estante, null.</summary>
    public static string? DesgloseEstante(string? cod)
    {
        if (string.IsNullOrWhiteSpace(cod)) return null;
        var m = ReCalle.Match(cod.Trim());
        if (m.Success)
        {
            var alt = int.Parse(m.Groups[3].Value);
            return $"Calle {int.Parse(m.Groups[1].Value)} · Rack {int.Parse(m.Groups[2].Value)} · Altura {alt}{(alt == 1 ? " (el piso)" : "")}"
                + (m.Groups[4].Success ? $" · Posición {int.Parse(m.Groups[4].Value)}" : "");
        }
        var (rack, pos, niv, conLetra) = PartirEstante(cod);
        if (niv <= 0) return null;
        return $"Rack {rack} · Altura {niv}{(niv == 1 ? " (el piso)" : "")}{(conLetra ? $" · Posición {pos}" : "")}";
    }

    /// <summary>2026-11-27: código de un estante de un lado del rack (igual que en el servidor):
    /// con calle "C3-R4-A3" (+ "-P2"); sin calle, el de siempre.</summary>
    public static string CodigoEstante(PlanoElementoDto rack, int cara, int pos, int nivel)
    {
        var (calle, nro) = cara == 2 && rack.Calle2 is not null ? (rack.Calle2, rack.Nro2)
            : rack.Calle1 is not null ? (rack.Calle1, rack.Nro1) : (rack.Calle2, rack.Nro2);
        if (calle is int c && nro is int n)
            return $"C{c}-R{n}-A{nivel}" + ((rack.Posiciones ?? 1) > 1 ? $"-P{pos}" : "");
        return CodigoEstante(rack.Codigo, rack.Posiciones, pos, nivel);
    }

    /// <summary>2026-11-27: los lados del rack que dan a una calle: (1, "Calle 1 · Rack 2"), (2, …).
    /// Sin calles cargadas: un solo lado con el nombre del rack.</summary>
    public static List<(int Cara, string Titulo)> CarasDe(PlanoElementoDto r)
    {
        var l = new List<(int, string)>();
        if (r.Calle1 is int c1 && r.Nro1 is int n1) l.Add((1, $"Calle {c1} · Rack {n1}"));
        if (r.Calle2 is int c2 && r.Nro2 is int n2) l.Add((2, $"Calle {c2} · Rack {n2}"));
        if (l.Count == 0) l.Add((1, $"Rack {r.Codigo}"));
        return l;
    }

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
