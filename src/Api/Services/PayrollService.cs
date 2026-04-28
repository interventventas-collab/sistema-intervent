using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class PayrollService
{
    private readonly AppDbContext _db;
    private readonly TreasuryService _treasury;

    public PayrollService(AppDbContext db, TreasuryService treasury)
    {
        _db = db;
        _treasury = treasury;
    }

    public async Task<List<PayrollDto>> GetAllAsync(int? employeeId = null, int? year = null, int? month = null)
    {
        var q = _db.Payrolls.Include(p => p.Employee).Include(p => p.PaidFromAccount).AsQueryable();
        if (employeeId.HasValue) q = q.Where(p => p.EmployeeId == employeeId.Value);
        if (year.HasValue) q = q.Where(p => p.Year == year.Value);
        if (month.HasValue) q = q.Where(p => p.Month == month.Value);

        var list = await q.OrderByDescending(p => p.Year).ThenByDescending(p => p.Month)
            .ThenBy(p => p.Employee!.LastName).ThenBy(p => p.Employee!.FirstName)
            .ToListAsync();
        return list.Select(Map).ToList();
    }

    public async Task<PayrollDto?> GetByIdAsync(int id)
    {
        var p = await _db.Payrolls.Include(x => x.Employee).Include(x => x.PaidFromAccount)
            .FirstOrDefaultAsync(x => x.Id == id);
        return p is null ? null : Map(p);
    }

    public async Task<PayrollDto> CreateAsync(CreatePayrollRequest r)
    {
        var emp = await _db.Employees.FindAsync(r.EmployeeId)
            ?? throw new InvalidOperationException("Empleado no encontrado.");
        if (await _db.Payrolls.AnyAsync(p => p.EmployeeId == emp.Id && p.Year == r.Year && p.Month == r.Month))
            throw new InvalidOperationException($"Ya existe una liquidacion para {emp.LastName}, {emp.FirstName} en {r.Year}/{r.Month:D2}.");

        var baseSalary = r.BaseSalary ?? emp.BaseSalary;
        var gross = baseSalary + r.Bonuses;
        var net = gross - r.Deductions;

        var p = new Payroll
        {
            EmployeeId = emp.Id,
            Year = r.Year, Month = r.Month,
            BaseSalary = baseSalary,
            Bonuses = r.Bonuses, Deductions = r.Deductions,
            GrossTotal = gross, NetTotal = net,
            Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes,
            IsPaid = false,
            CreatedAt = DateTime.UtcNow
        };
        _db.Payrolls.Add(p);
        await _db.SaveChangesAsync();
        return (await GetByIdAsync(p.Id))!;
    }

    public async Task<PayrollDto?> UpdateAsync(int id, UpdatePayrollRequest r)
    {
        var p = await _db.Payrolls.FindAsync(id);
        if (p is null) return null;
        if (p.IsPaid) throw new InvalidOperationException("No se puede modificar una liquidacion ya pagada. Desmarcala como pagada primero.");

        if (r.BaseSalary.HasValue) p.BaseSalary = r.BaseSalary.Value;
        if (r.Bonuses.HasValue) p.Bonuses = r.Bonuses.Value;
        if (r.Deductions.HasValue) p.Deductions = r.Deductions.Value;
        if (r.Notes is not null) p.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes;
        p.GrossTotal = p.BaseSalary + p.Bonuses;
        p.NetTotal = p.GrossTotal - p.Deductions;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetByIdAsync(p.Id);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var p = await _db.Payrolls.FindAsync(id);
        if (p is null) return false;
        if (p.IsPaid) throw new InvalidOperationException("No se puede eliminar una liquidacion pagada.");
        _db.Payrolls.Remove(p);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<int> GenerateMonthAsync(GeneratePayrollMonthRequest r)
    {
        var employees = await _db.Employees.Where(e => e.IsActive).ToListAsync();
        int created = 0;
        foreach (var e in employees)
        {
            if (await _db.Payrolls.AnyAsync(p => p.EmployeeId == e.Id && p.Year == r.Year && p.Month == r.Month))
                continue;
            var p = new Payroll
            {
                EmployeeId = e.Id, Year = r.Year, Month = r.Month,
                BaseSalary = e.BaseSalary,
                Bonuses = 0, Deductions = 0,
                GrossTotal = e.BaseSalary, NetTotal = e.BaseSalary,
                IsPaid = false,
                CreatedAt = DateTime.UtcNow
            };
            _db.Payrolls.Add(p);
            created++;
        }
        await _db.SaveChangesAsync();
        return created;
    }

    public async Task<PayrollDto?> MarkPaidAsync(int id, MarkPayrollPaidRequest r)
    {
        var p = await _db.Payrolls.Include(x => x.Employee).FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return null;
        if (p.IsPaid) return await GetByIdAsync(id);

        p.IsPaid = true;
        p.PaidAt = (r.PaidAt ?? DateTime.UtcNow);
        p.PaidFromAccountId = r.AccountId;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Si se especifico cuenta, registrar el egreso en tesoreria
        if (r.AccountId.HasValue && p.NetTotal > 0)
        {
            var concept = "Sueldo";
            var desc = $"{p.Year}/{p.Month:D2} - {p.Employee?.LastName}, {p.Employee?.FirstName}";
            await _treasury.RegisterEgresoAsync(r.AccountId.Value, p.NetTotal, concept, desc, p.EmployeeId);
        }

        return await GetByIdAsync(id);
    }

    public async Task<PayrollDto?> UnmarkPaidAsync(int id)
    {
        var p = await _db.Payrolls.FindAsync(id);
        if (p is null) return null;
        p.IsPaid = false;
        p.PaidAt = null;
        p.PaidFromAccountId = null;
        p.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return await GetByIdAsync(id);
    }

    private static PayrollDto Map(Payroll p) => new PayrollDto(
        p.Id, p.EmployeeId,
        p.Employee != null ? $"{p.Employee.LastName}, {p.Employee.FirstName}" : "?",
        p.Employee?.Code,
        p.Year, p.Month,
        p.BaseSalary, p.Bonuses, p.Deductions, p.GrossTotal, p.NetTotal,
        p.Notes, p.IsPaid, p.PaidAt, p.PaidFromAccountId, p.PaidFromAccount?.Name,
        p.CreatedAt, p.UpdatedAt);
}
