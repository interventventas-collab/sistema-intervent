using Api.Data;
using Api.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Api.Controllers;

/// <summary>
/// 2026-07-17: Base propia de clientes de MercadoLibre. Listado con buscador, detalle de compras,
/// actualizacion manual y exportacion a Excel. La base la mantiene al dia MeliClientesBackgroundService.
/// </summary>
[ApiController]
[Route("api/meli/clientes")]
[Authorize]
public class MeliClientesController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MeliClientesService _service;

    public MeliClientesController(AppDbContext db, MeliClientesService service) { _db = db; _service = service; }

    private IQueryable<Api.Models.MeliCliente> Filtrar(string? search, bool soloConTelefono)
    {
        var q = _db.MeliClientes.AsQueryable();
        if (soloConTelefono) q = q.Where(c => c.Phone != null && c.Phone != "");
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            q = q.Where(c =>
                (c.Nickname != null && EF.Functions.Like(c.Nickname, $"%{s}%")) ||
                (c.ReceiverName != null && EF.Functions.Like(c.ReceiverName, $"%{s}%")) ||
                (c.Phone != null && EF.Functions.Like(c.Phone, $"%{s}%")) ||
                (c.City != null && EF.Functions.Like(c.City, $"%{s}%")) ||
                (c.State != null && EF.Functions.Like(c.State, $"%{s}%")) ||
                (c.LastItems != null && EF.Functions.Like(c.LastItems, $"%{s}%")));
        }
        return q;
    }

    /// <summary>Listado paginado con buscador. Devuelve tambien los totales (global y con telefono).</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? search = null,
        [FromQuery] bool soloConTelefono = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50)
    {
        if (page < 1) page = 1;
        if (pageSize < 1 || pageSize > 500) pageSize = 50;

        var q = Filtrar(search, soloConTelefono);
        var total = await q.CountAsync();
        var totalGlobal = await _db.MeliClientes.CountAsync();
        var conTelefono = await _db.MeliClientes.CountAsync(c => c.Phone != null && c.Phone != "");

        var items = await q
            .OrderByDescending(c => c.LastPurchaseAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new
            {
                id = c.Id,
                buyerId = c.BuyerId,
                nickname = c.Nickname,
                receiverName = c.ReceiverName,
                phone = c.Phone,
                addressLine = c.AddressLine,
                neighborhood = c.Neighborhood,
                city = c.City,
                state = c.State,
                zipCode = c.ZipCode,
                ordersCount = c.OrdersCount,
                totalSpent = c.TotalSpent,
                firstPurchaseAt = c.FirstPurchaseAt,
                lastPurchaseAt = c.LastPurchaseAt,
                lastItems = c.LastItems
            })
            .ToListAsync();

        return Ok(new { total, totalGlobal, conTelefono, page, pageSize, items });
    }

    /// <summary>Historial de compras de un cliente.</summary>
    [HttpGet("{id:int}/compras")]
    public async Task<IActionResult> Compras(int id)
    {
        var compras = await _db.MeliClienteCompras
            .Where(c => c.MeliClienteId == id)
            .OrderByDescending(c => c.Fecha)
            .Select(c => new
            {
                meliOrderId = c.MeliOrderId,
                fecha = c.Fecha,
                items = c.Items,
                cantidad = c.Cantidad,
                total = c.Total,
                canal = c.Canal
            })
            .ToListAsync();
        return Ok(compras);
    }

    /// <summary>Actualiza la base ahora (suma las ventas nuevas). Tambien hace el backfill inicial.</summary>
    [HttpPost("sync")]
    public async Task<IActionResult> SyncNow()
    {
        var procesadas = await _service.SyncAsync();
        var totalClientes = await _db.MeliClientes.CountAsync();
        return Ok(new { procesadas, totalClientes });
    }

    /// <summary>Exporta a Excel todos los clientes que matchean el filtro.</summary>
    [HttpGet("export-excel")]
    public async Task<IActionResult> ExportExcel([FromQuery] string? search = null, [FromQuery] bool soloConTelefono = false)
    {
        var clientes = await Filtrar(search, soloConTelefono)
            .OrderByDescending(c => c.LastPurchaseAt)
            .ToListAsync();

        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Clientes MeLi");
        var headers = new[] { "Usuario (MeLi)", "Nombre", "Teléfono", "Dirección", "Barrio", "Ciudad", "Provincia", "CP",
                              "Compras", "Total gastado", "Primera compra", "Última compra", "Última compra (detalle)" };
        for (int i = 0; i < headers.Length; i++)
        {
            ws.Cell(1, i + 1).Value = headers[i];
            ws.Cell(1, i + 1).Style.Font.Bold = true;
            ws.Cell(1, i + 1).Style.Fill.BackgroundColor = XLColor.FromHtml("#1d4ed8");
            ws.Cell(1, i + 1).Style.Font.FontColor = XLColor.White;
        }

        int row = 2;
        foreach (var c in clientes)
        {
            ws.Cell(row, 1).Value = c.Nickname ?? "";
            ws.Cell(row, 2).Value = c.ReceiverName ?? "";
            ws.Cell(row, 3).Value = c.Phone ?? "";
            ws.Cell(row, 3).Style.NumberFormat.Format = "@"; // texto (no romper el 0 inicial)
            ws.Cell(row, 4).Value = c.AddressLine ?? "";
            ws.Cell(row, 5).Value = c.Neighborhood ?? "";
            ws.Cell(row, 6).Value = c.City ?? "";
            ws.Cell(row, 7).Value = c.State ?? "";
            ws.Cell(row, 8).Value = c.ZipCode ?? "";
            ws.Cell(row, 9).Value = c.OrdersCount;
            ws.Cell(row, 10).Value = c.TotalSpent;
            ws.Cell(row, 10).Style.NumberFormat.Format = "$#,##0.00";
            ws.Cell(row, 11).Value = c.FirstPurchaseAt?.ToLocalTime().ToString("dd/MM/yyyy") ?? "";
            ws.Cell(row, 12).Value = c.LastPurchaseAt?.ToLocalTime().ToString("dd/MM/yyyy") ?? "";
            ws.Cell(row, 13).Value = c.LastItems ?? "";
            row++;
        }
        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var nombre = $"clientes-meli-{DateTime.Now:yyyy-MM-dd-HHmm}.xlsx";
        return File(ms.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", nombre);
    }
}
