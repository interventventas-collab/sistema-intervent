using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class EmployeeService
{
    private readonly AppDbContext _db;
    private const string CodePrefix = "EMP-";

    public EmployeeService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<EmployeeDto>> GetAllAsync()
    {
        return await _db.Employees
            .OrderBy(e => e.LastName).ThenBy(e => e.FirstName)
            .Select(e => Map(e))
            .ToListAsync();
    }

    public async Task<EmployeeDto?> GetByIdAsync(int id)
    {
        var e = await _db.Employees.FindAsync(id);
        return e is null ? null : Map(e);
    }

    public async Task<EmployeeDto> CreateAsync(CreateEmployeeRequest r)
    {
        var code = string.IsNullOrWhiteSpace(r.Code) ? await GenerateCodeAsync() : r.Code.Trim();
        if (await _db.Employees.AnyAsync(x => x.Code == code))
            throw new InvalidOperationException($"Ya existe un empleado con codigo '{code}'.");

        var e = new Employee
        {
            Code = code,
            FirstName = r.FirstName.Trim(),
            LastName = r.LastName.Trim(),
            Dni = N(r.Dni), Cuil = N(r.Cuil), Position = N(r.Position),
            HireDate = r.HireDate?.Date,
            BaseSalary = r.BaseSalary,
            Bank = N(r.Bank), Cbu = N(r.Cbu), Phone = N(r.Phone), Email = N(r.Email),
            Address = N(r.Address), Notes = N(r.Notes),
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };
        _db.Employees.Add(e);
        await _db.SaveChangesAsync();
        return Map(e);
    }

    public async Task<EmployeeDto?> UpdateAsync(int id, UpdateEmployeeRequest r)
    {
        var e = await _db.Employees.FindAsync(id);
        if (e is null) return null;
        if (r.Code is not null)
        {
            var nc = r.Code.Trim();
            if (nc != e.Code && await _db.Employees.AnyAsync(x => x.Id != id && x.Code == nc))
                throw new InvalidOperationException($"Ya existe un empleado con codigo '{nc}'.");
            e.Code = nc;
        }
        if (r.FirstName is not null) e.FirstName = r.FirstName.Trim();
        if (r.LastName is not null) e.LastName = r.LastName.Trim();
        if (r.Dni is not null) e.Dni = N(r.Dni);
        if (r.Cuil is not null) e.Cuil = N(r.Cuil);
        if (r.Position is not null) e.Position = N(r.Position);
        if (r.HireDate.HasValue) e.HireDate = r.HireDate.Value.Date;
        if (r.BaseSalary.HasValue) e.BaseSalary = r.BaseSalary.Value;
        if (r.Bank is not null) e.Bank = N(r.Bank);
        if (r.Cbu is not null) e.Cbu = N(r.Cbu);
        if (r.Phone is not null) e.Phone = N(r.Phone);
        if (r.Email is not null) e.Email = N(r.Email);
        if (r.Address is not null) e.Address = N(r.Address);
        if (r.Notes is not null) e.Notes = N(r.Notes);
        if (r.IsActive.HasValue) e.IsActive = r.IsActive.Value;
        e.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return Map(e);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var e = await _db.Employees.FindAsync(id);
        if (e is null) return false;
        _db.Employees.Remove(e);
        await _db.SaveChangesAsync();
        return true;
    }

    private async Task<string> GenerateCodeAsync()
    {
        var existing = await _db.Employees
            .Where(e => e.Code.StartsWith(CodePrefix))
            .Select(e => e.Code).ToListAsync();
        int max = 0;
        foreach (var code in existing)
        {
            var num = code.Substring(CodePrefix.Length);
            if (int.TryParse(num, out var n) && n > max) max = n;
        }
        return $"{CodePrefix}{(max + 1):D3}";
    }

    private static string? N(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();

    private static EmployeeDto Map(Employee e) => new EmployeeDto(
        e.Id, e.Code, e.FirstName, e.LastName, $"{e.LastName}, {e.FirstName}",
        e.Dni, e.Cuil, e.Position, e.HireDate, e.BaseSalary,
        e.Bank, e.Cbu, e.Phone, e.Email, e.Address, e.Notes,
        e.IsActive, e.CreatedAt, e.UpdatedAt);
}
