using System.Net.Http.Json;
using Web.Models;

namespace Web.Services;

// 2026-11-27: "🧭 Recorrer estantes" del celu del depósito — mismo pase de la clave del día que /depo.
public partial class ApiClient
{
    private const string URL_RECORRIDO = "/api/public/depo-recorrido";

    /// <summary>2026-11-28: foto del estante sacada con el celu. Devuelve el error o null.</summary>
    public async Task<(bool Vencido, string? Error)> FotoRecorridoAsync(string pase, string codigo, int operadorId, Microsoft.AspNetCore.Components.Forms.IBrowserFile archivo)
    {
        try
        {
            using var form = new MultipartFormDataContent();
            var sc = new StreamContent(archivo.OpenReadStream(16 * 1024 * 1024));
            sc.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(archivo.ContentType) ? "image/jpeg" : archivo.ContentType);
            form.Add(sc, "file", archivo.Name);
            form.Add(new StringContent(operadorId.ToString()), "operadorId");
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{URL_RECORRIDO}/estante/{Uri.EscapeDataString(codigo)}/foto") { Content = form };
            req.Headers.Add("X-Pase-Celu", pase);
            var resp = await _http.SendAsync(req);
            if ((int)resp.StatusCode == 403) return (true, null);
            if (resp.IsSuccessStatusCode) return (false, null);
            string? err = null;
            try { err = (await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("error").GetString(); } catch { }
            return (false, err ?? "No se pudo subir la foto.");
        }
        catch { return (false, "No se pudo subir la foto (¿muy grande o sin conexión?)."); }
    }

    public Task<RespuestaCelu<RecorridoInicioDto>> GetRecorridoInicioAsync(string pase)
        => MandarCeluAsync<RecorridoInicioDto>(HttpMethod.Get, $"{URL_RECORRIDO}/inicio", pase);

    public Task<RespuestaCelu<RecorridoEstanteDto>> GetRecorridoEstanteAsync(string pase, string codigo)
        => MandarCeluAsync<RecorridoEstanteDto>(HttpMethod.Get, $"{URL_RECORRIDO}/estante/{Uri.EscapeDataString(codigo)}", pase);

    public Task<RespuestaCelu<List<RecorridoProductoDto>>> BuscarRecorridoAsync(string pase, string q)
        => MandarCeluAsync<List<RecorridoProductoDto>>(HttpMethod.Get, $"{URL_RECORRIDO}/buscar?q={Uri.EscapeDataString(q)}", pase);

    public Task<RespuestaCelu<RecorridoContarResultDto>> ContarRecorridoAsync(string pase, string codigo, int operadorId, int productoId, int? cantidad, bool todo, bool mover)
        => MandarCeluAsync<RecorridoContarResultDto>(HttpMethod.Post, $"{URL_RECORRIDO}/estante/{Uri.EscapeDataString(codigo)}/contar", pase,
            new { operadorId, productoId, cantidad, todo, mover });

    public async Task<RespuestaCelu<bool>> SacarRecorridoAsync(string pase, string codigo, int productoId)
    {
        var r = await MandarCeluAsync<System.Text.Json.JsonElement?>(HttpMethod.Delete, $"{URL_RECORRIDO}/estante/{Uri.EscapeDataString(codigo)}/producto/{productoId}", pase);
        return new(r.Error is null && !r.Vencido, r.Vencido, r.Error);
    }

    // 2026-11-27: buscar por marca, comentario del estante, código de barras aprendido
    public Task<RespuestaCelu<List<RecorridoMarcaDto>>> MarcasRecorridoAsync(string pase, string q)
        => MandarCeluAsync<List<RecorridoMarcaDto>>(HttpMethod.Get, $"{URL_RECORRIDO}/marcas?q={Uri.EscapeDataString(q)}", pase);

    public Task<RespuestaCelu<List<RecorridoProductoDto>>> BuscarMarcaRecorridoAsync(string pase, string marca)
        => MandarCeluAsync<List<RecorridoProductoDto>>(HttpMethod.Get, $"{URL_RECORRIDO}/buscar?marca={Uri.EscapeDataString(marca)}", pase);

    public async Task<RespuestaCelu<bool>> NotaRecorridoAsync(string pase, string codigo, int operadorId, string? nota)
    {
        var r = await MandarCeluAsync<System.Text.Json.JsonElement?>(HttpMethod.Post, $"{URL_RECORRIDO}/estante/{Uri.EscapeDataString(codigo)}/nota", pase, new { operadorId, nota });
        return new(r.Error is null && !r.Vencido, r.Vencido, r.Error);
    }

    public async Task<RespuestaCelu<bool>> BarcodeRecorridoAsync(string pase, int productoId, int operadorId, string barcode)
    {
        var r = await MandarCeluAsync<System.Text.Json.JsonElement?>(HttpMethod.Post, $"{URL_RECORRIDO}/producto/{productoId}/barcode", pase, new { operadorId, barcode });
        var ok = r.Dato is System.Text.Json.JsonElement j && j.TryGetProperty("ok", out var o) && o.ValueKind == System.Text.Json.JsonValueKind.True;
        return new(ok, r.Vencido, r.Error);
    }

    public async Task<RespuestaCelu<bool>> RevisadoRecorridoAsync(string pase, string codigo, int operadorId, int ajustes)
    {
        var r = await MandarCeluAsync<System.Text.Json.JsonElement?>(HttpMethod.Post, $"{URL_RECORRIDO}/estante/{Uri.EscapeDataString(codigo)}/revisado", pase, new { operadorId, ajustes });
        return new(r.Error is null && !r.Vencido, r.Vencido, r.Error);
    }
}
