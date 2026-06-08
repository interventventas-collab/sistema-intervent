namespace Api.DTOs;

// ===== Empleados =====
// 2026-06-08: agregado ModalidadSueldo ("mensual" | "diario") + JornalDiario.
public record NomEmpleadoDto(
    int Id, string Nombre, string? Documento, string? Puesto,
    DateTime FechaIngreso,
    decimal SueldoBase, decimal ValorHora, decimal? ComisionPorcentaje,
    decimal ComisionPorKg, decimal BonoFijo,
    string ModalidadSueldo, decimal JornalDiario,
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
    public decimal BonoFijo { get; set; }
    // 2026-06-08: modalidad de pago ("mensual" o "diario") + jornal diario en $.
    public string? ModalidadSueldo { get; set; }
    public decimal JornalDiario { get; set; }
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
    public decimal? BonoFijo { get; set; }
    public string? ModalidadSueldo { get; set; }
    public decimal? JornalDiario { get; set; }
    public bool? IsActive { get; set; }
}

// ===== Liquidaciones =====
public record NomPagoDto(
    int Id, int LiquidacionId, DateTime FechaPago, string Metodo, decimal Monto,
    string Concepto, string? Detalle,
    string? Notas, DateTime CreatedAt);

// 2026-06-08: agregado DiasTrabajados + datos del empleado (ModalidadSueldo, JornalDiario)
// para que el frontend sepa si tiene que mostrar "Sueldo base" o "Días trabajados".
public record NomLiquidacionDto(
    int Id, int EmpleadoId, string EmpleadoNombre, string? EmpleadoPuesto,
    int Anio, int Mes,
    decimal HorasTrabajadas, decimal HorasExtra, decimal RecargoHsExtraPct,
    decimal DiasAusencia, decimal DiasVacaciones,
    decimal KgCafe, decimal DiasTrabajados,
    decimal SueldoBase, decimal MontoHsExtra, decimal Comision, decimal Bonos,
    decimal Aguinaldo,
    decimal DescuentoFaltas, decimal Adelantos, decimal OtrosDescuentos,
    decimal TotalGanado, decimal TotalDescuentos, decimal NetoAPagar,
    string Estado, string? Notas,
    decimal TotalPagado, decimal Saldo,
    string EmpleadoModalidadSueldo, decimal EmpleadoJornalDiario,
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
    public decimal DiasTrabajados { get; set; }  // 2026-06-08: solo se usa si empleado es diario
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
    public decimal? DiasTrabajados { get; set; }  // 2026-06-08
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

/// <summary>Editar un pago existente. Requiere operador + clave (misma password global
/// que las acciones criticas — sales.delete_password).</summary>
public class UpdateNomPagoRequest
{
    public DateTime? FechaPago { get; set; }
    public string? Metodo { get; set; }
    public decimal? Monto { get; set; }
    public string? Concepto { get; set; }
    public string? Detalle { get; set; }
    public string? Notas { get; set; }
    /// <summary>Nombre del operador autorizado (sales.delete_allowed_operator, default OSMAR).</summary>
    public string? Operator { get; set; }
    /// <summary>Clave de seguridad (sales.delete_password). Sin esto no se ejecuta el update.</summary>
    public string? Password { get; set; }
}

// ===== Reportes =====
public record NomResumenMensualDto(
    int Anio, int Mes,
    int CantidadEmpleados,
    decimal TotalGanado, decimal TotalDescuentos, decimal TotalNeto,
    decimal TotalPagado, decimal SaldoPendiente);
