namespace Api.DTOs;

/// <summary>2026-11-03: una publicación de MeLi donde aparece un producto (suelta, pack del
/// mismo producto o combo con otros). Cantidad = cuántas unidades del producto descuenta.</summary>
public record MeliPubliDeProductoDto(
    string MeliItemId, string Title, string? Thumbnail, string? Permalink, decimal Price,
    string Status, string? LogisticType, int AvailableQuantity, int SoldQuantity, string? Sku,
    string? ListingTypeId, decimal Cantidad, string Tipo, string? Variante);

public record MeliPublisDeProductoDto(
    int ProductoId, string? Sku, string Nombre, int Activas, int Pausadas,
    List<MeliPubliDeProductoDto> Publis);

/// <summary>Resultado del buscador por código: el producto encontrado (o null) y, si el código
/// no coincide exacto, otros productos parecidos para elegir.</summary>
public record MeliPublisBusquedaDto(MeliPublisDeProductoDto? Producto, List<MeliProductoCandidatoDto> Candidatos);

public record MeliProductoCandidatoDto(int Id, string? Sku, string Nombre);

public record MeliPublisConteoDto(int ProductoId, int Activas, int Pausadas);
