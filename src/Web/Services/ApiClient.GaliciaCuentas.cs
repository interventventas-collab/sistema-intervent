using System.Net;
using System.Net.Http.Json;
namespace Web.Services;

/// <summary>18/11/2026 — Varias cuentas del Galicia (una por sociedad). La principal es Palanica;
/// las otras se ven aparte en la ventana 🏦 Banco Galicia. Archivo aparte para no pisarse con
/// otras sesiones que tocan ApiClient.cs.</summary>
public partial class ApiClient
{
    public record GaliciaCuentaDto(int Id, string Nombre, bool EsPrincipal, string Usuario, bool HasPassword, bool IsActive,
        bool AutoSyncEnabled, string? AutoSyncTimes, DateTime? LastAutoSyncAt);
    public record GaliciaTraerDto(bool Ok, string Texto);
    public record GaliciaMovDto(int Id, DateTime Fecha, string Nombre, string? Detalle, decimal Importe);
    public record GaliciaChequeDto(int Id, string Numero, string? Quien, DateTime? Vence, decimal Importe, string Estado);
    public record GaliciaResumenDto(int Id, string Nombre, decimal? Saldo, DateTime? UltimaFecha, DateTime? UltimoImportadoAt,
        List<GaliciaMovDto> Entraron, List<GaliciaMovDto> Salieron, List<GaliciaChequeDto> ChequesRecibidos, List<GaliciaChequeDto> ChequesEmitidos);

    public async Task<List<GaliciaCuentaDto>> GetGaliciaCuentasAsync()
        => await GetAsync<List<GaliciaCuentaDto>>("/api/galicia/cuentas") ?? new();

    /// <summary>id = 0 crea una cuenta nueva.</summary>
    public async Task<Web.Models.GaliciaAccountDto?> SaveGaliciaCuentaAsync(int id, Web.Models.SaveGaliciaAccountRequest req)
        => await PutAsync<Web.Models.GaliciaAccountDto>($"/api/galicia/cuentas/{id}", req);

    public async Task<GaliciaResumenDto?> GetGaliciaResumenCuentaAsync(int id, int dias = 30)
        => await GetAsync<GaliciaResumenDto>($"/api/galicia/cuentas/{id}/resumen?dias={dias}");

    /// <summary>que = "movimientos" o "cheques". Puede tardar varios minutos (cliente de 10 min).</summary>
    public async Task<GaliciaTraerDto?> TraerGaliciaCuentaAsync(int id, string que)
    {
        var resp = await _httpGalicia.PostAsJsonAsync($"/api/galicia/cuentas/{id}/traer/{que}", new { });
        if (resp.StatusCode == HttpStatusCode.Unauthorized) { await HandleUnauthorizedAsync(); return null; }
        await ThrowIfErrorAsync(resp);
        return await resp.Content.ReadFromJsonAsync<GaliciaTraerDto>();
    }
}
