using System.ComponentModel.DataAnnotations;

namespace Api.DTOs;

public record EmployeeDto(
    int Id,
    string Code,
    string FirstName,
    string LastName,
    string FullName,
    string? Dni,
    string? Cuil,
    string? Position,
    DateTime? HireDate,
    decimal BaseSalary,
    string? Bank,
    string? Cbu,
    string? Phone,
    string? Email,
    string? Address,
    string? Notes,
    bool IsActive,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreateEmployeeRequest(
    [MaxLength(30)] string? Code,
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    [MaxLength(20)] string? Dni,
    [MaxLength(20)] string? Cuil,
    [MaxLength(100)] string? Position,
    DateTime? HireDate,
    decimal BaseSalary,
    [MaxLength(100)] string? Bank,
    [MaxLength(30)] string? Cbu,
    [MaxLength(50)] string? Phone,
    [EmailAddress, MaxLength(255)] string? Email,
    [MaxLength(500)] string? Address,
    string? Notes
);

public record UpdateEmployeeRequest(
    [MaxLength(30)] string? Code,
    [MaxLength(100)] string? FirstName,
    [MaxLength(100)] string? LastName,
    [MaxLength(20)] string? Dni,
    [MaxLength(20)] string? Cuil,
    [MaxLength(100)] string? Position,
    DateTime? HireDate,
    decimal? BaseSalary,
    [MaxLength(100)] string? Bank,
    [MaxLength(30)] string? Cbu,
    [MaxLength(50)] string? Phone,
    [EmailAddress, MaxLength(255)] string? Email,
    [MaxLength(500)] string? Address,
    string? Notes,
    bool? IsActive
);

public record PayrollDto(
    int Id,
    int EmployeeId,
    string EmployeeFullName,
    string? EmployeeCode,
    int Year,
    int Month,
    decimal BaseSalary,
    decimal Bonuses,
    decimal Deductions,
    decimal GrossTotal,
    decimal NetTotal,
    string? Notes,
    bool IsPaid,
    DateTime? PaidAt,
    int? PaidFromAccountId,
    string? PaidFromAccountName,
    DateTime CreatedAt,
    DateTime? UpdatedAt
);

public record CreatePayrollRequest(
    [Required] int EmployeeId,
    [Range(2000, 2100)] int Year,
    [Range(1, 12)] int Month,
    decimal? BaseSalary,
    decimal Bonuses,
    decimal Deductions,
    [MaxLength(500)] string? Notes
);

public record UpdatePayrollRequest(
    decimal? BaseSalary,
    decimal? Bonuses,
    decimal? Deductions,
    [MaxLength(500)] string? Notes
);

public record GeneratePayrollMonthRequest(
    [Range(2000, 2100)] int Year,
    [Range(1, 12)] int Month
);

public record MarkPayrollPaidRequest(
    int? AccountId,
    DateTime? PaidAt
);
