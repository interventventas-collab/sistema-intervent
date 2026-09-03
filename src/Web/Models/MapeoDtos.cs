namespace Web.Models;

// Resultado de sumar una venta al mapa (botón en el listado de ventas).
public class SumarAlMapaResult
{
    public bool Ok { get; set; }
    public bool YaEstaba { get; set; }
    public string? Motivo { get; set; }         // "sin_domicilio" | "no_resuelto"
    public string? Mensaje { get; set; }
    public int? ClienteId { get; set; }
    public string? ClienteNombre { get; set; }
    public string? DireccionSugerida { get; set; }
    public string? Localidad { get; set; }
    public string? Nombre { get; set; }
}

// Resultado de escanear el QR de una etiqueta Flex (pantalla /mapeo/escanear).
public class ScanFlexResult
{
    public bool Ok { get; set; }
    public bool YaEstaba { get; set; }
    public string? Motivo { get; set; }
    public string? Mensaje { get; set; }
    public long Id { get; set; }
    public string? Nombre { get; set; }
    public string? Localidad { get; set; }
    public int StopId { get; set; }
}

// Resultado de "traer por número" (venta / alquiler / envío MeLi). StopId puede venir vacío
// cuando la venta/alquiler no tiene domicilio cargado (Motivo = sin_domicilio / no_resuelto).
public class TraerPorNumeroResult
{
    public bool Ok { get; set; }
    public bool YaEstaba { get; set; }
    public string? Motivo { get; set; }
    public string? Mensaje { get; set; }
    public string? Nombre { get; set; }
    public string? Localidad { get; set; }
    public int? StopId { get; set; }
    public string? Tipo { get; set; }   // "venta" | "alquiler" | (null = envío MeLi)
}

public class MapeoDriverDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Telefono { get; set; }
    public string Color { get; set; } = "#1d4ed8";
    public bool IsActive { get; set; } = true;
    public string? ShareToken { get; set; }
    // Vínculo al repartidor REAL (Cafe_Repartidores) para que la ruta le aparezca en su teléfono.
    public int? CafeRepartidorId { get; set; }
    // Token del link fijo del repartidor (/mis-pedidos/{PublicToken}) — para "Ver como repartidores".
    public string? PublicToken { get; set; }
}

// Parada del Mapeo en solo-lectura para la pantalla del repartidor (Mis Pedidos).
public class MisPedidosMapeoDto
{
    public int Id { get; set; }
    public int? OrderInRoute { get; set; }
    public string Origin { get; set; } = "";
    public string? OriginRefId { get; set; }
    public string? Nombre { get; set; }
    public string Direccion { get; set; } = "";
    public string? Localidad { get; set; }
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? Telefono { get; set; }
    public string? Comprador { get; set; }
    public long? NumeroVenta { get; set; }
    public bool Entregado { get; set; }
    public DateTime? DateDelivered { get; set; }
    /// <summary>2026-09-02: cerrada SIN entregar (cancelada, "no encontró", o MercadoLibre avisó
    /// que no se entregó). No es una entrega, pero tampoco le queda pendiente.</summary>
    public bool NoEntregada { get; set; }
    /// <summary>2026-09-02: el repartidor la tildó a mano para que su lista avance. SOLO visual:
    /// no toca MercadoLibre, no marca entregado y no cuenta como entrega en ningún número.</summary>
    public bool Visto { get; set; }
}

public class PublicRouteDto
{
    public string DriverNombre { get; set; } = "";
    public string DriverColor { get; set; } = "#1d4ed8";
    public string? DriverTelefono { get; set; }
    public DateTime? Now { get; set; }
    public string? StartAddress { get; set; }
    public decimal? StartLat { get; set; }
    public decimal? StartLng { get; set; }
    public List<PublicStopDto> Stops { get; set; } = new();
}

public class PublicStopDto
{
    public int Id { get; set; }
    public int? OrderInRoute { get; set; }
    public string? Alias { get; set; }
    public string Direccion { get; set; } = "";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? ContactName { get; set; }
    public string? Telefono { get; set; }
    public string? Notas { get; set; }
    public string InternalStatus { get; set; } = "pending";
    public string? Comprador { get; set; }
    public string? NumeroVenta { get; set; }
}

