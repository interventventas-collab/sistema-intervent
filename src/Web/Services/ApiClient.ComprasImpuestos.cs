using System.Net.Http.Json;
namespace Web.Services;

/// <summary>18/11/2026 — Facturas recibidas (ARCA a las 7 + Traer ahora). Archivo aparte para no
/// pisarse con otras sesiones que tocan ApiClient.cs.</summary>
public partial class ApiClient
{
    public record FacturaRecibidaDto(string Id, DateTime Llego, DateTime? Fecha, string Proveedor, string? Cuit, int? ProveedorId,
        string Tipo, string Numero, decimal Total, bool EsNotaCredito, bool TienePdf, bool Nueva, string? Pago,
        string? Categoria = null, string? Comentario = null, string? PagoVia = null, FacturaSugerenciaDto? Sugerencia = null,
        bool CuentaCorriente = false, string? ReglaPago = null, bool PagoManual = false, string? PagoAuto = null);
    /// <summary>18/11/2026 (2ª): pago que puede ser de la factura (MP o BCO = banco Galicia).</summary>
    public record FacturaSugerenciaDto(string Origen, long MovId, DateTime Fecha, decimal Importe, string Texto);
    public record FacturasPorProveedorDto(string Nombre, int Cantidad, decimal Total);
    public record FacturasEstadoDto(DateTime? Ultima, string? Resultado, bool Corriendo, DateTime? VistasHasta);
    public record FacturasResumenDto(int Nuevas, decimal TotalNuevas, List<FacturasPorProveedorDto> PorProveedor, FacturasEstadoDto Estado);

    public async Task<FacturasResumenDto?> GetFacturasRecibidasResumenAsync()
        => await GetAsync<FacturasResumenDto>("/api/compras/facturas-recibidas/resumen");
    public async Task<FacturasEstadoDto?> GetFacturasRecibidasEstadoAsync()
        => await GetAsync<FacturasEstadoDto>("/api/compras/facturas-recibidas/estado");
    public async Task<List<FacturaRecibidaDto>> GetFacturasRecibidasAsync(string? mes, string? q)
    {
        var url = "/api/compras/facturas-recibidas?x=1";
        if (!string.IsNullOrWhiteSpace(mes)) url += "&mes=" + Uri.EscapeDataString(mes);
        if (!string.IsNullOrWhiteSpace(q)) url += "&q=" + Uri.EscapeDataString(q.Trim());
        return await GetAsync<List<FacturaRecibidaDto>>(url) ?? new();
    }
    public async Task<bool> MarcarFacturasRecibidasVistasAsync()
        => await PostAsync<object>("/api/compras/facturas-recibidas/marcar-vistas", new { }) is not null;
    // ── 18/11/2026 (2ª): categorías, comentarios y pagos ──
    private const string FrBase = "/api/compras/facturas-recibidas";
    public async Task<List<string>> GetFacturasCategoriasAsync() => await GetAsync<List<string>>(FrBase + "/categorias") ?? new();
    private async Task<bool> FrEnviar(HttpMethod m, string url, object? body)
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
    public Task<bool> PonerCategoriaProveedorAsync(string cuit, string? categoria)
        => FrEnviar(HttpMethod.Put, $"{FrBase}/proveedor/{Uri.EscapeDataString(cuit)}/categoria", new { Categoria = categoria });
    public Task<bool> PonerReglaPagoProveedorAsync(string cuit, string? regla)
        => FrEnviar(HttpMethod.Put, $"{FrBase}/proveedor/{Uri.EscapeDataString(cuit)}/regla", new { Regla = regla });
    public Task<bool> GuardarComentarioFacturaAsync(string id, string? texto)
        => FrEnviar(HttpMethod.Put, $"{FrBase}/{Uri.EscapeDataString(id)}/comentario", new { Texto = texto });
    public Task<bool> MarcarFacturaPagadaAsync(string id, string medio, string? origen = null, long? movId = null)
        => FrEnviar(HttpMethod.Post, $"{FrBase}/{Uri.EscapeDataString(id)}/pago", new { Medio = medio, Origen = origen, MovId = movId });
    public Task<bool> DesmarcarFacturaPagadaAsync(string id)
        => FrEnviar(HttpMethod.Delete, $"{FrBase}/{Uri.EscapeDataString(id)}/pago", null);
    public Task<bool> RechazarSugerenciaFacturaAsync(string id, string origen, long movId)
        => FrEnviar(HttpMethod.Post, $"{FrBase}/{Uri.EscapeDataString(id)}/rechazar", new { Origen = origen, MovId = movId });

    public async Task<bool> TraerFacturasRecibidasAhoraAsync()
        => await PostAsync<object>("/api/compras/facturas-recibidas/traer-ahora", new { }) is not null;

    // 18/11/2026: almanaque fijo de ARCA (terminación 9). Estado: PAGADO · BANCO · VEP · SIN_VEP.
    public record VencimientoArcaDto(string Concepto, string Nombre, string Periodo, DateTime Vence, string Estado,
        decimal? Importe, DateTime? PagadoFecha, int? VepId);
    public async Task<List<VencimientoArcaDto>> GetVencimientosArcaAsync()
        => await GetAsync<List<VencimientoArcaDto>>("/api/impuestos/vencimientos") ?? new();
}
