namespace Api.DTOs;

// ===== Equipos =====
public record AlqEquipoDto(
    int Id, string Sku, string Nombre, string? Categoria, string? Descripcion,
    int StockTotal, decimal PrecioDiario, decimal? PrecioReposicion,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt);

public class CreateAlqEquipoRequest
{
    public string? Sku { get; set; }
    public string Nombre { get; set; } = string.Empty;
    public string? Categoria { get; set; }
    public string? Descripcion { get; set; }
    public int StockTotal { get; set; }
    public decimal PrecioDiario { get; set; }
    public decimal? PrecioReposicion { get; set; }
}

public class UpdateAlqEquipoRequest
{
    public string? Sku { get; set; }
    public string? Nombre { get; set; }
    public string? Categoria { get; set; }
    public string? Descripcion { get; set; }
    public int? StockTotal { get; set; }
    public decimal? PrecioDiario { get; set; }
    public decimal? PrecioReposicion { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Clientes =====
public record AlqClienteDto(
    int Id, string Nombre, string? Empresa, string? DniCuit,
    string? Telefono, string? Telefono2, string? Email,
    string? DireccionDefault, string? Piso, string? Depto, string? Barrio, string? EntreCalles,
    string? Notas,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt);

public class CreateAlqClienteRequest
{
    public string Nombre { get; set; } = string.Empty;
    public string? Empresa { get; set; }
    public string? DniCuit { get; set; }
    public string? Telefono { get; set; }
    public string? Telefono2 { get; set; }
    public string? Email { get; set; }
    public string? DireccionDefault { get; set; }
    public string? Piso { get; set; }
    public string? Depto { get; set; }
    public string? Barrio { get; set; }
    public string? EntreCalles { get; set; }
    public string? Notas { get; set; }
}

public class UpdateAlqClienteRequest
{
    public string? Nombre { get; set; }
    public string? Empresa { get; set; }
    public string? DniCuit { get; set; }
    public string? Telefono { get; set; }
    public string? Telefono2 { get; set; }
    public string? Email { get; set; }
    public string? DireccionDefault { get; set; }
    public string? Piso { get; set; }
    public string? Depto { get; set; }
    public string? Barrio { get; set; }
    public string? EntreCalles { get; set; }
    public string? Notas { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Reservas =====
public record AlqReservaItemDto(int Id, int EquipoId, string EquipoSku, string EquipoNombre, int Cantidad, decimal PrecioUnitario);

public record AlqReservaDto(
    int Id, string Numero,
    int ClienteId, string ClienteNombre, string? ClienteTelefono,
    DateTime FechaEntrega, DateTime FechaRetiro,
    string? HoraInicio, string? HoraFin,
    string? DireccionEvento,
    decimal MontoTotal, decimal Descuento, decimal Sena,
    string Estado, string? Notas,
    DateTime CreatedAt, DateTime? UpdatedAt,
    List<AlqReservaItemDto> Items,
    DateTime? FechaEvento = null,
    // Link de Google Maps del lugar del evento (propio de la reserva). 2026-07-02
    string? MapeoLink = null,
    // ===== QR + Repartidor (2026-06-26) =====
    string? PublicToken = null,
    decimal MontoCobrado = 0,
    int? EntregadoPorRepartidorId = null, string? EntregadoPorRepartidorNombre = null,
    DateTime? EntregadoAt = null, string? ComentarioEntrega = null,
    int? RetiradoPorRepartidorId = null, string? RetiradoPorRepartidorNombre = null,
    DateTime? RetiradoAt = null, string? ComentarioRetiro = null,
    // Repartidor asignado actualmente (ultimo 'cargado' en Alq_QrEscaneos), aunque no haya entregado todavia
    int? AsignadoARepartidorId = null, string? AsignadoARepartidorNombre = null,
    // ===== ARCA — facturación de la reserva (2026-07-04) =====
    string TipoComprobante = "X",
    string CondicionIva = "CF",
    int Concepto = 2,
    string ArcaEstado = "no_aplica",
    string? ArcaCae = null,
    DateTime? ArcaCaeVto = null,
    int? ArcaPtoVta = null,
    int? ArcaWebserviceAccountId = null,
    int? ArcaCbteNro = null,
    int? ArcaCbteTipoNum = null,
    string? ArcaError = null,
    decimal? ArcaImpTotal = null);

public class CreateAlqReservaItemRequest
{
    public int EquipoId { get; set; }
    public int Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
}

public class CreateAlqReservaRequest
{
    public int ClienteId { get; set; }
    public DateTime FechaEntrega { get; set; }
    public DateTime FechaRetiro { get; set; }
    public DateTime? FechaEvento { get; set; }
    public string? HoraInicio { get; set; }
    public string? HoraFin { get; set; }
    public string? DireccionEvento { get; set; }
    public decimal Descuento { get; set; }
    public decimal Sena { get; set; }
    /// <summary>Si viene con valor, se usa como total final (modo "importe a mano"). Si es null, se calcula sumando los items.</summary>
    public decimal? MontoTotalManual { get; set; }
    public string? Estado { get; set; }
    public string? Notas { get; set; }
    /// <summary>Link de Google Maps del lugar del evento. Se guarda en la reserva.</summary>
    public string? MapeoLink { get; set; }
    /// <summary>Si es true, ademas guarda el link en la ficha del cliente (para futuras entregas).</summary>
    public bool GuardarMapeoEnCliente { get; set; }
    public List<CreateAlqReservaItemRequest> Items { get; set; } = new();
    // ===== ARCA — facturación (2026-07-04). Todos opcionales; si TipoComprobante="X" no se factura. =====
    public string? TipoComprobante { get; set; }
    public string? CondicionIva { get; set; }
    public int? Concepto { get; set; }
    /// <summary>Certificado/CUIT con el que se factura (multi-empresa). Null = default del negocio.</summary>
    public int? ArcaWebserviceAccountId { get; set; }
}

public class UpdateAlqReservaRequest
{
    public int? ClienteId { get; set; }
    public DateTime? FechaEntrega { get; set; }
    public DateTime? FechaRetiro { get; set; }
    public DateTime? FechaEvento { get; set; }
    public bool FechaEventoSet { get; set; }   // true si el form mandó explícitamente FechaEvento (permite limpiarla)
    public string? HoraInicio { get; set; }
    public string? HoraFin { get; set; }
    public string? DireccionEvento { get; set; }
    public decimal? Descuento { get; set; }
    public decimal? Sena { get; set; }
    /// <summary>Si viene con valor, se usa como total final (modo "importe a mano"). Si es null, se calcula sumando los items.</summary>
    public decimal? MontoTotalManual { get; set; }
    public string? Estado { get; set; }
    public string? Notas { get; set; }
    /// <summary>Link de Google Maps del lugar del evento. Se guarda en la reserva.</summary>
    public string? MapeoLink { get; set; }
    /// <summary>Si es true, ademas guarda el link en la ficha del cliente (para futuras entregas).</summary>
    public bool GuardarMapeoEnCliente { get; set; }
    public List<CreateAlqReservaItemRequest>? Items { get; set; }
    // ===== ARCA — facturación (2026-07-04) =====
    public string? TipoComprobante { get; set; }
    public string? CondicionIva { get; set; }
    public int? Concepto { get; set; }
    public int? ArcaWebserviceAccountId { get; set; }
}

// ===== Disponibilidad =====
public record AlqDisponibilidadDto(int EquipoId, string EquipoSku, string EquipoNombre, int StockTotal, int StockComprometido, int Disponible);
