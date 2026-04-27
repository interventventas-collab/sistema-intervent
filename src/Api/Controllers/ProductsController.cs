using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ProductsController : ControllerBase
{
    private readonly ProductService _productService;

    public ProductsController(ProductService productService)
    {
        _productService = productService;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var products = await _productService.GetAllAsync();
        return Ok(products);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var product = await _productService.GetByIdAsync(id);
        if (product is null) return NotFound(new { message = "Producto no encontrado" });
        return Ok(product);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProductRequest request)
    {
        var product = await _productService.CreateAsync(request);
        if (product is null) return BadRequest(new { message = "Error al crear el producto" });
        return Created("/api/products/" + product.Id, product);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateProductRequest request)
    {
        var product = await _productService.UpdateAsync(id, request);
        if (product is null) return NotFound(new { message = "Producto no encontrado" });
        return Ok(product);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var result = await _productService.DeleteAsync(id);
        if (!result) return NotFound(new { message = "Producto no encontrado" });
        return NoContent();
    }

    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkProductIdsRequest request)
    {
        if (request.Ids == null || !request.Ids.Any())
            return BadRequest(new { message = "No se enviaron IDs" });

        var deleted = await _productService.BulkDeleteAsync(request.Ids);
        return Ok(new { deleted });
    }

    [HttpPut("bulk-toggle-status")]
    public async Task<IActionResult> BulkToggleStatus([FromBody] BulkToggleStatusRequest request)
    {
        if (request.Ids == null || !request.Ids.Any())
            return BadRequest(new { message = "No se enviaron IDs" });

        var updated = await _productService.BulkToggleStatusAsync(request.Ids, request.IsActive);
        return Ok(new { updated });
    }
}
