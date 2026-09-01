namespace Web.Models;

// 2026-08-31 — Lo que devuelve /api/panorama. Espeja los records de Api/Services/PanoramaService.cs.
// Si allá cambia un nombre de campo, acá tiene que cambiar igual (el JSON se ata por nombre).

public record PanoramaPataDto(
    string Clave, string Nombre, string Canal,
    decimal Facturado, decimal? Margen, decimal? MargenPct,
    int Operaciones, string UnidadOperacion,
    decimal? VarFacturado, string? SinMargenPorque,
    // Qué parte de lo facturado (0 a 100) se pudo juzgar con un costo confiable.
    // En MercadoLibre solo guardamos el costo de HOY: si el precio se movió mucho desde la
    // venta, esa venta no entra en el margen. Null = la pata no tiene margen.
    decimal? MargenCobertura = null);

public record PanoramaPuntoDto(
    int Anio, int Mes, string Etiqueta,
    decimal Iv, decimal Ie, decimal Fk, decimal Lg,
    decimal MargenIv, decimal MargenFk,
    int OpsIv, int OpsIe, int OpsFk, int OpsLg,
    decimal KgCafe);

public record PanoramaFilaDto(string Nombre, string Pata, decimal Valor, decimal? Margen,
    decimal? Var, string? Detalle);

public record PanoramaRankingDto(string Clave, string Titulo, string ColumnaNombre, string ColumnaValor,
    string? Nota, List<PanoramaFilaDto> Filas);

public record PanoramaAvisoDto(string Texto, string Detalle, bool EsAlarma, string? Link);

public record PanoramaDto(
    string Periodo, string Etiqueta, string Comparacion,
    DateTime Desde, DateTime Hasta,
    List<PanoramaPataDto> Patas,
    decimal TotalFacturado, decimal TotalMargen, decimal TotalMargenPct, string TotalMargenSobre,
    int TotalOperaciones, decimal? VarTotal,
    decimal KgCafe, decimal? KgCafeVar,
    List<PanoramaPuntoDto> Serie,
    List<PanoramaRankingDto> Rankings,
    List<PanoramaAvisoDto> Avisos,
    // Primer día del primer mes con datos cargados. Antes de eso la pantalla no deja ir.
    DateTime? PrimerMesConDatos,
    DateTime GeneradoAt);
