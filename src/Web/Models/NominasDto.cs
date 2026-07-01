namespace Web.Models;

public class NomEmpleadoDto
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Documento { get; set; }
    public string? Puesto { get; set; }
    public DateTime FechaIngreso { get; set; }
    public decimal SueldoBase { get; set; }
    public decimal ValorHora { get; set; }
    public decimal? ComisionPorcentaje { get; set; }
    public decimal ComisionPorKg { get; set; }
    public decimal BonoFijo { get; set; }
    // 2026-06-08: modalidad de pago ("mensual" o "diario") + jornal diario
    public string ModalidadSueldo { get; set; } = "mensual";
    public decimal JornalDiario { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    // 2026-06-25: cómo aparece esta misma persona en otros módulos
    // (kiosko de fichaje + módulo de repartidores). Null si no está vinculado allá.
    public string? ApodoKiosko { get; set; }
    public string? ApodoRepartidor { get; set; }
}

public class CreateNomEmpleadoRequest
{
    public string Nombre { get; set; } = "";
    public string? Documento { get; set; }
    public string? Puesto { get; set; }
    public DateTime? FechaIngreso { get; set; }
    public decimal SueldoBase { get; set; }
    public decimal ValorHora { get; set; }
    public decimal? ComisionPorcentaje { get; set; }
    public decimal ComisionPorKg { get; set; }
    public decimal BonoFijo { get; set; }
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

public class NomPagoDto
{
    public int Id { get; set; }
    public int LiquidacionId { get; set; }
    public DateTime FechaPago { get; set; }
    public string Metodo { get; set; } = "";
    public decimal Monto { get; set; }
    public string Concepto { get; set; } = "sueldo";
    public string? Detalle { get; set; }
    public string? Notas { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class NomLiquidacionDto
{
    public int Id { get; set; }
    public int EmpleadoId { get; set; }
    public string EmpleadoNombre { get; set; } = "";
    public string? EmpleadoPuesto { get; set; }
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal HorasTrabajadas { get; set; }
    public decimal HorasExtra { get; set; }
    public decimal RecargoHsExtraPct { get; set; }
    public decimal DiasAusencia { get; set; }
    public decimal DiasVacaciones { get; set; }
    public decimal KgCafe { get; set; }
    public decimal DiasTrabajados { get; set; }  // 2026-06-08
    public decimal SueldoBase { get; set; }
    public decimal MontoHsExtra { get; set; }
    public decimal Comision { get; set; }
    public decimal Bonos { get; set; }
    public decimal Aguinaldo { get; set; }
    public decimal DescuentoFaltas { get; set; }
    public decimal Adelantos { get; set; }
    public decimal OtrosDescuentos { get; set; }
    public decimal TotalGanado { get; set; }
    public decimal TotalDescuentos { get; set; }
    public decimal NetoAPagar { get; set; }
    public string Estado { get; set; } = "pendiente";
    public string? Notas { get; set; }
    public decimal TotalPagado { get; set; }
    public decimal Saldo { get; set; }
    // 2026-06-08: datos del empleado para que el frontend muestre la UI correcta
    public string EmpleadoModalidadSueldo { get; set; } = "mensual";
    public decimal EmpleadoJornalDiario { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<NomPagoDto> Pagos { get; set; } = new();
    // 2026-07-01: cantidad de recibos/nóminas adjuntas (para mostrar 📎N en la lista)
    public int ArchivosCount { get; set; }
}

// 2026-07-01: metadata de un archivo adjunto (recibo/nómina) — sin el contenido binario.
public class NomNominaArchivoDto
{
    public int Id { get; set; }
    public int LiquidacionId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/pdf";
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; }
    public string? UploadedBy { get; set; }
}

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
    public decimal DiasTrabajados { get; set; }  // 2026-06-08
    public decimal Bonos { get; set; }
    public decimal Aguinaldo { get; set; }
    public decimal Adelantos { get; set; }
    public decimal OtrosDescuentos { get; set; }
    public string? Notas { get; set; }
}

public class UpdateNomLiquidacionRequest
{
    public decimal? SueldoBase { get; set; }  // 2026-07-01: sueldo base editable/congelado por liquidación
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

public class CreateNomPagoRequest
{
    public int LiquidacionId { get; set; }
    public DateTime? FechaPago { get; set; }
    public string? FechaPagoStr { get; set; }  // 2026-07-01: fecha "yyyy-MM-dd" sin zona horaria
    public string Metodo { get; set; } = "efectivo";
    public decimal Monto { get; set; }
    public string Concepto { get; set; } = "sueldo";
    public string? Detalle { get; set; }
    public string? Notas { get; set; }
}

/// <summary>Editar un pago existente. Requiere operador + clave.</summary>
public class UpdateNomPagoRequest
{
    public DateTime? FechaPago { get; set; }
    public string? FechaPagoStr { get; set; }  // 2026-07-01: fecha "yyyy-MM-dd" sin zona horaria
    public string? Metodo { get; set; }
    public decimal? Monto { get; set; }
    public string? Concepto { get; set; }
    public string? Detalle { get; set; }
    public string? Notas { get; set; }
    public string? Operator { get; set; }
    public string? Password { get; set; }
}

public class NomResumenMensualDto
{
    public int Anio { get; set; }
    public int Mes { get; set; }
    public int CantidadEmpleados { get; set; }
    public decimal TotalGanado { get; set; }
    public decimal TotalDescuentos { get; set; }
    public decimal TotalNeto { get; set; }
    public decimal TotalPagado { get; set; }
    public decimal SaldoPendiente { get; set; }
}

// ===== Panel "¿Cuánto debo pagar?" =====

public class DashboardConceptoDto
{
    public string Concepto { get; set; } = "";
    public decimal Presupuestado { get; set; }
    public decimal Pagado { get; set; }
    public decimal Pendiente { get; set; }
}

public class DashboardLiquidacionDto
{
    public int LiquidacionId { get; set; }
    public int Anio { get; set; }
    public int Mes { get; set; }
    public decimal NetoAPagar { get; set; }
    public decimal TotalPagado { get; set; }
    public decimal Saldo { get; set; }
    public DateTime FechaVencimiento { get; set; }
    public int DiasParaVencer { get; set; }
    public List<DashboardConceptoDto> Conceptos { get; set; } = new();
}

public class DashboardEmpleadoDto
{
    public int EmpleadoId { get; set; }
    public string Nombre { get; set; } = "";
    public decimal TotalDebe { get; set; }
    public bool TieneVencido { get; set; }
    public int DiasParaVencerMasUrgente { get; set; }
    public List<DashboardLiquidacionDto> Liquidaciones { get; set; } = new();
}

public class DashboardDeudasDto
{
    public decimal TotalAPagar { get; set; }
    public decimal TotalVencido { get; set; }
    public int CantidadConDeuda { get; set; }
    public int CantidadVencidos { get; set; }
    public List<DashboardEmpleadoDto> Empleados { get; set; } = new();
}

public class DashboardPagarRequest
{
    public int LiquidacionId { get; set; }
    public string Concepto { get; set; } = "sueldo";
    public decimal Monto { get; set; }
    public string Metodo { get; set; } = "efectivo";
    public DateTime? FechaPago { get; set; }
    public string? FechaPagoStr { get; set; }  // 2026-07-01: fecha "yyyy-MM-dd" sin zona horaria
    public string? Detalle { get; set; }
    public string? Notas { get; set; }
    public string? Operator { get; set; }
    public string? Password { get; set; }
}
