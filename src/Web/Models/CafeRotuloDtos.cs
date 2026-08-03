namespace Web.Models;

// ========== Transportes (catalogo para rotulos) ==========
public record CafeTransporteDto(int Id, string Nombre, string? Direccion, string? Telefono,
    string? Localidad, string? Provincia, string? CodigoPostal, bool PagoDestino, bool Activo);

public record UpsertTransporteRequest(string? Nombre, string? Direccion, string? Telefono,
    string? Localidad, string? Provincia, string? CodigoPostal, bool? PagoDestino, bool? Activo);

// ========== Remitentes (catalogo para rotulos) ==========
public record CafeRemitenteDto(int Id, string Nombre, string? Cuit, string? NombreFantasia,
    string? Direccion, string? Telefono, string? Localidad, string? Provincia, string? CodigoPostal, bool Activo);

public record UpsertRemitenteRequest(string? Nombre, string? Cuit, string? NombreFantasia,
    string? Direccion, string? Telefono, string? Localidad, string? Provincia, string? CodigoPostal, bool? Activo);
