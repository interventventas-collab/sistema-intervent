namespace Web.Models;

public class EmployeeDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string? Dni { get; set; }
    public string? Cuil { get; set; }
    public string? Position { get; set; }
    public DateTime? HireDate { get; set; }
    public decimal BaseSalary { get; set; }
    public string? Bank { get; set; }
    public string? Cbu { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class CreateEmployeeRequest
{
    public string? Code { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string? Dni { get; set; }
    public string? Cuil { get; set; }
    public string? Position { get; set; }
    public DateTime? HireDate { get; set; }
    public decimal BaseSalary { get; set; }
    public string? Bank { get; set; }
    public string? Cbu { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
}

public class UpdateEmployeeRequest
{
    public string? Code { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? Dni { get; set; }
    public string? Cuil { get; set; }
    public string? Position { get; set; }
    public DateTime? HireDate { get; set; }
    public decimal? BaseSalary { get; set; }
    public string? Bank { get; set; }
    public string? Cbu { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Address { get; set; }
    public string? Notes { get; set; }
    public bool? IsActive { get; set; }
}

public class PayrollDto
{
    public int Id { get; set; }
    public int EmployeeId { get; set; }
    public string EmployeeFullName { get; set; } = string.Empty;
    public string? EmployeeCode { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal BaseSalary { get; set; }
    public decimal Bonuses { get; set; }
    public decimal Deductions { get; set; }
    public decimal GrossTotal { get; set; }
    public decimal NetTotal { get; set; }
    public string? Notes { get; set; }
    public bool IsPaid { get; set; }
    public DateTime? PaidAt { get; set; }
    public int? PaidFromAccountId { get; set; }
    public string? PaidFromAccountName { get; set; }
    public decimal TotalPaid { get; set; }
    public decimal Pending { get; set; }
    public List<PayrollPaymentDto> Payments { get; set; } = new();
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
}

public class PayrollPaymentDto
{
    public int Id { get; set; }
    public int PayrollId { get; set; }
    public DateTime Date { get; set; }
    public decimal Amount { get; set; }
    public int? AccountId { get; set; }
    public string? AccountName { get; set; }
    public string PaymentMethod { get; set; } = "efectivo";
    public string? Concept { get; set; }
    public string? Notes { get; set; }
}

public class AddPayrollPaymentRequest
{
    public DateTime? Date { get; set; }
    public decimal Amount { get; set; }
    public int? AccountId { get; set; }
    public string PaymentMethod { get; set; } = "efectivo";
    public string? Concept { get; set; }
    public string? Notes { get; set; }
}

public class CreatePayrollRequest
{
    public int EmployeeId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public decimal? BaseSalary { get; set; }
    public decimal Bonuses { get; set; }
    public decimal Deductions { get; set; }
    public string? Notes { get; set; }
}

public class UpdatePayrollRequest
{
    public decimal? BaseSalary { get; set; }
    public decimal? Bonuses { get; set; }
    public decimal? Deductions { get; set; }
    public string? Notes { get; set; }
}

public class GeneratePayrollMonthRequest
{
    public int Year { get; set; }
    public int Month { get; set; }
}

public class MarkPayrollPaidRequest
{
    public int? AccountId { get; set; }
    public DateTime? PaidAt { get; set; }
}
