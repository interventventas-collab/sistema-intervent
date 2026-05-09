namespace Api.DTOs;

// ===== Empleados =====
public record NomEmpleadoDto(
    int Id, string Nombre, string? Documento, string? Puesto,
    DateTime FechaIngreso,
    decimal SueldoBase, decimal ValorHora, decimal? ComisionPorcentaje,
    decimal ComisionPorKg,
    bool IsActive, DateTime CreatedAt, DateTime? UpdatedAt);

public class CreateNomEmpleadoRequest
{
    public string Nombre { get; set; } = string.Empty;
    public string? Documento { get; set; }
    public string? Puesto { get; set; }
    public DateTime? FechaIngreso { get; set; }
    public decimal SueldoBase { get; set; }
    public decimal ValorHora { get; set; }
    public decimal? ComisionPorcentaje { get; set; }
    public decimal ComisionPorKg { get; set; }
}

public class UpdateNomEmpleadoRequest
{
    public string? Nombre { get; set; }
    public string? Documento { get; set; }
    public string? Puesto { get; set; }
    public DateTime? FechaIngreso { get; set; }
    public decimal? SueldoBase { get; set; }
    public decimal? ValorHora { get; set; }
    public decimal? ComisionPorcentaje { get; set; }
    public decimal? ComisionPorKg { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Liquidaciones =====
public record NomPagoDto(
    int Id, int LiquidacionId, DateTime FechaPago, string Metodo, decimal Monto,
    string Concepto, string? Detalle,
    string? Notas, DateTime CreatedAt);

public record NomLiquidacionDto(
    int Id, int EmpleadoId, string EmpleadoNombre, string? EmpleadoPuesto,
    int Anio, int Mes,
    decimal HorasTrabajadas, decimal HorasExtra, decimal RecargoHsExtraPct,
    decimal DiasAusencia, decimal DiasVacaciones,
    decimal KgCafe,
    decimal SueldoBase, decimal MontoHsExtra, decimal Comision, decimal Bonos,
    decimal Aguinaldo,
    decimal DescuentoFaltas, decimal Adelantos, decimal OtrosDescuentos,
    decimal TotalGanado, decimal TotalDescuentos, decimal NetoAPagar,
    string Estado, string? Notas,
    decimal TotalPagado, decimal Saldo,
    DateTime CreatedAt, DateTime? UpdatedAt,
    List<NomPagoDto> Pagos);

public class CreateNomLiquidacionRequest
{
    public int EmpleadoId { get; set; }
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal HorasTrabajadas { get; set; }
    public decimal HorasExtra { get; set; }
    public decimal? RecargoHsExtraPct { get; set; }
    public decimal DiasAusencia { get; set; }
    public decimal DiasVacaciones { get; set; }
    public decimal KgCafe { get; set; }
    public decimal Bonos { get; set; }
    public decimal Aguinaldo { get; set; }
    public decimal Adelantos { get; set; }
    public decimal OtrosDescuentos { get; set; }
    public string? Notas { get; set; }
}

public class UpdateNomLiquidacionRequest
{
    public decimal? HorasTrabajadas { get; set; }
    public decimal? HorasExtra { get; set; }
    public decimal? RecargoHsExtraPct { get; set; }
    public decimal? DiasAusencia { get; set; }
    public decimal? DiasVacaciones { get; set; }
    public decimal? KgCafe { get; set; }
    public decimal? Bonos { get; set; }
    public decimal? Aguinaldo { get; set; }
    public decimal? Adelantos { get; set; }
    public decimal? OtrosDescuentos { get; set; }
    public string? Estado { get; set; }
    public string? Notas { get; set; }
}

// ===== Pagos =====
public class CreateNomPagoRequest
{
    public int LiquidacionId { get; set; }
    public DateTime? FechaPago { get; set; }
    public string Metodo { get; set; } = "efectivo";
    public decimal Monto { get; set; }
    public string Concepto { get; set; } = "sueldo";
    public string? Detalle { get; set; }
    public string? Notas { get; set; }
}

// ===== Reportes =====
public record NomResumenMensualDto(
    int Anio, int Mes,
    int CantidadEmpleados,
    decimal TotalGanado, decimal TotalDescuentos, decimal TotalNeto,
    decimal TotalPagado, decimal SaldoPendiente);