public class MapeoStopDto
{
    public int Id { get; set; }
    public string Origin { get; set; } = "manual";
    public string? OriginRefId { get; set; }
    public string? Alias { get; set; }
    public string Direccion { get; set; } = "";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? ContactName { get; set; }
    public string? Telefono { get; set; }
    public string? Notas { get; set; }
    public string InternalStatus { get; set; } = "pending";
    public int? AssignedDriverId { get; set; }
    public string? AssignedDriverName { get; set; }
    public string? AssignedDriverColor { get; set; }
    public int? AssignedVehicleSlot { get; set; }
    public int? OrderInRoute { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Localidad / ciudad de la parada — para agrupar la lista lateral por zona.</summary>
    public string? Localidad { get; set; }
    // Datos del envío de MeLi enlazado (paradas Flex/ME1 escaneadas): usuario, nº venta, entregado.
    public long? MeliOrderId { get; set; }
    public string? BuyerNickname { get; set; }
    public string? MeliStatus { get; set; }
    public DateTime? DateDelivered { get; set; }
    public string? ReceiverName { get; set; }
}

/// <summary>Respuesta de "asignar esta parada a un cliente del sistema": la parada ya renombrada
/// con el cliente, y si además se le guardó la ubicación en la ficha del cliente.</summary>
public class AsignarClienteStopResult
{
    public MapeoStopDto? Stop { get; set; }
    public string? ClienteNombre { get; set; }
    public bool UbicacionGuardada { get; set; }
}

public class ImportFlexPreviewDto
{
    public int Total { get; set; }
    public int YaCargados { get; set; }
    public int AImportar { get; set; }
    public List<ImportFlexSampleDto> Sample { get; set; } = new();
}

public class ImportFlexSampleDto
{
    public string? ReceiverName { get; set; }
    public string? City { get; set; }
    public string? AddressLine { get; set; }
}

public class MapeoSnapshotListItemDto
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public int StopsCount { get; set; }
    public int VehiclesCount { get; set; }
    public int DriversCount { get; set; }
    public int DeliveredCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? CreatedByUsername { get; set; }
    public string? Notes { get; set; }
}

public class RutaLegDto
{
    public int Seconds { get; set; }
    public int Meters { get; set; }
    // Línea codificada de ESTE tramo (para dibujarlo clickeable y mostrar su distancia al tocarlo).
    public string? Encoded { get; set; }
    // De qué punto a qué punto va el tramo (ej: "Salida"→"1", "2"→"3").
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    // Nivel de tránsito por pedacito de la línea (para pintarla rojo/amarillo donde hay embotellamiento).
    public List<TramoTransitoDto> Transito { get; set; } = new();
}

public class TramoTransitoDto
{
    public int Start { get; set; }
    public int End { get; set; }
    public string Speed { get; set; } = ""; // NORMAL | SLOW | TRAFFIC_JAM | SPEED_UNSPECIFIED
}

public class RutaOverviewDto
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public string Color { get; set; } = "#1d4ed8";
    public int? DriverId { get; set; }
    // Zona/vehículo (slot) de esta ruta cuando no tiene repartidor (para ocultar su línea con el ojito).
    public int? VehicleSlot { get; set; }
    public int DurationSeconds { get; set; }
    public int DistanceMeters { get; set; }
    public string? EncodedPolyline { get; set; }
    public int StopCount { get; set; }
    // Una línea codificada por tramo (rutas de >25 paradas vienen partidas; se dibujan pegadas).
    public List<string> Segments { get; set; } = new();
    // Tiempo/metros entre parada y parada (en orden de visita).
    public List<RutaLegDto> Legs { get; set; } = new();
}

public class RutaAhorroDto
{
    public string Label { get; set; } = "";
    public string Color { get; set; } = "#6b7280";
    public int? DriverId { get; set; }
    public int ActualSeconds { get; set; }
    public int OptimoSeconds { get; set; }
    public int ActualMeters { get; set; }
    public int OptimoMeters { get; set; }
    public int StopCount { get; set; }
    public bool Calculable { get; set; }
}

public class MapeoFavoritoDto
{
    public int Id { get; set; }
    public string Alias { get; set; } = "";
    public string Direccion { get; set; } = "";
    public decimal Latitude { get; set; }
    public decimal Longitude { get; set; }
    public string? ContactName { get; set; }
    public string? Telefono { get; set; }
    public string? Notas { get; set; }
    public bool IsActive { get; set; } = true;
}

// "Armar ruta guiada": una parada del medio clavada en un puesto fijo (opcional).
public class FijaMedioDto
{
    public int StopId { get; set; }
    public int Puesto { get; set; }
}

// Resultado de armar la ruta guiada: cuántas paradas ordenó y si lo hizo Google o el respaldo.
public class ArmarRutaGuiadaResult
{
    public int Total { get; set; }
    public bool PorGoogle { get; set; }
}
