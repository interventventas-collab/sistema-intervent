using Api.Data;
using Api.DTOs;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

public class RoleService
{
    private readonly AppDbContext _db;

    public RoleService(AppDbContext db)
    {
        _db = db;
    }

    public async Task<List<RoleDto>> GetAllAsync()
    {
        return await _db.Roles
            .Include(r => r.Permissions)
            .OrderBy(r => r.Id)
            .Select(r => new RoleDto(
                r.Id,
                r.Name,
                r.Description,
                r.CreatedAt,
                r.Users.Count,
                r.Name == "admin"
                    ? MenuDefinition.AllMenuKeys
                    : r.Permissions.Select(p => p.MenuKey).ToList()
            ))
            .ToListAsync();
    }

    public async Task<RoleDto?> GetByIdAsync(int id)
    {
        return await _db.Roles
            .Include(r => r.Permissions)
            .Where(r => r.Id == id)
            .Select(r => new RoleDto(
                r.Id,
                r.Name,
                r.Description,
                r.CreatedAt,
                r.Users.Count,
                r.Name == "admin"
                    ? MenuDefinition.AllMenuKeys
                    : r.Permissions.Select(p => p.MenuKey).ToList()
            ))
            .FirstOrDefaultAsync();
    }

    public async Task<RoleDto?> CreateAsync(CreateRoleRequest request)
    {
        if (await _db.Roles.AnyAsync(r => r.Name == request.Name))
            return null;

        var role = new Role
        {
            Name = request.Name,
            Description = request.Description,
            CreatedAt = DateTime.UtcNow
        };

        _db.Roles.Add(role);
        await _db.SaveChangesAsync();

        // Add permissions
        var permissionKeys = request.Permissions ?? new List<string>();
        // Always include dashboard
        if (!permissionKeys.Contains("dashboard"))
            permissionKeys.Add("dashboard");

        var validKeys = permissionKeys.Where(k => MenuDefinition.AllMenuKeys.Contains(k)).ToList();
        foreach (var key in validKeys)
        {
            _db.RolePermissions.Add(new RolePermission { RoleId = role.Id, MenuKey = key });
        }
        await _db.SaveChangesAsync();

        return new RoleDto(role.Id, role.Name, role.Description, role.CreatedAt, 0, validKeys);
    }

    public async Task<RoleDto?> UpdateAsync(int id, UpdateRoleRequest request)
    {
        var role = await _db.Roles
            .Include(r => r.Users)
            .Include(r => r.Permissions)
            .FirstOrDefaultAsync(r => r.Id == id);
        if (role is null) return null;

        if (request.Name is not null && request.Name != role.Name)
        {
            if (await _db.Roles.AnyAsync(r => r.Name == request.Name && r.Id != id))
                return null;
            role.Name = request.Name;
        }

        if (request.Description is not null) role.Description = request.Description;

        // Update permissions (skip for admin - always has all)
        if (request.Permissions is not null && role.Name != "admin")
        {
            // Remove existing permissions
            _db.RolePermissions.RemoveRange(role.Permissions);

            // Add new permissions
            var permissionKeys = request.Permissions;
            if (!permissionKeys.Contains("dashboard"))
                permissionKeys.Add("dashboard");

            var validKeys = permissionKeys.Where(k => MenuDefinition.AllMenuKeys.Contains(k)).ToList();
            foreach (var key in validKeys)
            {
                _db.RolePermissions.Add(new RolePermission { RoleId = role.Id, MenuKey = key });
            }
        }

        await _db.SaveChangesAsync();

        var currentPermissions = role.Name == "admin"
            ? MenuDefinition.AllMenuKeys
            : await _db.RolePermissions.Where(rp => rp.RoleId == role.Id).Select(rp => rp.MenuKey).ToListAsync();

        return new RoleDto(role.Id, role.Name, role.Description, role.CreatedAt, role.Users.Count, currentPermissions);
    }

    public async Task<bool> DeleteAsync(int id)
    {
        var role = await _db.Roles.Include(r => r.Users).FirstOrDefaultAsync(r => r.Id == id);
        if (role is null) return false;

        // No permitir borrar roles que tienen usuarios asignados
        if (role.Users.Any()) return false;

        _db.Roles.Remove(role);
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<List<string>> GetPermissionsByRoleIdAsync(int roleId)
    {
        var role = await _db.Roles.FindAsync(roleId);
        if (role?.Name == "admin")
            return MenuDefinition.AllMenuKeys;

        return await _db.RolePermissions
            .Where(rp => rp.RoleId == roleId)
            .Select(rp => rp.MenuKey)
            .ToListAsync();
    }

    /// <summary>
    /// Ensures the admin role has all menu permissions from MenuDefinition.
    /// Called at app startup to auto-sync when new menu items are added.
    /// </summary>
    public async Task SyncAdminPermissionsAsync()
    {
        var adminRole = await _db.Roles.FirstOrDefaultAsync(r => r.Name == "admin");
        if (adminRole is null) return;

        var existingKeys = await _db.RolePermissions
            .Where(rp => rp.RoleId == adminRole.Id)
            .Select(rp => rp.MenuKey)
            .ToListAsync();

        var missingKeys = MenuDefinition.AllMenuKeys.Except(existingKeys).ToList();
        if (missingKeys.Count == 0) return;

        foreach (var key in missingKeys)
        {
            _db.RolePermissions.Add(new RolePermission { RoleId = adminRole.Id, MenuKey = key });
        }
        await _db.SaveChangesAsync();
    }
}
