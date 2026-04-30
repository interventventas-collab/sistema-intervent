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
    private readonly BulkImportService _import;

    public ProductsController(ProductService productService, BulkImportService import)
    {
        _productService = productService;
        _import = import;
    }

    [HttpGet("import-template")]
    public IActionResult Template()
    {
        var bytes = _import.BuildProductTemplate();
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "productos-template.xlsx");
    }

    // Plantilla especifica para productos base (sin columna producto_base_sku)
    [HttpGet("base-import-template")]
    public IActionResult BaseTemplate()
    {
        var bytes = _import.BuildBaseProductTemplate();
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "productos-base-template.xlsx");
    }

    [HttpPost("bulk-import")]
    public async Task<IActionResult> BulkImport(IFormFile file)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Subi un archivo .xlsx" });
        try
        {
            using var stream = file.OpenReadStream();
            var result = await _import.ImportProductsAsync(stream);
            return Ok(result);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    // Importacion para productos base: marca IsBase=true en cada producto creado.
    [HttpPost("base-bulk-import")]
    public async Task<IActionResult> BaseBulkImport(IFormFile file)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Subi un archivo .xlsx" });
        try
        {
            using var stream = file.OpenReadStream();
            var result = await _import.ImportProductsAsync(stream, markAsBase: true);
            return Ok(result);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    // ===== IMPORT POR OEM (codigo del proveedor) =====
    [HttpGet("import-by-oem-template")]
    public IActionResult OemTemplate()
    {
        var bytes = _import.BuildProductsByOemTemplate();
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "productos-por-oem-template.xlsx");
    }

    [HttpPost("bulk-import-by-oem")]
    public async Task<IActionResult> BulkImportByOem(IFormFile file,
        [FromQuery] bool createIfMissing = true,
        [FromQuery] bool loadAsInactive = false)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Subi un archivo .xlsx" });
        try
        {
            using var stream = file.OpenReadStream();
            var result = await _import.ImportProductsByOemAsync(stream, createIfMissing, loadAsInactive);
            return Ok(result);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    /// <summary>Re-vincula publicaciones ML huerfanas a productos con SKU u OEM coincidente.</summary>
    [HttpPost("relink-meli-orphans")]
    public async Task<IActionResult> RelinkMeliOrphans()
    {
        try
        {
            var report = await _import.RelinkOrphanMeliItemsExactAsync();
            return Ok(report);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
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

    /// <summary>Crea una variedad nueva de cafe (padre + 3 hijos 1kg/500g/250g) en una sola llamada.</summary>
    [HttpPost("coffee-variety")]
    public async Task<IActionResult> CreateCoffeeVariety([FromBody] CreateCoffeeVarietyRequest request)
    {
        try { return Ok(await _productService.CreateCoffeeVarietyAsync(request)); }
        catch (InvalidOperationException ex) { return BadRequest(new { error = ex.Message }); }
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
