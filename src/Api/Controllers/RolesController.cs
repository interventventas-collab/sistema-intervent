using System.Security.Claims;
using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class RolesController : ControllerBase
{
    private readonly RoleService _roleService;

    public RolesController(RoleService roleService)
    {
        _roleService = roleService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var roles = await _roleService.GetAllAsync();
        return Ok(roles);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var role = await _roleService.GetByIdAsync(id);
        if (role is null) return NotFound(new { message = "Rol no encontrado" });
        return Ok(role);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRoleRequest request)
    {
        if (!IsAdmin()) return Forbid();

        var role = await _roleService.CreateAsync(request);
        if (role is null) return Conflict(new { message = "Ya existe un rol con ese nombre" });
        return Created($"/api/roles/{role.Id}", role);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateRoleRequest request)
    {
        if (!IsAdmin()) return Forbid();

        var role = await _roleService.UpdateAsync(id, request);
        if (role is null) return NotFound(new { message = "Rol no encontrado o nombre duplicado" });
        return Ok(role);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!IsAdmin()) return Forbid();

        var result = await _roleService.DeleteAsync(id);
        if (!result) return BadRequest(new { message = "No se puede eliminar un rol que tiene usuarios asignados" });
        return NoContent();
    }

    [HttpGet("menu-tree")]
    public IActionResult GetMenuTree()
    {
        var tree = MenuDefinition.MenuTree.Select(g => new MenuTreeDto(
            g.GroupKey,
            g.Label,
            g.Items.Select(i => new MenuItemDto(i.Key, i.Label, i.Route)).ToList()
        )).ToList();
        return Ok(tree);
    }

    private bool IsAdmin()
    {
        return User.FindFirst(ClaimTypes.Role)?.Value == "admin";
    }
}
