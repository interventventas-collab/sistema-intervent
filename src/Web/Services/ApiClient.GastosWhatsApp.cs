using System.Net.Http.Json;
namespace Web.Services;

/// <summary>18/11/2026 — "#gasto" por WhatsApp. Archivo aparte para no pisarse con otras sesiones.</summary>
public partial class ApiClient
{
    public record GastoWaComentarioDto(int Id, string Texto, string? Usuario, DateTime CreatedAt);
    public record GastoWaDto(int Id, string Numero, string? Quien, string Texto, string? MediaUrl, string? MediaFilename,
        decimal? Importe, string? Medio, string Estado, int? PagoId, string? PagoNumero, string? MotivoRechazo,
        string? ResueltoPor, DateTime? ResueltoAt, DateTime CreatedAt, List<GastoWaComentarioDto> Comentarios);
    public record GastoWaPersonaDto(string Nombre, string Numero);
    public record GastoWaNumerosDto(List<string> Numeros, bool UsaAlertas, List<GastoWaPersonaDto> DeAlertas);

    private const string GwBase = "/api/compras/gastos-whatsapp";

    public async Task<List<GastoWaDto>> GetGastosWaAsync(string? estado = null)
        => await GetAsync<List<GastoWaDto>>(GwBase + (string.IsNullOrEmpty(estado) ? "" : "?estado=" + estado)) ?? new();
    public async Task<GastoWaDto?> GetGastoWaAsync(int id) => await GetAsync<GastoWaDto>($"{GwBase}/{id}");

    private async Task<bool> GwEnviar(HttpMethod m, string url, object? body)
    {
        try
        {
            using var req = new HttpRequestMessage(m, url);
            if (body is not null) req.Content = JsonContent.Create(body);
            var r = await _http.SendAsync(req);
            return r.IsSuccessStatusCode;
        }
        catch { return false; }
    }
    public Task<bool> ComentarGastoWaAsync(int id, string texto) => GwEnviar(HttpMethod.Post, $"{GwBase}/{id}/comentarios", new { Texto = texto });
    public Task<bool> AplicarGastoWaAsync(int id, int pagoId) => GwEnviar(HttpMethod.Post, $"{GwBase}/{id}/aplicar", new { PagoId = pagoId });
    public Task<bool> RechazarGastoWaAsync(int id, string? motivo) => GwEnviar(HttpMethod.Post, $"{GwBase}/{id}/rechazar", new { Motivo = motivo });
    public Task<bool> IgnorarGastoWaAsync(int id) => GwEnviar(HttpMethod.Post, $"{GwBase}/{id}/ignorar", new { });
    public Task<bool> ReabrirGastoWaAsync(int id) => GwEnviar(HttpMethod.Post, $"{GwBase}/{id}/reabrir", new { });
    public async Task<GastoWaNumerosDto?> GetGastoWaNumerosAsync() => await GetAsync<GastoWaNumerosDto>($"{GwBase}/numeros");
    public Task<bool> GuardarGastoWaNumerosAsync(List<string> numeros) => GwEnviar(HttpMethod.Put, $"{GwBase}/numeros", new { Numeros = numeros });
}
