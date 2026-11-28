using Web.Models;

namespace Web.Services;

// 2026-11-27: "🧭 Recorrer estantes" del celu del depósito — mismo pase de la clave del día que /depo.
public partial class ApiClient
{
    private const string URL_RECORRIDO = "/api/public/depo-recorrido";

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
