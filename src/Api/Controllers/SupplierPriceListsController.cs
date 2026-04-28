using Api.DTOs;
using Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Api.Controllers;

[ApiController]
[Route("api/price-lists")]
[Authorize]
public class SupplierPriceListsController : ControllerBase
{
    private readonly SupplierPriceListService _service;

    public SupplierPriceListsController(SupplierPriceListService service)
    {
        _service = service;
    }

    // ===== Listas =====

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await _service.GetAllAsync());

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(int id)
    {
        var l = await _service.GetByIdAsync(id);
        if (l is null) return NotFound(new { error = "Lista no encontrada" });
        return Ok(l);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateSupplierPriceListRequest r)
    {
        try { return Ok(await _service.CreateAsync(r)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateSupplierPriceListRequest r)
    {
        try
        {
            var u = await _service.UpdateAsync(id, r);
            if (u is null) return NotFound(new { error = "Lista no encontrada" });
            return Ok(u);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var ok = await _service.DeleteAsync(id);
        if (!ok) return NotFound(new { error = "Lista no encontrada" });
        return Ok(new { deleted = true });
    }

    // ===== Items =====

    [HttpGet("{id}/items")]
    public async Task<IActionResult> GetItems(int id, [FromQuery] string? search = null)
        => Ok(await _service.GetItemsAsync(id, search));

    [HttpPost("{id}/items")]
    public async Task<IActionResult> AddItem(int id, [FromBody] CreatePriceListItemRequest r)
    {
        try { return Ok(await _service.AddItemAsync(id, r)); }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpPut("items/{itemId}")]
    public async Task<IActionResult> UpdateItem(int itemId, [FromBody] UpdatePriceListItemRequest r)
    {
        try
        {
            var u = await _service.UpdateItemAsync(itemId, r);
            if (u is null) return NotFound(new { error = "Item no encontrado" });
            return Ok(u);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpDelete("items/{itemId}")]
    public async Task<IActionResult> DeleteItem(int itemId)
    {
        var ok = await _service.DeleteItemAsync(itemId);
        if (!ok) return NotFound(new { error = "Item no encontrado" });
        return Ok(new { deleted = true });
    }

    // ===== Importacion masiva =====

    [HttpPost("{id}/import")]
    public async Task<IActionResult> Import(int id, IFormFile file)
    {
        if (file is null || file.Length == 0) return BadRequest(new { error = "Subi un archivo .xlsx" });
        try
        {
            using var stream = file.OpenReadStream();
            var result = await _service.ImportExcelAsync(id, stream);
            return Ok(result);
        }
        catch (Exception ex) { return BadRequest(new { error = ex.Message }); }
    }

    [HttpGet("import-template")]
    public IActionResult Template()
    {
        // Plantilla minima
        using var workbook = new ClosedXML.Excel.XLWorkbook();
        var ws = workbook.AddWorksheet("Lista");
        var headers = new[] { "codigo", "descripcion", "costo", "pvp_sugerido", "notas" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
            ws.Cell(1, i + 1).Style.Fill.BackgroundColor = ClosedXML.Excel.XLColor.LightGray;
        }
        ws.Cell(2, 1).Value = "9311"; ws.Cell(2, 2).Value = "Caja Plastica 10L";
        ws.Cell(2, 3).Value = 1500.00; ws.Cell(2, 4).Value = 2999.99;
        ws.Cell(2, 5).Value = "ejemplo (borrar antes de importar)";
        ws.Cell(2, 1).Style.Font.Italic = true; ws.Cell(2, 1).Style.Font.FontColor = ClosedXML.Excel.XLColor.Gray;
        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "lista-precios-template.xlsx");
    }
}
