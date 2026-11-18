namespace Web.Services;

/// <summary>18/11/2026 — Facturas recibidas (ARCA a las 7 + Traer ahora). Archivo aparte para no
/// pisarse con otras sesiones que tocan ApiClient.cs.</summary>
public partial class ApiClient
{
    public record FacturaRecibidaDto(string Id, DateTime Llego, DateTime? Fecha, string Proveedor, string? Cuit, int? ProveedorId,
        string Tipo, string Numero, decimal Total, bool EsNotaCredito, bool TienePdf, bool Nueva, string? Pago);
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
    public async Task<bool> TraerFacturasRecibidasAhoraAsync()
        => await PostAsync<object>("/api/compras/facturas-recibidas/traer-ahora", new { }) is not null;
}
