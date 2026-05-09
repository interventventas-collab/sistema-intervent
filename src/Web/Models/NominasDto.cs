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
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
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
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public List<NomPagoDto> Pagos { get; set; } = new();
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
