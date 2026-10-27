namespace Web.Models;

/// <summary>27/10/2026: "Ver historial" de una venta (carga, armado, reparto, entrega, cobros, cambios).
/// Cuando viene en UTC.</summary>
public record VentaHistorialItemDto(DateTime Cuando, string Tipo, string? Quien, string? Desde, string Texto,
    List<string> Detalle, bool Tachado = false, bool Alerta = false);

public record VentaHistorialDto(int VentaId, string Numero, string? Cliente, decimal Total, List<VentaHistorialItemDto> Items);

/// <summary>27/10/2026: aviso antes de asignar una venta vieja (3 días o más) o que ya figura entregada.</summary>
public record AvisoAsignarDto(bool Avisar, string Numero, DateTime Fecha, int Dias, DateTime? EntregadoAt,
    string? EntregadoPor, int? OtraVentaId, string? OtraVentaNumero, DateTime? OtraVentaFecha);
